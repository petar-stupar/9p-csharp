# Mounting a 9P tree

A 9P server is reachable from any 9P client, and this repository ships one. Mounting the tree into
an operating system's own namespace — so that `ls`, `grep` and every other tool can reach it — is a
different question, and the answer depends entirely on the platform.

**Linux mounts 9P natively. macOS and Windows cannot mount it at all.** On those two the only route
is a bridge: a Linux container mounts the tree over 9P and re-exports it over SMB, which the host
then mounts. That path drives your server through a kernel client and a file-sharing server, and it
asks for things a hand-written 9P client never asks for. This page is what a server owes them.

Everything below was measured on an Apple M4 Pro running macOS 26.6.2 (Darwin 25.6.0) with Docker
Desktop on 2026-09-11, against `NineP.Server` 0.3.0. Where a claim is about the protocol rather
than that setup, it says so.

## Contents

- [Where you can mount](#where-you-can-mount)
- [The bridge](#the-bridge)
- [What your server must provide](#what-your-server-must-provide)
- [Mount options that are not optional](#mount-options-that-are-not-optional)
- [SMB caveats](#smb-caveats)
- [macOS gates network volumes per application](#macos-gates-network-volumes-per-application)
- [Checklist](#checklist)

## Where you can mount

| Platform | Native 9P client | What to do |
| --- | --- | --- |
| Linux | **yes** — `v9fs` in the mainline kernel | `mount -t 9p` directly; see below for the options |
| macOS | **no** | bridge through a Linux container |
| Windows | **no** | bridge through WSL2 or a container |

macOS ships `/sbin/mount_9p`, which is not a general 9P client: its usage is `mount_9p [-r] fs_tag`,
and it mounts a Virtualization.framework share **by tag, from inside a guest**. It cannot address a
TCP server. The other route, `9pfuse`, is not a Homebrew formula — it ships inside plan9port, which
is not in core either — so it means building plan9port from source against macFUSE, and macFUSE on
Apple Silicon requires booting to Recovery and lowering the machine to Reduced Security first.

Windows has no v9fs. WSL2 runs a real Linux kernel, so a mount inside WSL2 works and is reachable
from Windows through `\\wsl$`, which is the same bridge shape by another name.

## The bridge

Docker Desktop's kernel carries v9fs (`nodev 9p` in `/proc/filesystems` on `7.0.12-linuxkit`), so
this needs no kernel extension and no `sudo` on the host.

```
    host (macOS / Windows)          Linux container              your process
    ┌──────────────────┐           ┌──────────────────┐         ┌──────────────┐
    │  Finder, tools   │◄──SMB────►│  mount -t 9p     │◄──9P───►│ NineP.Server │
    │  mount_smbfs     │           │  smbd            │         │              │
    └──────────────────┘           └──────────────────┘         └──────────────┘
```

Inside the container:

```sh
# 1. Mount the 9P tree. trans=tcp takes an IP ADDRESS, never a host name.
mount -t 9p -o trans=tcp,port=5640,version=9p2000.L,uname=root,dfltuid=0,dfltgid=0,access=any \
      192.168.65.254 /mnt/tree

# 2. Re-export it. Do NOT use "read only = yes"; see SMB caveats below.
cat >> /etc/samba/smb.conf <<'CONF'
[tree]
   path = /mnt/tree
   browseable = yes
   guest ok = yes
   writeable = yes
CONF
smbd -F
```

On the host:

```sh
mount_smbfs //guest@localhost/tree /Volumes/tree     # macOS
net use T: \\localhost\tree                          # Windows
```

## What your server must provide

These are not optional extras. Each one presents as a broken tool rather than as a missing feature,
which is what makes them expensive to diagnose.

### 1. Answer `Tstatfs`

Samba calls `disk_free` when a client connects to a share. A server with no `IStatFsCapability` is
answered `EOPNOTSUPP` by the core, Samba fails the tree connect, and the host reports

```text
mount_smbfs: mount error: /Volumes/tree: Operation not supported
```

with no mount and nothing pointing at 9P. Only the Samba log says why:

```text
sys_disk_free: VFS disk_free failed. Error was : Not supported
```

The numbers need not be interesting. A generated tree can report one nominal block with none free;
what matters is that the message is answered at all.

```csharp
public sealed class MyTree : IDirectoryHandler, IStatFsCapability
{
    // type, blockSize, blocks, blocksFree, blocksAvailable, files, filesFree, fsId, nameLength
    public ValueTask<StatFs> StatFsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new StatFs(
            StatFs.V9fsMagic, 4096, 1, 0, 0, 1, 0, 1, (uint)Constants.MaxNameLength));
}
```

### 2. Give every file a read bit, even a write-only one

Samba asks for extended attributes when it opens a file, and `Txattrwalk` requires read permission
on the file before the server is consulted for an `IXattrHandler`. So a control file at mode `0222`
— the Plan 9 convention for write-only — is refused `EACCES` on **open**, and the user sees
`permission denied` on a shell redirect that never reached your handler.

Serve anything meant to be written through a bridge at `0666` or `0644`, whether or not reading it
means anything. Returning an empty read, or a line of help text, costs nothing and keeps the file
openable.

### 3. Accept a `Tsetattr` that carries a size and a time

`>` opens with `O_TRUNC`, and v9fs sends that as one `Tsetattr` carrying the size **and** an
mtime with no explicit value — "stamp it from your own clock". A handler that refuses every
`SetAttrAsync`, which is the obvious thing to write for a synthetic tree, fails the open before a
byte is written.

```csharp
public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default)
{
    if (update.Size is 0)
    {
        _buffer.Clear();     // a truncate is how a redirect starts
    }

    // Times this tree does not keep are accepted and ignored rather than refused: refusing one
    // fails the open that carried it.
    return ValueTask.CompletedTask;
}
```

The core's own half of this was a defect and is fixed as of 0.4.0: stamping a time from the
server's clock is granted on write permission rather than ownership (reference §8 rule 43). Before
that fix, a default v9fs mount could not truncate any file at all.

### 4. State an owner, or leave it unstated and let the library handle it

`Attr.Uid` and `Attr.Gid` default to `Constants.NONUNAME` — "this handler did not state an owner".
9P2000 and `.u` can say that, because ownership travels there as a *name* with the number beside it
optional. **`.L` cannot**: `uid` and `gid` are plain required numbers with no sentinel, and
`0xFFFFFFFF` is `(uid_t)-1`, which Linux refuses to map. It shows every file as `nobody` and
answers `EOVERFLOW` to any operation needing the real owner:

```text
-r--r--r--  1 nobody nobody  12705 Compare.md

rm: can't remove 'Compare.md': Value too large for data type
```

The client decides that locally, so no request reaches your server, nothing appears in a log, and
there is no error you can improve. As of 0.4.0 the library coerces the sentinel to `0` at the `.L`
projection boundary (reference §8 rule 42), so an unstated owner is merely uninteresting rather
than unusable. Stating a real `Uid`/`Gid` is still better, and is required if you want ownership to
mean anything on the mount.

## Mount options that are not optional

**`uname=` and `dfltuid=`.** v9fs attaches as `uname=nobody` with uid `-1` unless told otherwise,
and an identity of `-1` can never equal any owner. With default options **no client owns anything**
on the mount, and every ownership-gated operation fails. Set them to match what your server reports
as the owner:

```
uname=root,dfltuid=0,dfltgid=0
```

**`access=any`.** Needed when a service such as `smbd` reads the mount as a different local user
than the one that mounted it. Without it the mount is visible only to the mounting uid, and Samba
reports permission errors on a tree it can see.

**`trans=tcp` takes an IP address as the device name.** Given a host name it fails:

```text
mount: mounting my-server on /mnt/tree failed: Invalid argument
```

which reads as a bad mount *option* rather than a bad *device*, and sends you to the wrong half of
the command line. Resolve the name yourself and pass the address.

**`version=9p2000.L`** unless you have a reason not to. It is the dialect v9fs exercises most and
the one the POSIX-shaped operations live in.

## SMB caveats

**Never export the share `read only = yes`.** Samba strips the write bits from **everything** it
serves, so a control file is unwritable however your 9P server presents it, and the failure looks
like a server bug. Read-only belongs in the 9P permissions, where your server enforces it and can
say why.

**Guest access needs `guest ok = yes` on the share and a `map to guest` setting Samba honours.** A
share that prompts for credentials from `mount_smbfs` usually means the guest mapping did not take.

**SMB caches attributes.** A synthetic file whose contents change on every read — a status file, a
counter — may be served from cache. Mount with caching disabled on the host if freshness matters.

## macOS gates network volumes per application

This one is not a bug in anything, and it costs an afternoon.

macOS asks each application for permission to access network volumes:
**System Settings → Privacy & Security → Files and Folders → Network Volumes**. A terminal launched
from the Finder is prompted once and remembers. A terminal started by *another program* — an IDE, a
build tool, an agent — is refused `EPERM` with **no prompt and no log entry**, so a correct mount
reads as a broken one. This was confirmed as macOS behaviour rather than a sandbox by reproducing
it with sandboxing disabled.

If a mount works from one terminal and not another, check this before anything else.

## Checklist

Before you mount a tree through a bridge:

- [ ] The root handler implements `IStatFsCapability`.
- [ ] Every file meant to be written is served with a read bit set.
- [ ] `SetAttrAsync` accepts a size-and-time update rather than refusing it.
- [ ] `Attr.Uid` and `Attr.Gid` are set, or you accept that the mount shows `nobody`.
- [ ] The mount carries `uname=`, `dfltuid=`, `dfltgid=` matching the owner your server reports.
- [ ] The mount carries `access=any` if another local user will read it.
- [ ] `trans=tcp` is given an IP address, not a host name.
- [ ] The SMB share is **not** `read only = yes`.
- [ ] On macOS, the terminal you are using has Network Volumes permission.

## See also

- [docs/server.md](server.md) — the handler table and the optional capabilities
- [docs/interop.md](interop.md) — what this implementation has been run against
- [docs/protocol.md](protocol.md) — the dialects and what each one can carry
