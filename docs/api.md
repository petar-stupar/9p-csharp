# The reference API

This document describes the complete public surface of the three published packages —
`NineP.Protocol`, `NineP.Client` and `NineP.Server` — as the C# implementation ships it. It is the
reference API the owner reviewed on 2026-09-08 and the thirteen other languages mirror (workspace
architecture §12), so it is written to be read front to back rather than looked up: names first, then every member with its exact C# signature and what it
is for.

It is maintained against `PublicAPI.Shipped.txt` in each of the three projects, which the
`PublicApiAnalyzers` produce mechanically from the compiled assemblies. Everything those files list
appears here and nothing else does; `RepoHygieneTests.ApiDocMatchesPublicSurface` reflects over the
packed assemblies and fails the build if that stops being true.

Reference sections cited as §n are sections of
[docs/9p/protocol-reference.md](9p/protocol-reference.md); architecture sections are of
[docs/9p/ARCHITECTURE.md](9p/ARCHITECTURE.md). Spec §5 is the normative contract this document
renders.

## Status: owner-reviewed, frozen at `0.1.0`

The owner reviewed this surface on 2026-09-08; the decisions are recorded in workspace architecture
§12 and its Decision Log, and every change they asked for is applied here (improvement requests
IR-4 … IR-8: protocol verbs on both sides, sentinel ids and one errno type, no client clock, the
payload-lifetime rule on the members that alias a frame, no message-boundaries flag). The surface
is **frozen**: every line of every
`PublicAPI.Unshipped.txt` has been moved into the matching `PublicAPI.Shipped.txt`, so an addition
or a removal is now an explicit, reviewable edit to a shipped file rather than a silent one.
`RepoHygieneTests.PublicApiFileIsUpToDate` asserts the unshipped files are empty and the shipped
ones cover every exported type; `RS0016` and `RS0017` are build errors, so neither file can fall
behind the compiler.

Eleven members are public here that spec §5's listings do not spell out. None of them is a new type —
the inventory below is exact. Seven are constants or helpers the implementation needed; four are
additions the owner approved:

| Member | Why it exists |
| --- | --- |
| `DirEntry.KindOf(byte)` | the inverse of `DirEntry.DirentType`, which §5.1.2 specifies; a reader of a raw dirent needs both directions |
| `PasswordFileStore.Algorithm`, `PasswordFileStore.MinimumIterations` | the hash name and the 600 000-iteration floor of S-15, so a deployment can check what its own file holds |
| `TlsClientCertAuthenticator.DefaultMapper` | the SAN/CN mapping, exposed so a custom mapper can fall back to it instead of reimplementing it |
| `ClientOptions.DefaultLinuxMsize`, `ClientOptions.DefaultLegacyMsize` | the two msize defaults §5.7 states in prose |
| `NinePServer.ListeningAsync` | §5.8 documents `Endpoints` as valid once serving has started and gave no way to know when that is; without it the README's own example would have had to poll |
| `Identity.IsAuthenticated` | false only for the anonymous identity a `NOFID` attach gets, so a filesystem can tell without comparing to a sentinel; kept by the owner 2026-09-08 |
| `TcpTransportOptions.Logger` | the plain TCP transport logs like the TLS and WebSocket ones, so the three option types are symmetric; kept by the owner 2026-09-08 |
| `ClientOptions.MaxReadAll` | whole-value cap, default 256 MiB; approved with the additional edge cases on 2026-09-08 |
| `ClientOptions.DisposeTimeout` | the total bound on the clunks a session disposal issues, so a dead peer cannot hang disposal; kept by the owner 2026-09-08 |

The implicit interface implementations — every concrete transport's `Schemes` / `ConnectAsync` /
`ListenAsync`, every authenticator's `IsRequired` / `BeginAsync`, every credential's
`AuthenticateAsync` — are public because C# has no other way to implement a public interface
member on a public type. They are listed under their types below and add nothing a port has to
decide.

## How to port this

Eight rules govern the shape of the API. They are not style advice: each one is enforced by a test
or an analyser here, and a port that breaks one produces an API that reads differently from the
other thirteen. Workspace architecture §12 restates them together with the owner's decisions of
2026-09-08 — optional ids as `NONUNAME` sentinels, errno a signed 32-bit value, positional message
records, generic-by-type decode, the payload-lifetime rule, no message-boundaries flag — and where
this document and §12 disagree, §12 wins and this document gets fixed.

**1. Named after the protocol, not after the language.** Message types keep their 9P names verbatim
(`Tversion`, `Rwalk`, `Twrite`). Constants keep their `fcall.h` / `linux-9p.h` names (`NOTAG`,
`NOFID`, `NONUNAME`, `MAXWELEM`, `IOHDRSZ`, `ERRMAX`, `STATFIXLEN`) even where the host language's
convention would rename them. Handler methods keep the verbs of architecture §12 rule 1, the same on
the client and the server — `Lookup`, `ReadDir`, `Create`, `Remove`, `Rename`, `Link`, `Open`,
`Read`, `Write`, `Readlink`, `GetAttr`, `SetAttr`. A reader who knows 9P must
recognise the API without a glossary.

**2. Symmetric client and server.** `NinePClient` faces `NinePServer`, `ClientOptions` faces
`ServerOptions`, and `ICredential` (client) mirrors `IAuthenticator` (server) exchange for
exchange; `ITransport` and `DirEntry` are shared by both. A reader must be able to guess the server
name from the client name.

**3. Minimal surface, internal by default.** Spec §5.9 lists every internal type, and none of them
is reachable from a public signature. The codec's streaming reader and writer, the fid and tag
tables, the dispatcher, the projectors and the packers are all internal; the test assemblies see
them only through `InternalsVisibleTo`. In a port, whatever is not in the inventory below is
private to the implementation.

**4. No leaked internals in signatures.** No public member exposes `PipeReader`, `PipeWriter`,
`ReadOnlySequence<byte>`, `ArrayPool<byte>`, `SemaphoreSlim`, `Channel<T>`, `Socket`, `SslStream`,
`WebSocket`, `SocketsHttpHandler` or `SqliteConnection`. The public payload currency is
`ReadOnlyMemory<byte>` and `Memory<byte>`; the public async currency is `ValueTask` /
`ValueTask<T>` with a trailing `CancellationToken cancellationToken = default`; every awaiting
member ends in `Async`. Ports substitute their own equivalents — a borrowed byte view, the
ecosystem's future type, the ecosystem's cancellation token — but the rule is the same: the buffer
strategy must not be visible.

**5. One error model.** `NinePError` is the value — an ename plus an errno, projected to `Rerror`,
`Rerror`+errno or `Rlerror` by the session dialect. `NinePException` is the base of everything the
library throws, with `NinePProtocolException` and `NinePVersionException` deriving from it. Nothing
else escapes a public method except the `ArgumentException` family, `ObjectDisposedException` and
`OperationCanceledException`.

**6. No public mutable static state, no public setters after construction.** `ClientOptions`,
`ServerOptions`, `Limits` and the transport options records are `sealed record`s whose members are
`init`-only, and every default is documented on the member itself. There is no global logger, no
ambient clock and no settable singleton anywhere in the surface.

**7. Every public member carries a doc comment** naming the reference section it implements where
one exists. `CS1591` (missing XML comment) is an error in this build, so the doc comment is not
optional; the one-line summaries reproduced in this document are those comments.

**8. The description is tested against the surface.** `RepoHygieneTests.ApiDocMatchesPublicSurface`
asserts that every public type and member appears in this document and that this document names
nothing that is not public, and `RepoHygieneTests.PublicTypeCountMatchesSpec` asserts the type
counts of the inventory below.

### Record boilerplate

C# `record` and `readonly record struct` declarations generate a fixed set of value-semantics
members. They are part of the public surface and they are listed once here rather than repeated
under each of the eighty-odd records in this document:

```csharp
// Every record and readonly record struct:
public override bool Equals(object? obj);
public bool Equals(T? other);              // T for a struct record, T? for a class record
public override int GetHashCode();
public override string ToString();
public static bool operator ==(T left, T right);
public static bool operator !=(T left, T right);

// Every readonly record struct additionally:
public T();                                // the parameterless struct constructor

// Every record declared with a positional parameter list additionally:
public T(...);                             // the positional constructor, given per type below
public void Deconstruct(out ...);          // one out parameter per positional member

// Every record class (sealed record) additionally:
public T <Clone>$();                       // the compiler-generated copy constructor behind `with`
```

A record declared with `init`-only properties rather than a positional list — `Attr`, `SetAttr`,
`Limits`, `StatRecord`, `PeerIdentity`, the four options records, `Identity`, `CreateRequest`,
`ServerCounters` and `ServerOptions` — has no `Deconstruct` and no positional constructor, only the
parameterless one. In a port, the equivalent is value equality, a hash, a readable rendering, and a
copy-with-changes operation; the exact member names are C#'s.

Positional record parameters are properties: `Twalk.Tag`, `Twalk.Fid`, `Twalk.NewFid` and
`Twalk.Wnames` are all `get`/`init` properties, and this document does not list them again beneath
the positional signature that declares them.

## Type inventory

Spec §5.10. `RepoHygieneTests.PublicTypeCountMatchesSpec` reflects over the three packed assemblies
and asserts these numbers exactly, so a type added or removed without updating this table fails the
build.

| Namespace | Types | The types |
| --- | --- | --- |
| `NineP.Protocol` | 30 | `Dialect`, `MessageType`, `QidType`, `Qid`, `FileKind`, `FileFlags`, `OpenMode`, `OpenFlags`, `MessageTypes`, `TimeSpec`, `DeviceId`, `Attr`, `SetAttr`, `DirEntry`, `StatFs`, `LockType`, `LockFlags`, `LockStatus`, `LockRequest`, `LockQueryResult`, `XattrFlags`, `NinePError`, `NinePException`, `NinePProtocolException`, `NinePVersionException`, `ProtocolErrorKind`, `Constants`, `Errno`, `ErrorTable`, `Limits` |
| `NineP.Protocol.Messages` | 70 | `IMessage`, `StatRecord`, `GetAttrMask`, `SetAttrMask`, and the 66 message records |
| `NineP.Protocol.Codec` | 1 | `MessageCodec` |
| `NineP.Protocol.Negotiation` | 2 | `NegotiationResult`, `Negotiator` |
| `NineP.Protocol.Transports` | 14 | `NinePScheme`, `NinePAddress`, `CloseReason`, `PeerIdentity`, `INinePConnection`, `INinePListener`, `ITransport`, `TcpTransport`, `TcpTransportOptions`, `TlsTransport`, `TlsTransportOptions`, `WebSocketTransport`, `WebSocketTransportOptions`, `MemoryTransport` |
| `NineP.Protocol.Auth` | 16 | `Identity`, `AuthRequest`, `IAuthSession`, `IAuthenticator`, `IAuthChannel`, `ICredential`, `TokenAuthenticator`, `PasswordAuthenticator`, `IPasswordStore`, `PasswordFileStore`, `TlsClientCertAuthenticator`, `TokenCredential`, `PasswordCredential`, `BearerTokenCredential`, `CallbackCredential`, `ConstantTime` |
| **`NineP.Protocol` assembly** | **133** | 30 + 70 + 1 + 2 + 14 + 16 |
| `NineP.Client` | 5 | `NinePClient`, `ClientOptions`, `NinePSession`, `INinePMessages`, `NinePFid` |
| `NineP.Server` | 17 | `NinePServer`, `ServerOptions`, `ServerCounters`, `RequestLogEntry`, `IRequestLogSink`, `IFilesystem`, `IHandler`, `IDirectoryHandler`, `DirectoryListing`, `CreateRequest`, `IFileHandler`, `IOpenFile`, `ISymlinkHandler`, `ILockCapability`, `IXattrHandler`, `ILinkCapability`, `IStatFsCapability` |
| **Total** | **155** | of which **66** are message records |

## `NineP.Protocol`

The dialect, the message-type numbers, the qid, the unified attribute model, the lock and xattr
values, the error model, the constants, the limits and the logger. Everything here is shared by the
client and the server and by every dialect.

### `Dialect`

`enum` — the three separately negotiated 9P dialects (§2). The workspace's umbrella label
"9P2000.uL" means "implements all three" and is never sent on the wire.

| Member | Value | Meaning |
| --- | --- | --- |
| `P9_2000` | 0 | Base 9P2000, the Plan 9 protocol of intro(5). |
| `P9_2000_u` | 1 | 9P2000.u, the Unix extension: numeric ids, errno, extension strings. |
| `P9_2000_L` | 2 | 9P2000.L, the Linux dialect of diod and v9fs. |

### `MessageType`

`enum : byte` — every 9P message type number of §2. Thirty-four T/R pairs, sixty-eight members, and
`R == T + 1` throughout. `Terror` (106) and `Tlerror` (6) are declared so that the numbers are
accounted for; they are legal in no dialect and have no message record.

| Member | Value | Member | Value | Member | Value |
| --- | --- | --- | --- | --- | --- |
| `Tlerror` | 6 | `Rlerror` | 7 | `Tstatfs` | 8 |
| `Rstatfs` | 9 | `Tlopen` | 12 | `Rlopen` | 13 |
| `Tlcreate` | 14 | `Rlcreate` | 15 | `Tsymlink` | 16 |
| `Rsymlink` | 17 | `Tmknod` | 18 | `Rmknod` | 19 |
| `Trename` | 20 | `Rrename` | 21 | `Treadlink` | 22 |
| `Rreadlink` | 23 | `Tgetattr` | 24 | `Rgetattr` | 25 |
| `Tsetattr` | 26 | `Rsetattr` | 27 | `Txattrwalk` | 30 |
| `Rxattrwalk` | 31 | `Txattrcreate` | 32 | `Rxattrcreate` | 33 |
| `Treaddir` | 40 | `Rreaddir` | 41 | `Tfsync` | 50 |
| `Rfsync` | 51 | `Tlock` | 52 | `Rlock` | 53 |
| `Tgetlock` | 54 | `Rgetlock` | 55 | `Tlink` | 70 |
| `Rlink` | 71 | `Tmkdir` | 72 | `Rmkdir` | 73 |
| `Trenameat` | 74 | `Rrenameat` | 75 | `Tunlinkat` | 76 |
| `Runlinkat` | 77 | `Tversion` | 100 | `Rversion` | 101 |
| `Tauth` | 102 | `Rauth` | 103 | `Tattach` | 104 |
| `Rattach` | 105 | `Terror` | 106 | `Rerror` | 107 |
| `Tflush` | 108 | `Rflush` | 109 | `Twalk` | 110 |
| `Rwalk` | 111 | `Topen` | 112 | `Ropen` | 113 |
| `Tcreate` | 114 | `Rcreate` | 115 | `Tread` | 116 |
| `Rread` | 117 | `Twrite` | 118 | `Rwrite` | 119 |
| `Tclunk` | 120 | `Rclunk` | 121 | `Tremove` | 122 |
| `Rremove` | 123 | `Tstat` | 124 | `Rstat` | 125 |
| `Twstat` | 126 | `Rwstat` | 127 | | |

### `MessageTypes`

`static class` — helpers over `MessageType`: wire legality per dialect, T/R pairing, and the
protocol's own name for a type number.

```csharp
public static bool IsLegal(MessageType type, Dialect dialect);
```
True when the type may appear in a session of that dialect (§2). `Terror` and `Tlerror` are legal
in none, and an unknown number is legal in none.

```csharp
public static bool IsRequest(MessageType type);
```
True for a request type, false for a reply; `Terror` and `Tlerror` are neither, because they never
reach the wire.

```csharp
public static MessageType ReplyOf(MessageType requestType);
```
The reply type paired with a request type, which is always its number plus one. Throws
`ArgumentOutOfRangeException` when the argument is not a request type.

```csharp
public static string GetName(MessageType type);
```
The protocol's name for the type, for example "Twalk", used in logs and error text; a number the
protocol does not define renders as `type <n>`.

### `QidType`

`[Flags] enum : byte` — qid type bits (§4.1). The two low bits follow Linux, not the 9P2000.u
draft, because every .u and .L peer this workspace interoperates with is Linux-derived.

| Member | Value | Meaning |
| --- | --- | --- |
| `QTFILE` | 0x00 | A plain file. |
| `QTLINK` | 0x01 | A hard link (.u, Linux). |
| `QTSYMLINK` | 0x02 | A symbolic link (.u, Linux). |
| `QTTMP` | 0x04 | Not backed up. |
| `QTAUTH` | 0x08 | An authentication file, the file an afid names. |
| `QTMOUNT` | 0x10 | A mounted channel. |
| `QTEXCL` | 0x20 | Exclusive use: one open fid at a time. |
| `QTAPPEND` | 0x40 | Append only: writes ignore the offset. |
| `QTDIR` | 0x80 | A directory. |

### `Qid`

`readonly record struct` — a 9P qid: `type[1] version[4] path[8]` (§4.1).

```csharp
public readonly record struct Qid(QidType Type, uint Version, ulong Path);
```
`Type` are the type bits, which mirror the high 8 bits of the file's mode word; `Version` is a
number that changes whenever the file changes; `Path` is the identity of the file within the
server, unique for the server's life.

```csharp
public const int WireSize = 13;
```
The 13 bytes this qid occupies on the wire.

### `FileKind`

`enum` — the dialect-neutral file type a handler declares (§7).

| Member | Value | Meaning |
| --- | --- | --- |
| `File` | 0 | A regular file. |
| `Directory` | 1 | A directory. |
| `Symlink` | 2 | A symbolic link; only .u and .L can represent one. |
| `Fifo` | 3 | A named pipe. |
| `Socket` | 4 | A Unix domain socket. |
| `CharDevice` | 5 | A character device. |
| `BlockDevice` | 6 | A block device. |

### `FileFlags`

`[Flags] enum` — the non-permission mode bits of §4.4, dialect-neutral.

| Member | Value | Meaning |
| --- | --- | --- |
| `None` | 0 | No flag is set. |
| `Append` | 1 | Append only: writes ignore the offset and OTRUNC is ignored. |
| `Exclusive` | 2 | Exclusive use: one open fid at a time. |
| `Temporary` | 4 | Not backed up. |
| `Auth` | 8 | The authentication file behind an afid. |
| `Mount` | 16 | A mounted channel. |

### `OpenMode`

`enum` — the access mode of an open fid: the low two bits of §4.5, unified across dialects.

| Member | Value | Meaning |
| --- | --- | --- |
| `Read` | 0 | Open for reading. |
| `Write` | 1 | Open for writing. |
| `ReadWrite` | 2 | Open for reading and writing. |
| `Exec` | 3 | Open for execution: a read that also checks execute permission. |

### `OpenFlags`

`[Flags] enum` — flags that modify an open without changing its access mode (§4.5). OAPPEND is one
of these, not an access mode: OREAD plus OAPPEND stays a read open.

| Member | Value | Meaning |
| --- | --- | --- |
| `None` | 0 | No flag is set. |
| `Truncate` | 1 | Truncate the file first; needs write permission. |
| `RemoveOnClose` | 2 | Remove the file when the fid is clunked; needs remove permission in the parent. |
| `Append` | 4 | Writes on this fid go to the end of the file. |
| `Exclusive` | 8 | Fail the open if the file already exists (.L O_EXCL on create). |
| `Directory` | 16 | Fail the open unless the file is a directory (.L O_DIRECTORY). |
| `NoFollow` | 32 | Fail the open if the final component is a symbolic link (.L O_NOFOLLOW). |

### `TimeSpec`

`readonly record struct` — a POSIX-style timestamp with nanosecond resolution (§3.4, §4.6).

```csharp
public readonly record struct TimeSpec(long Seconds, uint Nanoseconds);
```
`Seconds` is whole seconds since the Unix epoch; `Nanoseconds` is nanoseconds within the second.

### `DeviceId`

`readonly record struct` — a device major/minor pair for character and block devices (§4.7).

```csharp
public readonly record struct DeviceId(uint Major, uint Minor);
```

### `Attr`

`sealed record` — the dialect-neutral attributes of one file (§7). A handler produces exactly one
of these and the protocol layer projects it into a 9P2000 stat record, a 9P2000.u stat record or an
`Rgetattr`; no handler ever writes a dialect-specific shape itself.

```csharp
public required Qid Qid { get; init; }
```
The file's qid; its type byte must agree with `Kind` and `Flags`.

```csharp
public required FileKind Kind { get; init; }
```
The file type (§7).

```csharp
public required uint Perm { get; init; }
```
Permission bits only: 0777 plus setuid, setgid and sticky (the 07777 mask).

```csharp
public FileFlags Flags { get; init; }
```
Non-permission mode bits: append, exclusive, temporary, auth.

```csharp
public ulong NLink { get; init; } = 1;
```
The hard-link count; 1 for a synthetic file.

```csharp
public string UserName { get; init; } = "";
```
The textual owner, which is the 9P2000 stat record's `uid`.

```csharp
public string GroupName { get; init; } = "";
```
The textual group, which is the 9P2000 stat record's `gid`.

```csharp
public string ModifierName { get; init; } = "";
```
The textual last modifier, which is the 9P2000 stat record's `muid`.

```csharp
public uint Uid { get; init; } = Constants.NONUNAME;
```
The numeric owner (.u `n_uid`, .L `uid`); NONUNAME when unknown.

```csharp
public uint Gid { get; init; } = Constants.NONUNAME;
```
The numeric group (.u `n_gid`, .L `gid`); NONUNAME when unknown.

```csharp
public uint ModifierUid { get; init; } = Constants.NONUNAME;
```
The numeric last modifier (.u `n_muid`); NONUNAME when unknown.

```csharp
public DeviceId? Rdev { get; init; }
```
The device numbers of a character or block device; null for anything else.

```csharp
public ulong Size { get; init; }
```
The file length in bytes; the projection reports 0 for a directory (§4.2).

```csharp
public ulong BlockSize { get; init; } = 4096;
```
The preferred I/O block size an `Rgetattr` reports.

```csharp
public ulong Blocks { get; init; }
```
The allocated 512-byte block count an `Rgetattr` reports.

```csharp
public TimeSpec ATime { get; init; }
public TimeSpec MTime { get; init; }
public TimeSpec CTime { get; init; }
public TimeSpec BTime { get; init; }
```
The last access time; the last modification time; the last status-change time (.L only); the
creation time (.L only, zero when unknown).

```csharp
public ulong Gen { get; init; }
public ulong DataVersion { get; init; }
```
The generation number and the data version, both .L only.

```csharp
public string? SymlinkTarget { get; init; }
```
The target of a `FileKind.Symlink`; null for anything else.

### `SetAttr`

`sealed record` — a partial attribute update; a null member means "do not touch" (§7). One type
carries both a `Twstat` and a `Tsetattr`, so a handler never learns which dialect asked.

**What a `Twstat` may change.** Reference §5.8 names the settable fields — `name`, `mode`, `mtime`,
`gid`, `length` and, in .u, `n_gid`. The rest are refused with `EPERM` rather than dropped and
answered `Rwstat`, which would be a success reply for work that was never done:

| Field | Rule |
| --- | --- |
| `uid`, `n_uid` | the owner may never change through a `Twstat` (use `Tsetattr` in .L) |
| `muid`, `n_muid` | the last modifier is the server's to set, never the client's |
| `atime` | not settable; `mtime` is |
| `type`, `dev` | kernel fields, not settable |
| `qid` | identity, not an attribute |
| the `DMDIR` bit of `mode` | a file cannot become a directory or stop being one |

A field counts as changed only when it **differs from the value the file already has**. A client
that fills a `Twstat` from the record it just read — which is what Linux v9fs does for an ordinary
`chmod` or `truncate` on a .u mount — carries the file's own `uid`, `muid`, `type` and `dev` back
to the server; those ask for nothing and are a no-op, not a refusal. The sentinel "don't touch"
values are of course still the primary way to leave a field alone, and a record where *every* field
is don't-touch is an fsync request (§4.2).

The client-side inverse refuses the same things before a byte goes out:
`AttrProjector.ToWstat` throws for a `SetAttr` that sets `Uid`.

```csharp
public string? Name { get; init; }
```
A new name (`Twstat` only; a rename within the same directory).

```csharp
public uint? Perm { get; init; }
```
New permission bits (the 07777 mask).

```csharp
public uint? Uid { get; init; }
```
A new numeric owner (`Tsetattr` only; a `Twstat` may never change it).

```csharp
public uint? Gid { get; init; }
```
A new numeric group.

```csharp
public string? GroupName { get; init; }
```
A new textual group (`Twstat`).

```csharp
public ulong? Size { get; init; }
```
A new length: truncation or extension.

```csharp
public TimeSpec? ATime { get; init; }
public TimeSpec? MTime { get; init; }
```
A new access time and a new modification time; either one left null while its `ATimeToNow` or
`MTimeToNow` twin is true means "use the server's clock".

```csharp
public bool ATimeToNow { get; init; }
public bool MTimeToNow { get; init; }
public bool CTimeToNow { get; init; }
```
True when the client asked for that time without its `_SET` twin: use the server's clock (§4.6).

```csharp
public bool IsFsyncRequest { get; }
```
True when every field is "don't touch": a `Twstat` that means commit this file to stable storage
rather than change it (§4.2), never a no-op.

### `DirEntry`

`readonly record struct` — one directory entry, unified over 9P2000 stat records and .L dirents
(§4.3). The server's directory packer and the client's `ReadDir` both speak it, so a listing looks
the same to a caller whichever record format the session negotiated.

```csharp
public readonly record struct DirEntry(string Name, Qid Qid, FileKind Kind, ulong Cursor);
```
`Name` never contains '/'; `Qid` is the qid of the file the entry names; `Kind` becomes the POSIX
`d_type` byte in a dirent; `Cursor` is the cookie a `Treaddir` passes to continue after this entry.

```csharp
public byte DirentType { get; }
```
The POSIX `d_type` byte this entry carries in an `Rreaddir` record.

```csharp
public static FileKind KindOf(byte direntType);
```
The file kind a POSIX `d_type` byte names; anything unknown reads as a file.

### `StatFs`

`readonly record struct` — filesystem statistics answered by `Tstatfs` (§4.9).

```csharp
public readonly record struct StatFs(
    uint Type, uint BlockSize, ulong Blocks, ulong BlocksFree, ulong BlocksAvailable,
    ulong Files, ulong FilesFree, ulong FsId, uint NameLength);
```
`Type` is the filesystem magic; `BlockSize` is the unit the block counts are expressed in;
`Blocks`, `BlocksFree` and `BlocksAvailable` are total, free and unprivileged-free blocks; `Files`
and `FilesFree` are total and free inodes; `FsId` is an opaque filesystem identifier; `NameLength`
is the longest component name the filesystem accepts.

```csharp
public const uint V9fsMagic = 0x01021997;
```
V9FS_MAGIC, the type a synthetic server reports.

### `LockType`

`enum : byte` — the POSIX lock type carried by `Tlock` and `Tgetlock` (§4.8).

| Member | Value | Meaning |
| --- | --- | --- |
| `ReadLock` | 0 | A shared read lock. |
| `WriteLock` | 1 | An exclusive write lock. |
| `Unlock` | 2 | Release a lock; as an `Rgetlock` answer it means "no conflicting lock". |

### `LockFlags`

`[Flags] enum` — `Tlock` flags (§4.8).

| Member | Value | Meaning |
| --- | --- | --- |
| `None` | 0 | No flag is set. |
| `Block` | 1 | The client is willing to be told BLOCKED and to retry. |
| `Reclaim` | 2 | Reserved by the reference; this workspace never honours it. |

### `LockStatus`

`enum : byte` — the status an `Rlock` carries (§4.8).

| Member | Value | Meaning |
| --- | --- | --- |
| `Success` | 0 | The lock was granted or released. |
| `Blocked` | 1 | A conflicting lock is held; the client may retry. |
| `Error` | 2 | The request could not be honoured. |
| `Grace` | 3 | The server is in its post-restart grace period. |

### `LockRequest`

`readonly record struct` — a byte-range lock request; a `Length` of 0 means "to the end of the
file".

```csharp
public readonly record struct LockRequest(
    LockType Type, LockFlags Flags, ulong Start, ulong Length, uint ProcId, string ClientId);
```
`ProcId` is the client's process identifier, which owns the lock, and `ClientId` scopes it.

### `LockQueryResult`

`readonly record struct` — the answer to `Tgetlock`; `LockType.Unlock` means no conflict.

```csharp
public readonly record struct LockQueryResult(
    LockType Type, ulong Start, ulong Length, uint ProcId, string ClientId);
```

### `XattrFlags`

`[Flags] enum` — `Txattrcreate` flags (§5.9).

| Member | Value | Meaning |
| --- | --- | --- |
| `None` | 0 | Create the attribute or replace it. |
| `Create` | 1 | Fail if the attribute already exists. |
| `Replace` | 2 | Fail if the attribute does not already exist. |

### `NinePError`

`readonly record struct` — the 9P error value: a Plan 9 ename plus a Linux errno (architecture §3).
One value, three wire shapes — `Rerror`, `Rerror` with `errno[4]`, and `Rlerror` — chosen by the
session dialect, never by the code that raised it.

```csharp
public readonly record struct NinePError(string Ename, int Errno);
```
`Ename` is the Plan 9 error text a 9P2000 or 9P2000.u peer is told; `Errno` is the Linux errno a
9P2000.L peer is told. errno is a signed 32-bit value everywhere in this API, `0` means none, and a
negative value is not an errno: the codec projects one to `EIO` and refuses to encode it.

```csharp
public static NinePError FromErrno(int errno);
```
Builds an error from an errno, taking the ename from `ErrorTable`.

```csharp
public static NinePError FromEname(string ename);
```
Builds an error from an ename, taking the errno from `ErrorTable`; an unknown ename maps to `EIO`.

```csharp
public string TruncatedEname { get; }
```
The ename truncated to `ERRMAX - 1` bytes on a UTF-8 rune boundary, so a long error never overruns
the conventional cap and never splits a character (§8 rule 10).

### `NinePException`

`class : Exception` — the base of every exception this library throws, carrying the 9P error value
the core projects onto the wire.

```csharp
public NinePException();
public NinePException(string message);
public NinePException(string message, Exception innerException);
public NinePException(NinePError error);
```
The first three carry the generic I/O error (`EIO`); the message is for the developer and never
reaches the wire. The fourth carries a specific 9P error value, which is what a handler throws to
answer a request with an error.

```csharp
public NinePError Error { get; }
```
The 9P error this exception projects to on the wire.

### `NinePProtocolException`

`sealed class : NinePException` — a malformed or illegal 9P message (§8). The kind is
machine-readable so that a caller can tell a size violation, which cannot be resynced, from a field
that merely did not parse.

```csharp
public NinePProtocolException();
public NinePProtocolException(string message);
public NinePProtocolException(string message, Exception innerException);
public NinePProtocolException(ProtocolErrorKind kind, string message);
```
The first three default the kind to `ProtocolErrorKind.Bounds`; the fourth states the kind and a
human-readable detail.

```csharp
public ProtocolErrorKind Kind { get; }
```
What was wrong with the message.

### `NinePVersionException`

`sealed class : NinePException` — version negotiation failed: the server answered "unknown", or it
offered a dialect below the client's floor. The client never silently downgrades (§5.1).

```csharp
public NinePVersionException();
public NinePVersionException(string message);
public NinePVersionException(string message, Exception innerException);
public NinePVersionException(string message, string serverVersion, uint serverMsize);
```
The last records what the server answered.

```csharp
public string ServerVersion { get; }
public uint ServerMsize { get; }
```
The version string and the msize the server replied with.

### `ProtocolErrorKind`

`enum` — the machine-readable classification of a codec failure (architecture §3).

| Member | Value | Meaning |
| --- | --- | --- |
| `Size` | 0 | `size` is below 7, above the active bound, or the frame is short. |
| `Bounds` | 1 | A counted field runs past the end of the frame. |
| `Utf8` | 2 | A string is not valid UTF-8. |
| `Nul` | 3 | A string contains a NUL byte. |
| `Name` | 4 | A name contains '/', is ".", or exceeds 255 bytes. |
| `NWName` | 5 | `nwname` or `nwqid` exceeds MAXWELEM (16). |
| `Trailing` | 6 | Bytes remain after the last field of the message. |
| `Overflow` | 7 | An arithmetic overflow: offset plus count, a size, a lock's start plus length, or an `errno[4]` / `ecode[4]` above `int.MaxValue`, which no signed errno can hold. |
| `Stat` | 8 | A `stat[n]` whose inner `size[2]` disagrees with `n - 2`. |
| `Type` | 9 | An unknown type number, or one illegal for the session dialect. |

### `Constants`

`static class` — the protocol constants of §1, under their `fcall.h` / `linux-9p.h` names.

```csharp
public const ushort NOTAG = 0xFFFF;
public const uint NOFID = 0xFFFFFFFF;
public const uint NONUNAME = 0xFFFFFFFF;
public const int MAXWELEM = 16;
public const int IOHDRSZ = 24;
public const int READDIRHDRSZ = 24;
public const int ERRMAX = 128;
public const int STATFIXLEN = 49;
public const int HDRSZ = 7;
public const int TwriteHeaderSize = 23;
public const int RreadHeaderSize = 11;
public const int MaxNameLength = 255;
public const string Version9P2000 = "9P2000";
public const string Version9P2000u = "9P2000.u";
public const string Version9P2000L = "9P2000.L";
public const string VersionUnknown = "unknown";
public const string WebSocketSubprotocol = "9p";
```

| Constant | What it is |
| --- | --- |
| `NOTAG` | The tag `Tversion` and `Rversion` carry (§1). |
| `NOFID` | "No fid": the `afid` of a `Tattach` that does not authenticate. |
| `NONUNAME` | The .u and .L `n_uname` value meaning "unspecified". |
| `MAXWELEM` | The largest `nwname` or `nwqid` one walk may carry (§8 rule 2). |
| `IOHDRSZ` | The header room reserved for I/O: the largest payload is `msize - IOHDRSZ`. |
| `READDIRHDRSZ` | The same allowance for `Rreaddir` (§1). |
| `ERRMAX` | The conventional cap on the length of an `ename` (§8 rule 10). |
| `STATFIXLEN` | The fixed part of a 9P2000 stat record, including its leading `size[2]`. |
| `HDRSZ` | The bytes every message spends on `size[4] type[1] tag[2]`. |
| `TwriteHeaderSize` | The `Twrite` header: `size[4] type[1] tag[2] fid[4] offset[8] count[4]`. |
| `RreadHeaderSize` | The `Rread` header: `size[4] type[1] tag[2] count[4]`. |
| `MaxNameLength` | The longest legal file-name component, in bytes (§8 rule 3). |
| `Version9P2000` | The wire string of the base dialect. |
| `Version9P2000u` | The wire string of the Unix extension. |
| `Version9P2000L` | The wire string of the Linux dialect. |
| `VersionUnknown` | The reply that means "no dialect was agreed" (§5.1). |
| `WebSocketSubprotocol` | The optional WebSocket subprotocol name a 9P endpoint offers. |

### `Errno`

`static class` — the Linux errno values this workspace's servers use (§5.9). Twenty-seven `int`
constants, each with the POSIX meaning of its name.

```csharp
public const int EPERM = 1;          // operation not permitted
public const int ENOENT = 2;         // no such file or directory
public const int EIO = 5;            // input/output error
public const int ENXIO = 6;          // no such device or address: an unopenable fifo, socket or device
public const int EBADF = 9;          // bad file descriptor: in 9P, an unknown fid
public const int EAGAIN = 11;        // resource temporarily unavailable
public const int ENOMEM = 12;        // out of memory
public const int EACCES = 13;        // permission denied
public const int EEXIST = 17;        // file exists
public const int ENOTDIR = 20;       // not a directory
public const int EISDIR = 21;        // is a directory
public const int EINVAL = 22;        // invalid argument
public const int ENFILE = 23;        // too many open files in the system: in 9P, the fid cap
public const int EFBIG = 27;         // file too large
public const int ENOSPC = 28;        // no space left on device
public const int EROFS = 30;         // read-only file system
public const int ERANGE = 34;        // result too large
public const int ENAMETOOLONG = 36;  // file name too long
public const int ENOLCK = 37;        // no locks available
public const int ENOSYS = 38;        // function not implemented
public const int ENOTEMPTY = 39;     // directory not empty
public const int ELOOP = 40;         // too many levels of symbolic links
public const int ENODATA = 61;       // no data available: no such extended attribute
public const int EPROTO = 71;        // protocol error
public const int EOVERFLOW = 75;     // value too large for its type
public const int EOPNOTSUPP = 95;    // operation not supported
public const int ECONNREFUSED = 111; // connection refused: the .L Tauth refusal
```

### `ErrorTable`

`static class` — the errno-to-ename projection table (architecture §3): Plan 9 wording where one
exists, so a 9P2000 client and a 9P2000.L client are told the same thing in their own terms.

```csharp
public static string EnameFor(int errno);
```
The Plan 9 ename for an errno, for example ENOENT to "file not found".

```csharp
public static int ErrnoFor(string ename);
```
The errno for a known Plan 9 ename; `EIO` when the ename is not in the table.

```csharp
public static IReadOnlyList<NinePError> All { get; }
```
Every (errno, ename) pair in the table, for documentation and tests.

The table is fixed and is a test fixture in its own right (`ErrorTableTests`):

| errno | ename | errno | ename |
| --- | --- | --- | --- |
| EPERM 1 | `permission denied` | ENFILE 23 | `too many fids` |
| ENOENT 2 | `file not found` | EFBIG 27 | `file too big` |
| EIO 5 | `i/o error` | ENOSPC 28 | `no space left` |
| ENXIO 6 | `no such device or address` | | |
| EBADF 9 | `unknown fid` | EROFS 30 | `read-only file system` |
| EAGAIN 11 | `try again` | ERANGE 34 | `result too large` |
| ENOMEM 12 | `out of memory` | ENAMETOOLONG 36 | `file name too long` |
| EACCES 13 | `permission denied` | ENOLCK 37 | `lock not available` |
| EEXIST 17 | `file already exists` | ENOSYS 38 | `not implemented` |
| ENOTDIR 20 | `not a directory` | ENOTEMPTY 39 | `directory not empty` |
| EISDIR 21 | `is a directory` | ELOOP 40 | `too many symbolic links` |
| EINVAL 22 | `bad argument` | ENODATA 61 | `no such attribute` |
| EPROTO 71 | `bad message` | EOVERFLOW 75 | `value too large` |
| EOPNOTSUPP 95 | `not supported` | ECONNREFUSED 111 | `authentication not required` |

Server-generated enames that are not derived from an errno keep their Plan 9 wording and map back
through `ErrnoFor`: `"bad message"` (EPROTO), `"duplicate tag"` (EINVAL), `"duplicate fid"`
(EINVAL), `"unknown fid"` (EBADF), `"unknown message"` (EOPNOTSUPP), `"bad offset"` (EINVAL),
`"bad open mode"` (EINVAL), `"bad name"` (EINVAL), `"cannot clone open fid"` (EINVAL),
`"authentication failed"` (EACCES), `"authentication not required"` (ECONNREFUSED),
`"too many fids"` (ENFILE), `"version not negotiated"` (EPROTO), `"symlinks not supported"`
(EOPNOTSUPP), `"file exists"` (EEXIST) and `"directory not empty"` (ENOTEMPTY).

The refusals of §8 rules 15 and 19 are in the same list, so a 9P2000 peer — whose `Rerror` carries
the text and no errno — recovers the errno the refusal actually meant rather than `EIO`:
`"create cannot set DMAPPEND/DMEXCL/DMTMP"`, `"wstat cannot change DMAPPEND/DMEXCL/DMTMP"`,
`"wstat cannot change DMDIR"`, `"wstat cannot change the owner"`, `"wstat cannot set muid"`,
`"wstat cannot set atime"`, `"wstat cannot set type"`, `"wstat cannot set dev"` and
`"wstat cannot set qid"` are all EPERM, and `"cannot rename across directories"` is EOPNOTSUPP.

### `Limits`

`sealed record` — every configurable bound of architecture §4, with the defaults this workspace
ships. The record is immutable: a server or client is handed one at construction and it does not
change under it.

```csharp
public static Limits Default { get; }
```
The limits every server and client uses unless the caller overrides them.

```csharp
public uint MaxMsize { get; init; } = 1024 * 1024;
```
The largest msize this side will negotiate. Default 1 MiB.

```csharp
public uint MinMsize { get; init; } = 4096;
```
The smallest msize this side will serve; below it a server answers `Rversion "unknown"` rather than
an `Rerror`, which version(5) forbids. Default 4096.

```csharp
public uint PreNegotiationFrameCap { get; init; } = 8192;
```
The frame cap that applies until `Tversion` has been answered. It is a small constant, never the
configured maximum: an unauthenticated peer must not be able to make a connection reserve a
megabyte by lying in the size field. Default 8192.

```csharp
public int MaxFidsPerConnection { get; init; } = 65536;
```
Fids one connection may hold; beyond it "too many fids" / ENFILE. Default 65536.

```csharp
public int MaxInFlightPerConnection { get; init; } = 256;
```
Requests in flight per connection, including the flush reserve. Default 256.

```csharp
public int MaxInFlightPerListener { get; init; } = 4096;
```
Requests in flight across every connection of one listener. Default 4096.

```csharp
public int FlushReservePerConnection { get; init; } = 8;
```
Slots of the per-connection budget only `Tflush` may use, so a client that filled its window can
still cancel what is in it. Default 8.

```csharp
public int MaxConnectionsPerListener { get; init; } = 1024;
```
Accepted connections per listener. Default 1024.

```csharp
public TimeSpan ReadHeaderTimeout { get; init; } = TimeSpan.FromSeconds(30);
```
How long a connection may take to deliver a complete frame header. Default 30 s.

```csharp
public TimeSpan IdleTimeout { get; init; } = TimeSpan.Zero;
```
Idle timeout; `TimeSpan.Zero` disables it. Default Zero.

```csharp
public int MaxNameLength { get; init; } = Constants.MaxNameLength;
```
The longest legal file-name component in bytes. Default 255.

```csharp
public int MaxAuthBytes { get; init; } = 64 * 1024;
```
Bytes an afid exchange may carry in each direction. Default 64 KiB.

```csharp
public TimeSpan AuthTimeout { get; init; } = TimeSpan.FromSeconds(30);
```
The wall-clock budget for one afid exchange. Default 30 s.

```csharp
public void Validate();
```
Throws `ArgumentOutOfRangeException` when the record is internally inconsistent, so a
misconfiguration fails at construction rather than under load.

### `IMessage`

`interface` — implemented by every wire-legal message record, tying a record to its type number.
The type is a static abstract member so that the codec can be generic over the record without
boxing it.

```csharp
static abstract MessageType Type { get; }
```
The type number this record encodes to.

```csharp
ushort Tag { get; }
```
The message tag; `NOTAG` for `Tversion` and `Rversion`.

### `StatRecord`

`readonly record struct` — a 9P2000 / 9P2000.u stat record (§4.2). In a 9P2000 session `Extension`
is null and the three numeric ids are `NONUNAME`: which fields are on the wire is decided by the
session dialect, never by sniffing bytes. It is declared with `init`-only properties, so it has no positional constructor and no
`Deconstruct`.

```csharp
public ushort Type { get; init; }        // for kernel use; a server that does not care sends zero
public uint Dev { get; init; }           // for kernel use; a server that does not care sends zero
public Qid Qid { get; init; }            // the qid of the file this record describes
public uint Mode { get; init; }          // the permission and type bits of reference §4.4
public uint ATime { get; init; }         // last access, in whole seconds since the Unix epoch
public uint MTime { get; init; }         // last modification, in whole seconds since the epoch
public ulong Length { get; init; }       // the file length in bytes; zero for a directory
public string Name { get; init; }        // the file's name; "/" for the root of a served tree
public string Uid { get; init; }         // the textual owner
public string Gid { get; init; }         // the textual group
public string Muid { get; init; }        // the textual name of the last user to modify the file
public string? Extension { get; init; }  // .u: a symlink target, or "b maj min" for a device
public uint NUid { get; init; }          // .u: the numeric owner; NONUNAME when absent (every 9P2000 session)
public uint NGid { get; init; }          // .u: the numeric group; NONUNAME when absent
public uint NMuid { get; init; }         // .u: the numeric last modifier; NONUNAME when absent
```

```csharp
public static StatRecord DontTouch { get; }
```
A record whose every integer field is the "don't touch" value of its width and whose every string
is empty: the shape a `Twstat` takes when it means fsync (§4.2).

```csharp
public bool IsAllDontTouch { get; }
```
True when every field carries its "don't touch" value, which makes a `Twstat` a request to commit
the file to stable storage rather than to change it (§4.2).

```csharp
public int GetEncodedSize(Dialect dialect);
```
The number of bytes this record occupies after its own leading `size[2]`, which is the value that
leading field carries.

**A record too long for its own length field is refused.** `size[2]` and the `n[2]` that wraps a
record inside `Rstat` are sixteen bits wide, so a record whose `name`, `uid`, `gid`, `muid` or .u
`extension` push it past 65 535 bytes (65 533 for the wrapped form) cannot describe itself.
Encoding one throws `NinePException` carrying `EOVERFLOW` (75, `"value too large"`) instead of
truncating the length field, which would produce a frame whose outer `size[4]` disagreed with its
inner lengths and which every peer would reject as garbage. This applies to `MessageCodec.Encode`
of an `Rstat` or a `Twstat`, and to the records packed into a directory `Rread`. The strings are
the server's own, but a client can provoke the refusal by supplying one of them — a long
`Tattach.uname` against a tree that reports the attaching user as the owner — so the shipped server
encodes every reply **before** it claims the request's tag, and answers the overflow as an ordinary
error reply rather than dropping the request (`docs/server.md`, "Stat records that cannot describe
themselves").

### `GetAttrMask`

`[Flags] enum : ulong` — the `Tgetattr.request_mask` and `Rgetattr.valid` bits (§4.6). A server
always sends the full 160-byte reply; fields it did not mark valid carry zero.

| Member | Value | Meaning |
| --- | --- | --- |
| `None` | 0 | Nothing is requested or valid. |
| `Mode` | 0x1 | The POSIX mode word. |
| `NLink` | 0x2 | The hard-link count. |
| `Uid` | 0x4 | The numeric owner. |
| `Gid` | 0x8 | The numeric group. |
| `Rdev` | 0x10 | The device numbers of a device node. |
| `ATime` | 0x20 | The last access time. |
| `MTime` | 0x40 | The last modification time. |
| `CTime` | 0x80 | The last status-change time. |
| `Ino` | 0x100 | The inode number, which 9P carries as the qid path. |
| `Size` | 0x200 | The file length. |
| `Blocks` | 0x400 | The allocated 512-byte block count. |
| `BTime` | 0x800 | The creation time. |
| `Gen` | 0x1000 | The generation number. |
| `DataVersion` | 0x2000 | The data version. |
| `Basic` | 0x7FF | Everything through `Blocks`: what a stat(2) needs. |
| `All` | 0x3FFF | Every defined bit. |

### `SetAttrMask`

`[Flags] enum : uint` — the `Tsetattr.valid` bits (§4.6). A time bit without its `_SET` twin means
"use the server's current time"; with it, use the value the client supplied.

| Member | Value | Meaning |
| --- | --- | --- |
| `None` | 0 | Nothing is being changed. |
| `Mode` | 0x1 | Change the permission bits. |
| `Uid` | 0x2 | Change the numeric owner. |
| `Gid` | 0x4 | Change the numeric group. |
| `Size` | 0x8 | Change the length: truncate or extend. |
| `ATime` | 0x10 | Change the access time. |
| `MTime` | 0x20 | Change the modification time. |
| `CTime` | 0x40 | Change the status-change time to the server's clock. |
| `ATimeSet` | 0x80 | The access time in the message is the one to use. |
| `MTimeSet` | 0x100 | The modification time in the message is the one to use. |

### The message records — session (§3.1)

| Record | Type |  C# positional signature | What it is |
| --- | --- | --- | --- |
| `Tversion` | 100 | `Tversion(ushort Tag, uint Msize, string Version)` | Proposes a dialect and a message size; it resets the session, clunking every fid and abandoning every outstanding request. |
| `Rversion` | 101 | `Rversion(ushort Tag, uint Msize, string Version)` | Answers a `Tversion` with the dialect and size the server agrees to, or "unknown"; an "unknown" reply echoes the client's msize, never the server's. |
| `Tauth` | 102 | `Tauth(ushort Tag, uint Afid, string Uname, string Aname, uint NUname)` | Opens an afid for the authentication exchange; what flows over the afid is not 9P's business. `NUname` is `NONUNAME` when unspecified; it is on the wire in .u and .L only, so a 9P2000 frame encodes nothing and decodes as `NONUNAME`. |
| `Rauth` | 103 | `Rauth(ushort Tag, Qid Aqid)` | Accepts an authentication exchange and names the file behind the afid; `Aqid.Type` carries `QTAUTH`. |
| `Tattach` | 104 | `Tattach(ushort Tag, uint Fid, uint Afid, string Uname, string Aname, uint NUname)` | Introduces a fid to the root of a tree, as the user the afid authenticated. `NUname` is `NONUNAME` when unspecified; on the wire in .u and .L only, as for `Tauth`. |
| `Rattach` | 105 | `Rattach(ushort Tag, Qid Qid)` | Answers a `Tattach` with the qid of the root of the tree. |
| `Rerror` | 107 | `Rerror(ushort Tag, string Ename, int Errno)` | The error reply of 9P2000 and 9P2000.u; `Errno` is 0 (none) in 9P2000, which has no such field, and set in .u. A .L session never carries one. |
| `Rlerror` | 7 | `Rlerror(ushort Tag, int Ecode)` | The error reply of 9P2000.L: an errno and nothing else. |
| `Tflush` | 108 | `Tflush(ushort Tag, ushort OldTag)` | Abandons an outstanding request (§5.3). |
| `Rflush` | 109 | `Rflush(ushort Tag)` | Confirms that a request has been abandoned; the client may reuse the old tag only now (§8 rule 14). |

### The message records — navigation and I/O, all dialects (§3.2)

| Record | Type |  C# positional signature | What it is |
| --- | --- | --- | --- |
| `Twalk` | 110 | `Twalk(ushort Tag, uint Fid, uint NewFid, IReadOnlyList<string> Wnames)` | Moves a fid through the tree one element at a time; at most MAXWELEM (16) elements per message. |
| `Rwalk` | 111 | `Rwalk(ushort Tag, IReadOnlyList<Qid> Wqids)` | One qid per element that was walked; fewer qids than names is a partial walk, and `NewFid` is not bound. |
| `Tread` | 116 | `Tread(ushort Tag, uint Fid, ulong Offset, uint Count)` | Reads bytes from an open fid (§5.6). |
| `Rread` | 117 | `Rread(ushort Tag, ReadOnlyMemory<byte> Data)` | The bytes that were read; a short reply is not an error. The wire `count` is `Data.Length`. |
| `Twrite` | 118 | `Twrite(ushort Tag, uint Fid, ulong Offset, ReadOnlyMemory<byte> Data)` | Writes bytes to an open fid; the count must equal `size - 23` (§8 rule 4). |
| `Rwrite` | 119 | `Rwrite(ushort Tag, uint Count)` | How many bytes were written; fewer than asked is a short write, not an error. |
| `Tclunk` | 120 | `Tclunk(ushort Tag, uint Fid)` | Forgets a fid; the fid is gone whatever the reply says. |
| `Rclunk` | 121 | `Rclunk(ushort Tag)` | Confirms that a fid has been forgotten. |
| `Tremove` | 122 | `Tremove(ushort Tag, uint Fid)` | Removes the file a fid names and forgets the fid, even when the removal fails. |
| `Rremove` | 123 | `Rremove(ushort Tag)` | Confirms that a file has been removed. |

**errno is signed everywhere and never negative on the wire.** `Rerror.Errno`, `Rlerror.Ecode`
and `NinePError.Errno` are all `int` (architecture §12 rule 2), and `0` means none. The wire field
is `u32`, so `MessageCodec.Encode` refuses a negative errno with `ArgumentException` wherever it
would go on the wire (`Rlerror`; `Rerror` in .u), and decoding rejects an `errno[4]` / `ecode[4]`
above `int.MaxValue` as `ProtocolErrorKind.Overflow`.

`Twalk.Wnames` and `Rwalk.Wqids` are `IReadOnlyList<T>` backed by a pooled array of at most
`MAXWELEM` entries owned by the frame lease; decoding rejects `nwname > 16` **before** allocating.

**Payloads alias the frame** (architecture §12 rule 5). `Rread.Data` and `Twrite.Data` alias the
decoded frame, whose buffer is pooled, and are valid only until that frame is released — for a
server, the end of the handler call; for a client, the return of the transaction that produced
them. A caller who keeps them copies. The rule is stated on the two members themselves, not only
on the internal lease type.

### The message records — 9P2000 and 9P2000.u only (§3.3)

| Record | Type |  C# positional signature | What it is |
| --- | --- | --- | --- |
| `Topen` | 112 | `Topen(ushort Tag, uint Fid, byte Mode)` | Opens an existing file; the access mode is the low two bits of `Mode`, and OAPPEND is a flag. |
| `Ropen` | 113 | `Ropen(ushort Tag, Qid Qid, uint Iounit)` | Answers a `Topen` with the qid and the iounit the server will honour. |
| `Tcreate` | 114 | `Tcreate(ushort Tag, uint Fid, string Name, uint Perm, byte Mode, string? Extension)` | Creates a file in the directory a fid names and opens the fid onto it. `Extension` is .u only. |
| `Rcreate` | 115 | `Rcreate(ushort Tag, Qid Qid, uint Iounit)` | Answers a `Tcreate` with the qid of the new file and the iounit. |
| `Tstat` | 124 | `Tstat(ushort Tag, uint Fid)` | Asks for the stat record of the file a fid names (§5.8). |
| `Rstat` | 125 | `Rstat(ushort Tag, StatRecord Stat)` | One stat record; the record's size appears twice on the wire. |
| `Twstat` | 126 | `Twstat(ushort Tag, uint Fid, StatRecord Stat)` | Changes a file through a stat record, atomically — all of it or none. A record whose every field is "don't touch" is an fsync request. |
| `Rwstat` | 127 | `Rwstat(ushort Tag)` | Confirms that a file has been changed. |

### The message records — 9P2000.L only (§3.4)

| Record | Type |  C# positional signature | What it is |
| --- | --- | --- | --- |
| `Tstatfs` | 8 | `Tstatfs(ushort Tag, uint Fid)` | Asks for statistics about the filesystem behind a fid (§4.9). |
| `Rstatfs` | 9 | `Rstatfs(ushort Tag, StatFs Stat)` | Answers a `Tstatfs`. |
| `Tlopen` | 12 | `Tlopen(ushort Tag, uint Fid, uint Flags)` | Opens an existing file with Linux open(2) flags. |
| `Rlopen` | 13 | `Rlopen(ushort Tag, Qid Qid, uint Iounit)` | Answers a `Tlopen` with the qid and the iounit. |
| `Tlcreate` | 14 | `Tlcreate(ushort Tag, uint Fid, string Name, uint Flags, uint Mode, uint Gid)` | Creates a file in the directory a fid names and opens the fid onto it. |
| `Rlcreate` | 15 | `Rlcreate(ushort Tag, Qid Qid, uint Iounit)` | Answers a `Tlcreate` with the qid of the new file and the iounit. |
| `Tsymlink` | 16 | `Tsymlink(ushort Tag, uint Fid, string Name, string Symtgt, uint Gid)` | Creates a symbolic link. |
| `Rsymlink` | 17 | `Rsymlink(ushort Tag, Qid Qid)` | Answers a `Tsymlink` with the qid of the new link. |
| `Tmknod` | 18 | `Tmknod(ushort Tag, uint Dfid, string Name, uint Mode, uint Major, uint Minor, uint Gid)` | Creates a device, socket or named pipe. |
| `Rmknod` | 19 | `Rmknod(ushort Tag, Qid Qid)` | Answers a `Tmknod` with the qid of the new node. |
| `Trename` | 20 | `Trename(ushort Tag, uint Fid, uint Dfid, string Name)` | Renames a file into another directory. |
| `Rrename` | 21 | `Rrename(ushort Tag)` | Confirms a rename. |
| `Treadlink` | 22 | `Treadlink(ushort Tag, uint Fid)` | Reads the target of a symbolic link. |
| `Rreadlink` | 23 | `Rreadlink(ushort Tag, string Target)` | Answers a `Treadlink` with the link's target. |
| `Tgetattr` | 24 | `Tgetattr(ushort Tag, uint Fid, GetAttrMask RequestMask)` | Asks for the attributes of a file (§4.6). |
| `Rgetattr` | 25 | `Rgetattr(ushort Tag, GetAttrMask Valid, Qid Qid, uint Mode, uint Uid, uint Gid, ulong NLink, ulong Rdev, ulong Size, ulong BlkSize, ulong Blocks, TimeSpec ATime, TimeSpec MTime, TimeSpec CTime, TimeSpec BTime, ulong Gen, ulong DataVersion)` | Answers a `Tgetattr`; the frame is always 160 bytes and fields not marked valid carry zero. |
| `Tsetattr` | 26 | `Tsetattr(ushort Tag, uint Fid, SetAttrMask Valid, uint Mode, uint Uid, uint Gid, ulong Size, TimeSpec ATime, TimeSpec MTime)` | Changes the attributes of a file; a time bit without its `_SET` twin means "use the server's clock". |
| `Rsetattr` | 27 | `Rsetattr(ushort Tag)` | Confirms an attribute change. |
| `Txattrwalk` | 30 | `Txattrwalk(ushort Tag, uint Fid, uint NewFid, string Name)` | Walks a fid onto an extended attribute, or onto the attribute list (§5.9). |
| `Rxattrwalk` | 31 | `Rxattrwalk(ushort Tag, ulong Size)` | Answers a `Txattrwalk` with the size of the attribute. |
| `Txattrcreate` | 32 | `Txattrcreate(ushort Tag, uint Fid, string Name, ulong AttrSize, XattrFlags Flags)` | Prepares a fid to write an extended attribute. |
| `Rxattrcreate` | 33 | `Rxattrcreate(ushort Tag)` | Confirms that a fid is ready to take an attribute's bytes. |
| `Treaddir` | 40 | `Treaddir(ushort Tag, uint Fid, ulong Offset, uint Count)` | Reads packed directory entries; in .L this is the only way to read a directory. |
| `Rreaddir` | 41 | `Rreaddir(ushort Tag, ReadOnlyMemory<byte> Data)` | Whole directory entries, packed; a count of 0 means the end. |
| `Tfsync` | 50 | `Tfsync(ushort Tag, uint Fid, uint Datasync)` | Commits a file to stable storage. Encoders always emit `datasync[4]` (15 bytes); decoders also accept the 11-byte form hugelgupf/p9 sends, defaulting `Datasync` to 0. |
| `Rfsync` | 51 | `Rfsync(ushort Tag)` | Confirms that a file has been committed. |
| `Tlock` | 52 | `Tlock(ushort Tag, uint Fid, LockRequest Request)` | Acquires or releases a byte-range lock (§4.8). |
| `Rlock` | 53 | `Rlock(ushort Tag, LockStatus Status)` | The outcome; BLOCKED asks the client to retry. |
| `Tgetlock` | 54 | `Tgetlock(ushort Tag, uint Fid, LockType Type, ulong Start, ulong Length, uint ProcId, string ClientId)` | Asks which lock, if any, conflicts with a range. |
| `Rgetlock` | 55 | `Rgetlock(ushort Tag, LockQueryResult Result)` | Answers a `Tgetlock`; a type of `Unlock` means nothing conflicts. |
| `Tlink` | 70 | `Tlink(ushort Tag, uint Dfid, uint Fid, string Name)` | Creates a hard link. |
| `Rlink` | 71 | `Rlink(ushort Tag)` | Confirms that a hard link has been created. |
| `Tmkdir` | 72 | `Tmkdir(ushort Tag, uint Dfid, string Name, uint Mode, uint Gid)` | Creates a directory. |
| `Rmkdir` | 73 | `Rmkdir(ushort Tag, Qid Qid)` | Answers a `Tmkdir` with the qid of the new directory. |
| `Trenameat` | 74 | `Trenameat(ushort Tag, uint OldDirFid, string OldName, uint NewDirFid, string NewName)` | Renames by directory fid and name, without a fid on the file itself. |
| `Rrenameat` | 75 | `Rrenameat(ushort Tag)` | Confirms a rename. |
| `Tunlinkat` | 76 | `Tunlinkat(ushort Tag, uint DirFid, string Name, uint Flags)` | Removes by directory fid and name; `Flags` carries `AT_REMOVEDIR = 0x200`. |
| `Runlinkat` | 77 | `Runlinkat(ushort Tag)` | Confirms a removal. |

## `NineP.Protocol.Codec`

### `MessageCodec`

`static class` — encodes and decodes single 9P frames. This is the whole public surface of the
codec: the streaming frame reader and writer, the pooled leases and the per-primitive readers are
internal, because no public member of this library exposes a pipe, a sequence or a pool.

Decoding is a synchronous function over one complete frame; nothing in it awaits, and it never
allocates in proportion to a *claimed* length — every counted field is bounds-checked against the
remaining bytes before a rental. Integers go through `BinaryPrimitives`, and strings through a
private `UTF8Encoding(false, throwOnInvalidBytes: true)` after a NUL scan, because
`Encoding.UTF8` substitutes U+FFFD instead of failing.

```csharp
public static TMessage Decode<TMessage>(ReadOnlyMemory<byte> frame, Dialect dialect)
    where TMessage : struct, IMessage;
```
Decodes one complete frame, `size[4]` included, into its record; throws `NinePProtocolException`
with a typed kind when the frame is malformed or illegal.

```csharp
public static bool TryDecode<TMessage>(
    ReadOnlyMemory<byte> frame, Dialect dialect,
    out TMessage message, out ProtocolErrorKind failure)
    where TMessage : struct, IMessage;
```
Decodes one complete frame, reporting the failure kind instead of throwing.

```csharp
public static int Encode<TMessage>(IBufferWriter<byte> writer, in TMessage message, Dialect dialect)
    where TMessage : struct, IMessage;
```
Encodes one message, `size[4]` included, into a buffer writer and returns the number of bytes
written; exactly one frame is written and advanced over. A message carrying a stat record that will
not fit its own 16-bit length field throws `NinePException(EOVERFLOW)` — see `StatRecord` above.

```csharp
public static int GetEncodedSize<TMessage>(in TMessage message, Dialect dialect)
    where TMessage : struct, IMessage;
```
The exact encoded length of a message, so a caller can size a buffer for it.

```csharp
public static uint PeekSize(ReadOnlySpan<byte> frame);
```
Reads `size[4]` from a frame without decoding it; the value includes those four bytes.

```csharp
public static MessageType PeekType(ReadOnlySpan<byte> frame);
```
Reads `type[1]` from a frame without decoding it, for the legality check.

```csharp
public static ushort PeekTag(ReadOnlySpan<byte> frame);
```
Reads `tag[2]` from a frame without decoding it, for the flush reserve.

## `NineP.Protocol.Negotiation`

### `NegotiationResult`

`readonly record struct` — the outcome of §5.1: the string to answer with, the msize, and the
dialect it selects. A result whose version is "unknown" carries no dialect and echoes the client's
msize; a server may never answer with a larger one.

```csharp
public readonly record struct NegotiationResult(string Version, uint Msize, Dialect? Dialect);
```

```csharp
public bool IsUnknown { get; }
```
True when `Version` is "unknown": no dialect was agreed.

### `Negotiator`

`static class` — the version negotiation function of §5.1, conditioned on the configured dialect
set. Every step is gated on that set: a dialect the server was told not to speak is never answered
with, however the client asks (R-2). The refusals are all `Rversion "unknown"` because version(5)
forbids `Rerror` for `Tversion` and forbids answering with an msize larger than the client's, which
rules out replying with the floor.

```csharp
public static NegotiationResult Negotiate(
    IReadOnlySet<Dialect> configured, string clientVersion, uint clientMsize, Limits limits);
```
Answers a `Tversion`: what the `Rversion` must carry.

```csharp
public static string VersionString(Dialect dialect);
```
The wire string for a dialect ("9P2000", "9P2000.u", "9P2000.L").

```csharp
public static bool TryParseVersion(string version, out Dialect dialect);
```
Parses an exact dialect string; false for anything else, including "unknown".

## `NineP.Protocol.Transports`

Transports live in `protocol` because both the client and the server need them (architecture §3).
The seam is three interfaces — `ITransport` dials and binds, `INinePListener` accepts,
`INinePConnection` carries bytes — and four implementations of it ship: TCP, TLS, WebSocket and an
in-process memory transport. Implementing `ITransport` is the documented extension point, and
`MemoryTransport` is the proof that the seam is real: every test that can run over it does.

### `NinePScheme`

`enum` — the address schemes a `NinePAddress` may carry (architecture §3).

| Member | Value | Meaning |
| --- | --- | --- |
| `Tcp` | 0 | Plain TCP. |
| `Tls` | 1 | TLS over TCP. |
| `Ws` | 2 | A WebSocket over plain HTTP. |
| `Wss` | 3 | A WebSocket over TLS. |
| `Unix` | 4 | A Unix domain socket, where the platform has them. |
| `Memory` | 5 | The in-process transport, whose "host" is the endpoint's name. |

### `NinePAddress`

`readonly record struct` — a 9P endpoint URL. Parsing is exact: an address this type cannot render
back is not an address it accepts.

```text
tcp://host:port    tls://host:port    ws://host:port/path    wss://host:port/path
unix:///path       memory://name
```

```csharp
public readonly record struct NinePAddress(NinePScheme Scheme, string Host, int Port, string Path);
```
`Host` is the host name or IP literal, the endpoint's name for `memory`, and empty for `unix`.
`Port` is the TCP port; 0 asks a listener for whatever port the kernel picks, and is what `unix`
and `memory` always carry. `Path` is the WebSocket path or the socket path, and is empty otherwise.

```csharp
public static NinePAddress Parse(string value);
```
Parses an address URL; throws `FormatException` naming the offending text.

```csharp
public static bool TryParse(string? value, out NinePAddress address);
```
Parses an address URL, returning false instead of throwing.

```csharp
public override string ToString();
```
The canonical URL form of this address: the text `Parse` accepts and returns this address for.

```csharp
public bool IsSecure { get; }
```
True when the scheme carries TLS: `tls` or `wss`.

### `CloseReason`

`enum` — why a connection closed, carried to the peer where the transport can express it
(architecture §3). A framing violation cannot be resynced, so it is always a close and never a
skipped message.

| Member | Value | Meaning |
| --- | --- | --- |
| `Normal` | 0 | An orderly close initiated by this side. |
| `PeerClosed` | 1 | The peer closed. |
| `ProtocolViolation` | 2 | A frame violated size, framing or dialect rules; the connection cannot be resynced. |
| `MessageTooLarge` | 3 | A frame exceeded the negotiated msize or the pre-negotiation cap. |
| `Timeout` | 4 | A timeout elapsed: read-header, idle, or authentication. |
| `ResourceLimit` | 5 | A limit was exceeded: connections, fids, or in-flight requests. |
| `Shutdown` | 6 | The server is shutting down. |
| `TransportError` | 7 | An unexpected I/O or transport failure. |

### `PeerIdentity`

`sealed record` — what the transport learned about the peer during its handshake: a TLS client
certificate, the headers of a WebSocket upgrade. It is evidence the transport gathered, never a
claim the peer made in a 9P message — the session's identity still comes from the authenticator.

```csharp
public X509Certificate2? ClientCertificate { get; init; }
```
The peer's TLS client certificate when mutual TLS was used; null otherwise.

```csharp
public string? Origin { get; init; }
```
The `Origin` header of a WebSocket upgrade; null for other transports.

```csharp
public IReadOnlyDictionary<string, string> Headers { get; init; }
```
Selected headers of a WebSocket upgrade, with lower-cased keys; empty otherwise.

```csharp
public string? RemoteAddress { get; init; }
```
The peer's network address as text, for logging.

### `INinePConnection`

`interface : IAsyncDisposable` — one bidirectional 9P byte stream. `WriteAsync` is called once per
complete 9P message, so a message-oriented transport can put each message in one frame of its own.
There is no "preserves message boundaries" flag (architecture §12 rule 6): nothing consumed one,
and the frame reader re-frames every transport alike from the `size[4]` field.

```csharp
NinePAddress RemoteAddress { get; }
```
The address of the peer.

```csharp
PeerIdentity? PeerIdentity { get; }
```
What the transport learned about the peer during the handshake; null when nothing.

```csharp
ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
```
Reads up to `buffer.Length` bytes; returns zero at the end of the stream.

```csharp
ValueTask WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);
```
Writes exactly one complete 9P message, `size[4]` included.

```csharp
ValueTask CloseAsync(CloseReason reason, CancellationToken cancellationToken = default);
```
Closes with a reason; the reason reaches the peer where the transport can express it.

### `INinePListener`

`interface : IAsyncDisposable` — a listening endpoint that accepts 9P connections.

```csharp
NinePAddress LocalAddress { get; }
```
The address actually bound, carrying the real port when 0 was requested.

```csharp
ValueTask<INinePConnection?> AcceptAsync(CancellationToken cancellationToken = default);
```
Accepts the next connection, or returns null once the listener has been disposed.

### `ITransport`

`interface` — the user-defined-transport seam (architecture §3): dial and listen.

```csharp
IReadOnlyCollection<NinePScheme> Schemes { get; }
```
The schemes this transport can dial and bind.

```csharp
ValueTask<INinePConnection> ConnectAsync(
    NinePAddress address, CancellationToken cancellationToken = default);
```
Dials an address; the caller disposes the connection.

```csharp
ValueTask<INinePListener> ListenAsync(
    NinePAddress address, CancellationToken cancellationToken = default);
```
Binds an address; the caller disposes the listener.

### `TcpTransport`

`sealed class : ITransport` — TCP with `TCP_NODELAY` and keep-alive on, an accept backlog and a
per-listener connection cap. The cap **stops accepting**: a connection over the limit waits in the
kernel's backlog and is served when a slot frees, rather than being accepted and reset, which a
client cannot tell from a crash.

```csharp
public TcpTransport(TcpTransportOptions? options = null);
public IReadOnlyCollection<NinePScheme> Schemes { get; }
public ValueTask<INinePConnection> ConnectAsync(
    NinePAddress address, CancellationToken cancellationToken = default);
public ValueTask<INinePListener> ListenAsync(
    NinePAddress address, CancellationToken cancellationToken = default);
```
The constructor takes its options record, defaulting them when null; the three members are the
`ITransport` implementation, dialling and binding a TCP endpoint.

### `TcpTransportOptions`

`sealed record` — TCP transport tuning (architecture §3).

```csharp
public bool NoDelay { get; init; } = true;
```
Disable Nagle. Default true: 9P is a request/response protocol whose messages are small and whose
latency is the thing being measured.

```csharp
public bool KeepAlive { get; init; } = true;
```
Enable TCP keep-alive. Default true.

```csharp
public int Backlog { get; init; } = 128;
```
The listen backlog. Default 128.

```csharp
public int MaxConnections { get; init; } = 1024;
```
Accepted connections held at once; further accepts wait. Default 1024.

```csharp
public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
```
The dial timeout. Default 30 s.

```csharp
public ILogger? Logger { get; init; }
```
Where a transient accept failure is reported; `NullLogger.Instance` when null. An accept that failed
and was retried is the one thing this transport has to say, and a listener that says nothing about
it hides a machine running out of descriptors.

### `TlsTransport`

`sealed class : ITransport` — TLS 1.2 / 1.3 over TCP, with chain and host-name verification on by
default and optional mutual TLS. The insecure opt-out is a separate explicit option that logs on
every connect, and `AdditionalPeerCheck` can only ever *add* a check: it runs after verification
has already passed, so no callback can turn a bad certificate into a good one. Closing sends
`close_notify` before the socket goes away.

```csharp
public TlsTransport(TlsTransportOptions options);
public IReadOnlyCollection<NinePScheme> Schemes { get; }
public ValueTask<INinePConnection> ConnectAsync(
    NinePAddress address, CancellationToken cancellationToken = default);
public ValueTask<INinePListener> ListenAsync(
    NinePAddress address, CancellationToken cancellationToken = default);
```
The options record is required rather than optional, because a TLS transport with no configuration
can neither present a certificate nor be told which roots to trust. `ConnectAsync` completes the
handshake before returning.

### `TlsTransportOptions`

`sealed record` — TLS configuration.

```csharp
public X509Certificate2? ServerCertificate { get; init; }
```
The server certificate with its private key; required in order to listen.

```csharp
public X509Certificate2Collection? ClientCertificates { get; init; }
```
Client certificates offered when dialling; supplying them enables mutual TLS.

```csharp
public bool RequireClientCertificate { get; init; }
```
Require a client certificate when listening; it becomes `PeerIdentity.ClientCertificate`.

```csharp
public X509Certificate2Collection? TrustedRoots { get; init; }
```
Extra roots used to validate the peer's chain; the platform store is used when null.

```csharp
public string? TargetHost { get; init; }
```
The host name verified against the server certificate; the dialled host when null.

```csharp
public bool AllowInsecureCertificates { get; init; }
```
Disables chain and host-name verification **when dialling**, and logs a warning on every connect.
Default false, and there is no way to reach it by accident: it is not implied by any other setting.
It is client-side only: a listener with `RequireClientCertificate` still verifies the certificate a
client presents, and one configured with this flag logs that it is being ignored.

```csharp
public Func<X509Certificate2, bool>? AdditionalPeerCheck { get; init; }
```
A callback that may only **add** checks: it is consulted after the chain and the host name have
been verified, so returning true never rescues a certificate that failed them.

```csharp
public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);
```
The handshake timeout. Default 30 s.

```csharp
public TcpTransportOptions? Tcp { get; init; }
```
The transport used underneath; a default `TcpTransport` when null.

```csharp
public ILogger? Logger { get; init; }
```
Where the insecure-mode warning goes; `NullLogger.Instance` when null.

### `WebSocketTransport`

`sealed class : ITransport` — RFC 6455 WebSocket: one 9P message per binary WebSocket message.
Fragmentation stays inside WebSocket, the size cap is enforced while a message is being
reassembled, a text message is a protocol error, and the server checks the `Origin` allow-list
before the upgrade completes.

```csharp
public WebSocketTransport(WebSocketTransportOptions? options = null);
public IReadOnlyCollection<NinePScheme> Schemes { get; }
public ValueTask<INinePConnection> ConnectAsync(
    NinePAddress address, CancellationToken cancellationToken = default);
public ValueTask<INinePListener> ListenAsync(
    NinePAddress address, CancellationToken cancellationToken = default);
```
`ConnectAsync` completes the upgrade before returning.

### `WebSocketTransportOptions`

`sealed record` — WebSocket configuration. One 9P message travels as one binary WebSocket message,
so the size cap here bounds a 9P frame directly.

```csharp
public string? Subprotocol { get; init; } = Constants.WebSocketSubprotocol;
```
The subprotocol offered and echoed. Default "9p"; null offers none.

```csharp
public IReadOnlyCollection<string> AllowedOrigins { get; init; }
```
Origins the server accepts. Empty means "accept any", which is logged as a warning at listen time:
a browser-reachable 9P server with no allow-list is a cross-origin hole.

```csharp
public int MaxMessageSize { get; init; } = 1024 * 1024;
```
The largest message accepted, enforced **during** accumulation rather than at the end: a peer must
not be able to make the server buffer a gigabyte before it is refused. Over the cap the socket
closes 1009. Default 1 MiB.

```csharp
public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);
```
The ping interval. Default 30 s.

```csharp
public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);
```
The handshake timeout. Default 30 s.

```csharp
public TlsTransportOptions? Tls { get; init; }
```
TLS settings for `wss://`; ignored for `ws://`.

```csharp
public TcpTransportOptions? Tcp { get; init; }
```
The underlying TCP tuning.

```csharp
public ILogger? Logger { get; init; }
```
Where handshake refusals are logged; `NullLogger.Instance` when null.

### `MemoryTransport`

`sealed class : ITransport` — an in-process transport over duplex pipes (architecture §3). Each
instance is isolated — two transports never share an endpoint name — and a close reason given to
one end is visible at the other.

```csharp
public MemoryTransport();
```
Creates an isolated in-process transport; its addresses are `memory://name`.

```csharp
public static (INinePConnection Client, INinePConnection Server) CreatePair();
```
Creates a connected client/server pair without a listener, for codec-level tests.

```csharp
public IReadOnlyCollection<NinePScheme> Schemes { get; }
public ValueTask<INinePConnection> ConnectAsync(
    NinePAddress address, CancellationToken cancellationToken = default);
public ValueTask<INinePListener> ListenAsync(
    NinePAddress address, CancellationToken cancellationToken = default);
```
The `ITransport` implementation, dialling and binding an in-process endpoint.

## `NineP.Protocol.Auth`

The protocol shape is §5.2: `Tauth` opens an afid, the client writes a credential and may read a
response, then presents the afid in `Tattach`. What flows over the afid is not 9P's business, so
this namespace defines it: `IAuthenticator` on the server, `ICredential` on the client, and one
`Identity` that the server trusts instead of the `uname` the client claimed.

### `Identity`

`sealed record` — who a session runs as. It is produced by the authenticator and never by the
client's claim (architecture §5): a `Tattach` may say any `uname` it likes, and the server still
runs the session as whoever the afid exchange proved.

```csharp
public required string User { get; init; }
```
The user name the session runs as.

```csharp
public uint Uid { get; init; } = Constants.NONUNAME;
```
The numeric uid when one is known; `NONUNAME` otherwise — the wire sentinel rather than a
nullable, as for every optional id (architecture §12 rule 2).

```csharp
public IReadOnlyList<string> Groups { get; init; }
```
Group names this identity belongs to.

```csharp
public IReadOnlyDictionary<string, string> Claims { get; init; }
```
Authenticator-specific claims, for example OIDC token claims. It never contains the raw token: a
claim map is logged and echoed, and a bearer token must be neither.

```csharp
public bool IsAuthenticated { get; }
```
False when nothing was proved and `User` is only what the client claimed — the identity of a
`Tattach` with `afid = NOFID`. A filesystem that scopes anything by the user name must read this
first: a server that allows unauthenticated attaches at all will otherwise hand out a named user's
tree to whoever asks for it by name. Only `Anonymous` produces false, so an identity an
authenticator built is authenticated by construction.

```csharp
public static Identity Anonymous(string user, uint uid = Constants.NONUNAME);
```
The identity of an unauthenticated attach: the uname as claimed, with no groups — only as
trustworthy as the transport under it.

### `AuthRequest`

`readonly record struct` — the triple a `Tauth` binds an afid to (§5.2). Binding on `uname` and
`aname` alone would let an afid opened for one numeric user satisfy an attach claiming another in a
.u or .L session.

```csharp
public readonly record struct AuthRequest(string Uname, uint NUname, string Aname);
```
An empty `Uname` and a `NUname` of NONUNAME both mean "unspecified".

### `IAuthSession`

`interface : IAsyncDisposable` — one in-progress afid exchange. The core bounds it at 64 KiB and
30 s (architecture §5).

```csharp
Identity? Identity { get; }
```
The identity once the exchange has succeeded; null until then, and an attach presenting an afid
whose session is still null fails EACCES.

```csharp
ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
```
Receives bytes the client wrote to the afid.

```csharp
ValueTask<ReadOnlyMemory<byte>> ReadAsync(
    int maxBytes, CancellationToken cancellationToken = default);
```
Produces bytes for the client to read from the afid; empty means the end of the exchange.

### `IAuthenticator`

`interface` — server-side authentication (architecture §5).

```csharp
bool IsRequired { get; }
```
True when an attach must present a verified afid; false lets NOFID attaches through.

```csharp
ValueTask<IAuthSession?> BeginAsync(
    AuthRequest request, PeerIdentity? peer, CancellationToken cancellationToken = default);
```
Starts an afid exchange, or returns null to refuse the `Tauth` outright.

### `IAuthChannel`

`interface` — the client's read/write view of an afid during `AttachAsync`.

```csharp
ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
```
Writes credential bytes to the afid.

```csharp
ValueTask<ReadOnlyMemory<byte>> ReadAsync(
    int maxBytes, CancellationToken cancellationToken = default);
```
Reads the server's answer from the afid; empty at the end of the exchange.

### `ICredential`

`interface` — client-side credential: it drives the afid exchange from inside `AttachAsync`, so a
caller never writes protocol code to authenticate (architecture §5).

```csharp
ValueTask AuthenticateAsync(IAuthChannel channel, CancellationToken cancellationToken = default);
```
Runs the client half of the exchange over the afid; throws `NinePException` when the server refused
the credential.

### `TokenAuthenticator`

`sealed class : IAuthenticator` — default authentication: the client writes an opaque token to the
afid once, the server compares it in constant time and answers `ok\n` (architecture §5). Plan 9's
`p9any`/`p9sk1` is deliberately out of scope: it is DES-based and is not production security.

```csharp
public TokenAuthenticator(ReadOnlyMemory<byte> secret);
```
Verifies against one shared secret; the identity is the claimed uname.

```csharp
public TokenAuthenticator(Func<AuthRequest, ReadOnlyMemory<byte>?> secretLookup);
```
Verifies against a per-user secret lookup; the identity is the resolved user.

```csharp
public bool IsRequired { get; }
```
True: an attach must present a verified afid.

```csharp
public ValueTask<IAuthSession?> BeginAsync(
    AuthRequest request, PeerIdentity? peer, CancellationToken cancellationToken = default);
```
Starts an exchange, or refuses a request the lookup has no secret for.

### `PasswordAuthenticator`

`sealed class : IAuthenticator` — default authentication: `user\npassword\n` over the afid,
verified against a hashed store.

```csharp
public PasswordAuthenticator(IPasswordStore store);
```
Creates an authenticator over a credential store.

```csharp
public bool IsRequired { get; }
```
True: an attach must present a verified afid.

```csharp
public ValueTask<IAuthSession?> BeginAsync(
    AuthRequest request, PeerIdentity? peer, CancellationToken cancellationToken = default);
```
Starts an exchange for any request; the store decides who succeeds. The peer identity is unused: a
password proves the client, not the transport.

### `IPasswordStore`

`interface` — a store of hashed credentials for `PasswordAuthenticator`.

```csharp
ValueTask<Identity?> VerifyAsync(
    string user, ReadOnlyMemory<byte> password, CancellationToken cancellationToken = default);
```
Verifies a password in constant time and returns the identity, or null when the credential is wrong
or the user is unknown.

### `PasswordFileStore`

`sealed class : IPasswordStore` — a password file of `user:pbkdf2-sha256$iterations$salt$hash`
lines. The BCL has no argon2id and no vetted .NET package for it exists, so the architecture's
stated fallback — PBKDF2-HMAC-SHA-256 at 600 000 iterations — is what ships.

```csharp
public const int MinimumIterations = 600_000;
```
The iteration count this workspace requires (S-15).

```csharp
public const string Algorithm = "pbkdf2-sha256";
```
The algorithm label every stored line begins with.

```csharp
public static PasswordFileStore Load(string path);
```
Loads a password file; throws `FormatException` naming the first bad line's number.

```csharp
public static string HashPassword(string password, int iterations = MinimumIterations);
```
Hashes a password into the value half of a file line; throws `ArgumentOutOfRangeException` below
the minimum iteration count.

```csharp
public ValueTask<Identity?> VerifyAsync(
    string user, ReadOnlyMemory<byte> password, CancellationToken cancellationToken = default);
```
Verifies a password in constant time.

### `TlsClientCertAuthenticator`

`sealed class : IAuthenticator` — derives the identity from the peer's TLS client certificate
(architecture §5). There is nothing to exchange over the afid: mutual TLS already proved who the
peer is before the first 9P byte, so the session is authenticated the moment it begins — and an
attach that claims a different `uname` is refused rather than quietly renamed.

```csharp
public TlsClientCertAuthenticator(Func<X509Certificate2, Identity?>? mapper = null);
```
Creates an authenticator over a certificate-to-identity mapping; the default takes the first DNS
SAN, else the CN.

```csharp
public static Identity? DefaultMapper(X509Certificate2 certificate);
```
The identity the default mapping derives: the first DNS SAN, else the common name; null when the
certificate names nobody.

```csharp
public bool IsRequired { get; }
```
True: an attach must present a verified afid.

```csharp
public ValueTask<IAuthSession?> BeginAsync(
    AuthRequest request, PeerIdentity? peer, CancellationToken cancellationToken = default);
```
Authenticates from the certificate the transport already validated; null when there is no
certificate or the uname disagrees.

### `TokenCredential`

`sealed class : ICredential` — writes an opaque token to the afid and expects `ok\n`; the mirror of
`TokenAuthenticator`.

```csharp
public TokenCredential(ReadOnlyMemory<byte> token);
public ValueTask AuthenticateAsync(
    IAuthChannel channel, CancellationToken cancellationToken = default);
```

### `PasswordCredential`

`sealed class : ICredential` — writes `user\npassword\n` to the afid; the mirror of
`PasswordAuthenticator`.

```csharp
public PasswordCredential(string user, string password);
public ValueTask AuthenticateAsync(
    IAuthChannel channel, CancellationToken cancellationToken = default);
```

### `BearerTokenCredential`

`sealed class : ICredential` — writes an OIDC access token the caller already holds (S-30). The
packages never perform a login: acquiring a token is the cli's job, and a library that could log in
would need to hold a client secret.

```csharp
public BearerTokenCredential(string accessToken);
```
Uses a fixed token.

```csharp
public BearerTokenCredential(Func<CancellationToken, ValueTask<string>> tokenProvider);
```
Fetches the token per attach, so a caller can refresh it out of band.

```csharp
public ValueTask AuthenticateAsync(
    IAuthChannel channel, CancellationToken cancellationToken = default);
```
Writes the token and waits for the acknowledgement.

### `CallbackCredential`

`sealed class : ICredential` — an arbitrary exchange written by the caller: the client-side
extension point.

```csharp
public CallbackCredential(Func<IAuthChannel, CancellationToken, ValueTask> exchange);
public ValueTask AuthenticateAsync(
    IAuthChannel channel, CancellationToken cancellationToken = default);
```

### `ConstantTime`

`static class` — constant-time comparison. This is the **only** byte-comparison path for secrets in
this library (architecture §8.3), and a test greps the authentication sources to keep it that way:
a comparison that returns early on the first differing byte leaks the secret one byte at a time to
anyone who can measure the answer.

```csharp
public static bool Equals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right);
```
True when both spans are equal, in time independent of where they differ.

## `NineP.Client`

Five types. `NinePClient` connects and negotiates; `NinePSession` is the negotiated session and the
path-based file API; `NinePFid` is one handle on one file; `INinePMessages` is the wire underneath,
one method per legal T-message; `ClientOptions` configures all of it. The session is fully
pipelined: a tag multiplexer routes replies by tag, cancellation of a request becomes a `Tflush`
with the exact flush(5) rules, and large transfers issue several requests at once.

### `NinePClient`

`static class` — the entry point: connect a transport, negotiate a dialect, and return a session
(architecture §6). Negotiation is one `Tversion` with `NOTAG`; the client never silently accepts
less than it asked for.

```csharp
public static ValueTask<NinePSession> ConnectAsync(
    NinePAddress address, ClientOptions options, CancellationToken cancellationToken = default);
```
Connects to an address using the transport that owns its scheme, then negotiates.

```csharp
public static ValueTask<NinePSession> ConnectAsync(
    ITransport transport, NinePAddress address, ClientOptions options,
    CancellationToken cancellationToken = default);
```
Connects over a caller-supplied transport: the user-defined-transport seam.

```csharp
public static ValueTask<NinePSession> ConnectAsync(
    INinePConnection connection, ClientOptions options,
    CancellationToken cancellationToken = default);
```
Negotiates over a connection the caller already established.

### `ClientOptions`

`sealed record` — everything a client session needs; init-only, with the defaults of architecture
§6.

```csharp
public const uint DefaultLinuxMsize = 1024 * 1024;
```
The msize a .L session asks for by default: 1 MiB (§5.1).

```csharp
public const uint DefaultLegacyMsize = 128 * 1024;
```
The msize a 9P2000 or .u session asks for by default: 128 KiB.

```csharp
public IReadOnlyList<Dialect> Dialects { get; init; }
```
Dialects to offer, most preferred first. Default: .L, .u, 9P2000. An `Rversion "unknown"` moves on
to the next entry, so the list is walked and not just read for its head.

```csharp
public Dialect MinDialect { get; init; } = Dialect.P9_2000;
```
The least dialect the caller accepts; a lower answer throws `NinePVersionException` rather than
silently degrading the session.

```csharp
public int MaxReadAll { get; init; } = 256 * 1024 * 1024;
```
Maximum bytes returned by `ReadAllAsync`, `ReadFileAsync` and whole-xattr reads. Metadata
preflight and incremental enforcement raise EFBIG above the cap. Zero permits empty values;
negative values and values above `Array.MaxLength` are rejected before connecting. Use bounded
`ReadAsync` buffers for larger files. The accumulator and final array may coexist temporarily.

```csharp
public uint? Msize { get; init; }
```
The msize to request; the dialect's default when null.

```csharp
public ICredential? Credential { get; init; }
```
The credential driving the afid exchange; a null one attaches with NOFID.

```csharp
public string Uname { get; init; } = "";
```
The textual user name sent in `Tauth` and `Tattach`. Default "".

```csharp
public uint NUname { get; init; } = Constants.NONUNAME;
```
The numeric user id sent in .u and .L. Default NONUNAME.

```csharp
public string Aname { get; init; } = "";
```
The tree name sent in `Tauth` and `Tattach`. Default "".

```csharp
public int InFlightWindow { get; init; } = 4;
```
Requests kept outstanding during a chunked transfer. Default 4 — this is where throughput comes
from on a single stream.

```csharp
public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
```
How long one request may take before it is flushed and cancelled. Default 60 s.

```csharp
public TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(5);
```
How long `NinePSession.DisposeAsync` may spend clunking the fids that are still open — in total,
not per fid. Default 5 s. The clunks are issued together and the effective bound is the smaller of
this and `RequestTimeout`; anything the peer has not answered by then is abandoned, because closing
the transport makes the server forget those fids anyway. Zero means no bound.

```csharp
public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
```
How long `ConnectAsync` may take, handshake and `Tversion` included. Default 30 s.

```csharp
public Limits Limits { get; init; } = Limits.Default;
```
The bounds this side enforces on incoming frames.

```csharp
public ILogger Logger { get; init; } = NullLogger.Instance;
```
Where the session logs.

There is no clock option: every client timeout runs on the system clock. `ServerOptions` keeps its
`TimeProvider`, which the server reads for qid versions and server-set times.

### `NinePSession`

`sealed class : IAsyncDisposable` — a negotiated 9P session: the tag multiplexer, the attach root
and the high-level file API. Every request is pipelined — the session never waits for one reply
before writing the next — and every fid it hands out is released deterministically, by disposal
rather than by collection.

```csharp
public Dialect Dialect { get; }
```
The dialect that was negotiated.

```csharp
public uint Msize { get; }
```
The msize both sides agreed on.

```csharp
public int MaxPayload { get; }
```
The largest payload one `Tread` or `Twrite` carries: `msize - IOHDRSZ`.

```csharp
public NinePFid Root { get; }
```
The attach root, available after `AttachAsync`; before it, reading this throws
`InvalidOperationException`.

```csharp
public INinePMessages Messages { get; }
```
The typed one-method-per-T-message API, for callers that want the wire directly.

```csharp
public ValueTask<NinePFid> AttachAsync(CancellationToken cancellationToken = default);
```
Runs the afid exchange from `ClientOptions.Credential`, attaches, and returns the root fid, which
is also published as `Root`.

```csharp
public ValueTask<NinePFid> AttachAsync(
    string uname, string aname, ICredential? credential = null,
    CancellationToken cancellationToken = default);
```
Attaches a second tree or a second user on the same connection.

```csharp
public ValueTask<NinePFid> WalkAsync(string path, CancellationToken cancellationToken = default);
```
Walks a slash-separated path from the root and returns the fid; the caller disposes it.

```csharp
public ValueTask<NinePFid> OpenFileAsync(
    string path, OpenMode mode, OpenFlags flags = OpenFlags.None,
    CancellationToken cancellationToken = default);
```
Opens a file by path and returns an open fid.

```csharp
public ValueTask<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken = default);
```
Reads a whole file, chunked at the payload maximum with the in-flight window.

```csharp
public ValueTask WriteFileAsync(
    string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
```
Replaces a file's contents, truncating first.

```csharp
public ValueTask<IReadOnlyList<DirEntry>> ReadDirAsync(
    string path, CancellationToken cancellationToken = default);
```
Lists a directory as unified entries, in both dialect record formats.

```csharp
public ValueTask MkdirAsync(
    string path, uint perm = 0x1ED, CancellationToken cancellationToken = default);
```
Creates a directory; the default permission is 0755.

```csharp
public ValueTask<NinePFid> CreateFileAsync(
    string path, uint perm = 0x1A4, CancellationToken cancellationToken = default);
```
Creates an empty regular file and returns it open for writing; the default permission is 0644.

```csharp
public ValueTask RemoveAsync(string path, CancellationToken cancellationToken = default);
```
Removes a file or an empty directory.

```csharp
public ValueTask RenameAsync(
    string oldPath, string newPath, CancellationToken cancellationToken = default);
```
Renames or moves a path: `Trenameat` in .L, `Twstat.name` otherwise.

```csharp
public ValueTask<Attr> GetAttrAsync(string path, CancellationToken cancellationToken = default);
```
Fetches the unified attributes of a path.

```csharp
public ValueTask SetAttrAsync(
    string path, SetAttr update, CancellationToken cancellationToken = default);
```
Applies a partial attribute update to a path.

```csharp
public ValueTask SymlinkAsync(
    string path, string target, CancellationToken cancellationToken = default);
```
Creates a symbolic link (.L only).

```csharp
public ValueTask<string> ReadlinkAsync(string path, CancellationToken cancellationToken = default);
```
Reads a symbolic link's target: `Treadlink` in .L, the stat extension in .u.

```csharp
public ValueTask<StatFs> StatFsAsync(string path, CancellationToken cancellationToken = default);
```
Filesystem statistics (.L only).

```csharp
public ValueTask DisposeAsync();
```
Clunks every outstanding fid and closes the transport.

### `INinePMessages`

`interface` — one method per legal T-message: the low-level API (architecture §6). The tag is
chosen by the multiplexer, so the request a caller builds may leave it at zero. A method whose
message is illegal for the session dialect throws `NinePProtocolException` with
`ProtocolErrorKind.Type` **before** anything reaches the wire.

There are exactly 32 methods — one per legal T-message, 13 base and 19 `.L` — which with the 34
R-types accounts for the 66 legal type numbers. Every one has the same shape: it takes the request
record and returns the reply record, and cancelling it sends a `Tflush`. A server error becomes a
`NinePException`, never a returned `Rerror`.

```csharp
ValueTask<Rversion> VersionAsync(Tversion request, CancellationToken cancellationToken = default);
ValueTask<Rauth> AuthAsync(Tauth request, CancellationToken cancellationToken = default);
ValueTask<Rattach> AttachAsync(Tattach request, CancellationToken cancellationToken = default);
ValueTask<Rflush> FlushAsync(Tflush request, CancellationToken cancellationToken = default);
ValueTask<Rwalk> WalkAsync(Twalk request, CancellationToken cancellationToken = default);
ValueTask<Ropen> OpenAsync(Topen request, CancellationToken cancellationToken = default);
ValueTask<Rcreate> CreateAsync(Tcreate request, CancellationToken cancellationToken = default);
ValueTask<Rread> ReadAsync(Tread request, CancellationToken cancellationToken = default);
ValueTask<Rwrite> WriteAsync(Twrite request, CancellationToken cancellationToken = default);
ValueTask<Rclunk> ClunkAsync(Tclunk request, CancellationToken cancellationToken = default);
ValueTask<Rremove> RemoveAsync(Tremove request, CancellationToken cancellationToken = default);
ValueTask<Rstat> StatAsync(Tstat request, CancellationToken cancellationToken = default);
ValueTask<Rwstat> WstatAsync(Twstat request, CancellationToken cancellationToken = default);
ValueTask<Rstatfs> StatfsAsync(Tstatfs request, CancellationToken cancellationToken = default);
ValueTask<Rlopen> LopenAsync(Tlopen request, CancellationToken cancellationToken = default);
ValueTask<Rlcreate> LcreateAsync(Tlcreate request, CancellationToken cancellationToken = default);
ValueTask<Rsymlink> SymlinkAsync(Tsymlink request, CancellationToken cancellationToken = default);
ValueTask<Rmknod> MknodAsync(Tmknod request, CancellationToken cancellationToken = default);
ValueTask<Rrename> RenameAsync(Trename request, CancellationToken cancellationToken = default);
ValueTask<Rreadlink> ReadlinkAsync(Treadlink request, CancellationToken cancellationToken = default);
ValueTask<Rgetattr> GetattrAsync(Tgetattr request, CancellationToken cancellationToken = default);
ValueTask<Rsetattr> SetattrAsync(Tsetattr request, CancellationToken cancellationToken = default);
ValueTask<Rxattrwalk> XattrwalkAsync(Txattrwalk request, CancellationToken cancellationToken = default);
ValueTask<Rxattrcreate> XattrcreateAsync(Txattrcreate request, CancellationToken cancellationToken = default);
ValueTask<Rreaddir> ReaddirAsync(Treaddir request, CancellationToken cancellationToken = default);
ValueTask<Rfsync> FsyncAsync(Tfsync request, CancellationToken cancellationToken = default);
ValueTask<Rlock> LockAsync(Tlock request, CancellationToken cancellationToken = default);
ValueTask<Rgetlock> GetlockAsync(Tgetlock request, CancellationToken cancellationToken = default);
ValueTask<Rlink> LinkAsync(Tlink request, CancellationToken cancellationToken = default);
ValueTask<Rmkdir> MkdirAsync(Tmkdir request, CancellationToken cancellationToken = default);
ValueTask<Rrenameat> RenameatAsync(Trenameat request, CancellationToken cancellationToken = default);
ValueTask<Runlinkat> UnlinkatAsync(Tunlinkat request, CancellationToken cancellationToken = default);
```

### `NinePFid`

`sealed class : IAsyncDisposable` — a client fid: the handle a session holds on one file
(architecture §6). Disposing it clunks the fid, which is what makes the release deterministic — a
fid that is only collected when a finaliser runs is a fid the server still holds a handler open
for.

```csharp
public uint Fid { get; }
```
The numeric fid, for tracing.

```csharp
public Qid Qid { get; }
```
The qid this fid refers to.

```csharp
public bool IsOpen { get; }
```
True once the fid has been opened.

```csharp
public int Iounit { get; }
```
The iounit the server reported at open, or `msize - IOHDRSZ` when it reported 0 (§5.5).

```csharp
public ValueTask<NinePFid> WalkAsync(
    IReadOnlyList<string> names, CancellationToken cancellationToken = default);
```
Walks elements from this fid into a fresh fid; a partial walk throws `NinePException`.

```csharp
public ValueTask<NinePFid> CloneAsync(CancellationToken cancellationToken = default);
```
Clones this fid without walking (`nwname == 0`); the fid must not be open (walk(5)).

```csharp
public ValueTask OpenAsync(
    OpenMode mode, OpenFlags flags = OpenFlags.None, CancellationToken cancellationToken = default);
```
Opens this fid: `Topen` in 9P2000 and .u, `Tlopen` in .L.

```csharp
public ValueTask CreateAsync(
    string name, uint perm, OpenMode mode, OpenFlags flags = OpenFlags.None,
    CancellationToken cancellationToken = default);
```
Creates a file in this directory fid; the fid then refers to the new, open file.

```csharp
public ValueTask<int> ReadAsync(
    ulong offset, Memory<byte> buffer, CancellationToken cancellationToken = default);
```
Reads at an offset into the caller's buffer; returns the byte count, 0 at end of file.

```csharp
public ValueTask<int> WriteAsync(
    ulong offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
```
Writes at an offset; returns the byte count the server reported.

```csharp
public ValueTask<byte[]> ReadAllAsync(CancellationToken cancellationToken = default);
```
Reads the whole file, chunked with the in-flight window.

```csharp
public ValueTask WriteAllAsync(
    ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
```
Writes the whole buffer, chunked with the in-flight window.

```csharp
public IAsyncEnumerable<DirEntry> ReadDirAsync(CancellationToken cancellationToken = default);
```
Enumerates directory entries, unifying stat records in 9P2000 and .u with dirents in .L. Neither
format ever yields "." or ".." to the caller.

```csharp
public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default);
```
Fetches unified attributes: `Tstat` in 9P2000 and .u, `Tgetattr` in .L.

```csharp
public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default);
```
Applies a partial update: `Twstat` in 9P2000 and .u, `Tsetattr` in .L.

```csharp
public ValueTask RemoveAsync(CancellationToken cancellationToken = default);
```
Removes the file this fid names; the fid is freed even when the remove fails.

```csharp
public ValueTask FsyncAsync(bool dataOnly = false, CancellationToken cancellationToken = default);
```
Flushes the file to stable storage: `Tfsync` in .L, a don't-touch `Twstat` in 9P2000 and .u.

```csharp
public ValueTask<LockStatus> LockAsync(
    LockRequest request, CancellationToken cancellationToken = default);
```
Acquires or releases a byte-range lock (.L only).

```csharp
public ValueTask<LockQueryResult> GetLockAsync(
    LockRequest request, CancellationToken cancellationToken = default);
```
Tests a byte-range lock (.L only).

```csharp
public ValueTask<byte[]> GetXattrAsync(string name, CancellationToken cancellationToken = default);
```
Reads one extended attribute; an empty name returns the packed name list (.L only).

```csharp
public ValueTask SetXattrAsync(
    string name, ReadOnlyMemory<byte> value, XattrFlags flags = XattrFlags.None,
    CancellationToken cancellationToken = default);
```
Writes one extended attribute (.L only).

```csharp
public ValueTask DisposeAsync();
```
Clunks the fid. Idempotent, and safe to call from a `finally`.

## `NineP.Server`

Seventeen types, of which twelve are the handler model. The developer never writes protocol code:
they supply an `IFilesystem` of typed handlers and the core owns negotiation, the fid table, the
tag table, `Tflush`, walk semantics, open state, directory packing, `iounit`, msize clamping,
attach identity binding, dialect projection and the permission checks.

### `NinePServer`

`sealed class : IAsyncDisposable` — a 9P server: listeners, dialects, authenticator, limits, logger
and clock (architecture §4).

```csharp
public NinePServer(ServerOptions options);
```
Creates a server from a validated configuration.

```csharp
public IReadOnlyList<NinePAddress> Endpoints { get; }
```
Addresses actually bound, with real ports; valid once serving has started.

```csharp
public ServerCounters Counters { get; }
```
Counters for messages, errors, bytes and connections (architecture §4).

```csharp
public ValueTask ListeningAsync(CancellationToken cancellationToken = default);
```
Waits until every configured address is bound, after which `Endpoints` carries the real ports.
`ServeAsync` runs until the server stops, so a caller that wants the bound address starts it,
awaits this, and reads `Endpoints`.

```csharp
public Task ServeAsync(IFilesystem filesystem, CancellationToken cancellationToken = default);
```
Binds every configured listener and serves until the token fires or `StopAsync` is called. This is
the one member that returns `Task` rather than `ValueTask`, because it is a long-running operation
a caller stores and awaits once rather than a request.

```csharp
public ValueTask StopAsync(TimeSpan graceful, CancellationToken cancellationToken = default);
```
Stops accepting, lets in-flight requests finish within the deadline, then closes.

```csharp
public ValueTask DisposeAsync();
```
Stops immediately and releases every listener and connection.

### `ServerOptions`

`sealed record` — everything a server needs; init-only, with the defaults of architecture §4.

```csharp
public required IReadOnlyList<NinePAddress> Listen { get; init; }
```
Addresses to bind. At least one is required.

```csharp
public IReadOnlyList<ITransport> Transports { get; init; }
```
Transports that own those schemes; the defaults cover tcp, tls, ws and wss.

```csharp
public IReadOnlySet<Dialect> Dialects { get; init; }
```
The dialects this server will negotiate. Default: all three. Every step of §5.1 is gated on this
set, so a dialect left out here is never answered with (R-2).

```csharp
public IAuthenticator? Authenticator { get; init; }
```
The authenticator; null refuses `Tauth` and admits NOFID attaches.

```csharp
public Limits Limits { get; init; } = Limits.Default;
```
Resource bounds.

```csharp
public ILogger Logger { get; init; } = NullLogger.Instance;
```
Where the server logs; never a global.

```csharp
public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
```
Clock for timeouts, qid versions and server-set times. Default the system clock.

```csharp
public IRequestLogSink? RequestLog { get; init; }
```
Optional per-request log hook; strings reach it escaped and capped.

### `ServerCounters`

`sealed record` — a snapshot of what the server has done, for metrics scraping (architecture §4).
It is a record rather than a set of live counters so that a scrape sees one consistent moment
rather than a mixture of two.

```csharp
public IReadOnlyDictionary<MessageType, long> MessagesByType { get; init; }
```
Messages received per type.

```csharp
public IReadOnlyDictionary<ProtocolErrorKind, long> ProtocolErrorsByKind { get; init; }
```
Errors answered per protocol error kind.

```csharp
public IReadOnlyDictionary<int, long> ErrorsByErrno { get; init; }
```
Errors answered per errno.

```csharp
public long BytesRead { get; init; }
public long BytesWritten { get; init; }
```
Bytes read from and written to transports.

```csharp
public long ConnectionsAccepted { get; init; }
public long ConnectionsOpen { get; init; }
public long ConnectionsRefused { get; init; }
```
Connections accepted since the server started, currently open, and closed because a limit was
reached.

### `RequestLogEntry`

`readonly record struct` — one completed request, for auditing (architecture §4). Every string in
it has already been escaped and capped by the core (§8 rule 11), so a sink may write it out as it
is.

```csharp
public readonly record struct RequestLogEntry(
    Identity? Identity, MessageType Request, string Summary, MessageType Reply,
    NinePError? Error, TimeSpan Duration);
```
`Identity` is who the request ran as, or null before an attach; `Summary` is a short, sanitised
description of what was asked; `Error` is set when the reply was an error.

### `IRequestLogSink`

`interface` — receives one entry per completed request. An implementation must not throw and must
not block: it is called from the connection's own loop, and a slow sink is backpressure on the
session.

```csharp
void Record(in RequestLogEntry entry);
```
Records one completed request.

### The handler model

Handlers are the only code the developer writes. They never see a T-message, a tag, a fid, a
dialect or an error shape, which is what lets one handler answer 9P2000, 9P2000.u and 9P2000.L
alike. A handler signals a failure by throwing `NinePException` carrying the `NinePError` it wants
the peer to see; the core projects it into `Rerror`, `Rerror`+errno or `Rlerror`.

Every legal T-message maps onto exactly one handler method. The table below is
[docs/server.md](server.md), which `ServerDocTests.HandlerTableListsEveryTMessage` checks against
the dispatcher's own list of routed types.

| T-message | Handler method | Owned entirely by the core |
| --- | --- | --- |
| `Tversion` | — | negotiation, session reset |
| `Tauth` | `IAuthenticator.BeginAsync` | afid allocation, triple binding, bounds |
| `Tattach` | `IFilesystem.AttachAsync` | afid verification, identity, fid bind |
| `Tflush` | (cancels the target's token) | flush semantics, CAS suppression |
| `Twalk` | `IDirectoryHandler.LookupAsync` per element | partial walk, clone, `Edupfid`, `..` |
| `Topen` | `IFileHandler.OpenAsync` | mode decode, `DMEXCL`, `ORCLOSE`, permissions, `iounit` |
| `Tlopen` | `IFileHandler.OpenAsync` | flag decode, `DMEXCL`, permissions, `iounit` |
| `Tcreate` | `IDirectoryHandler.CreateAsync` | perm masking, name rules, open state |
| `Tlcreate` | `IDirectoryHandler.CreateAsync` | perm masking, name rules, open state |
| `Tmkdir` | `IDirectoryHandler.CreateAsync` | perm masking, name rules |
| `Tsymlink` | `IDirectoryHandler.CreateAsync` | name rules |
| `Tmknod` | `IDirectoryHandler.CreateAsync` | kind from the POSIX mode, perm masking |
| `Tread` | `IOpenFile.ReadAsync` / `IDirectoryHandler.ReadDirAsync` | count clamp, EOF, stat-record packing, offset rule |
| `Treaddir` | `IDirectoryHandler.ReadDirAsync` | dirent packing, cookie rule |
| `Twrite` | `IOpenFile.WriteAsync` | short-write reporting, append semantics |
| `Tclunk` | `IHandler.ClunkAsync` | fid free, `ORCLOSE` remove, xattr commit |
| `Tremove` | `IDirectoryHandler.RemoveAsync` | fid free even on error, non-empty check |
| `Tunlinkat` | `IDirectoryHandler.RemoveAsync` | no fid is clunked |
| `Tstat` | `IHandler.GetAttrAsync` | projection, masks |
| `Tgetattr` | `IHandler.GetAttrAsync` | projection, masks, 160-byte reply |
| `Twstat` | `IHandler.SetAttrAsync` | `SetAttr` translation, don't-touch, permissions |
| `Tsetattr` | `IHandler.SetAttrAsync` | `SetAttr` translation, server clock, permissions |
| `Trename` | `IDirectoryHandler.RenameAsync` | name rules, cross-directory checks |
| `Trenameat` | `IDirectoryHandler.RenameAsync` | name rules, cross-directory checks |
| `Treadlink` | `ISymlinkHandler.ReadlinkAsync` | kind check |
| `Tlink` | `ILinkCapability.LinkAsync` | capability probe, EOPNOTSUPP |
| `Tlock` | `ILockCapability.LockAsync` | capability probe, EOPNOTSUPP |
| `Tgetlock` | `ILockCapability.GetLockAsync` | capability probe, EOPNOTSUPP |
| `Txattrwalk` | `IXattrHandler.ListXattrAsync` / `GetXattrAsync` | xattr fid state |
| `Txattrcreate` | `IXattrHandler.SetXattrAsync` | xattr fid state, commit on clunk |
| `Tstatfs` | `IStatFsCapability.StatFsAsync` | capability probe, EOPNOTSUPP |
| `Tfsync` | `IHandler.FsyncAsync` | 11/15-byte decode |

Permissions are evaluated by the core against `Attr.Perm` and the fid's implicit identity — the
user of the attach that created the fid, never a field of the message being answered —
**before** the handler is called. A handler may check more; it can never be reached with less.
`ILockCapability`, `IXattrHandler`, `ILinkCapability` and `IStatFsCapability` are separate
interfaces, and a handler that does not implement one is answered `EOPNOTSUPP` ("not supported") by
the core, never a crash and never a silent success.

### `IFilesystem`

`interface` — the tree a server serves (architecture §4): one root directory per authenticated
attach. This is the only interface a developer must implement, and it is handed the identity the
authenticator produced, never the `uname` the client claimed.

```csharp
ValueTask<IDirectoryHandler> AttachAsync(
    Identity identity, string aname, CancellationToken cancellationToken = default);
```
Returns the root directory for this identity and tree name ("" is the default tree), or throws
`NinePException` when this identity may not attach to this tree.

### `IHandler`

`interface` — the operations every file type answers (architecture §4).

```csharp
Qid Qid { get; }
```
The qid of this file; stable for the life of the file (§4.1).

```csharp
ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default);
```
The unified attributes; the core projects them into the dialect's shape.

```csharp
ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default);
```
Applies a partial update; a field this handler cannot change throws `NinePException` with
EOPNOTSUPP.

```csharp
ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default);
```
Finalizes one fid; `wasOpen` says whether that fid had been opened. A handler can be shared by other fids: the core does not reference-count handler objects or dispose a shared handler on their behalf.

```csharp
ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default);
```
Flushes to stable storage; `dataOnly` mirrors `Tfsync.datasync`.

### `IDirectoryHandler`

`interface : IHandler` — a directory (architecture §4). Every name reaching these methods has
already been validated against §8 rule 3 by the core, so a handler never has to defend against
"..", "/" or a NUL byte in a name.

```csharp
ValueTask<IHandler?> LookupAsync(string name, CancellationToken cancellationToken = default);
```
Resolves one path element; returns null when the name does not exist.

```csharp
ValueTask<DirectoryListing> ReadDirAsync(
    ulong cursor, int max, CancellationToken cancellationToken = default);
```
Returns a page of entries starting at a cursor (0 starts the listing); the core packs and bounds
them.

```csharp
ValueTask<IHandler> CreateAsync(
    CreateRequest request, CancellationToken cancellationToken = default);
```
Creates a child of any kind and returns its handler (§5.5).

```csharp
ValueTask RemoveAsync(string name, FileKind kind, CancellationToken cancellationToken = default);
```
Removes a child; `kind` is what the core resolved it to.

```csharp
ValueTask RenameAsync(
    string oldName, IDirectoryHandler newParent, string newName,
    CancellationToken cancellationToken = default);
```
Renames a child into another directory of the same filesystem, which may be this one.

### `DirectoryListing`

`readonly record struct` — a page of directory entries (architecture §4). The handler decides how
many it can produce; the core decides how many fit the reply, and never splits one across replies
(§6.7).

```csharp
public readonly record struct DirectoryListing(
    IReadOnlyList<DirEntry> Entries, ulong NextCursor, bool EndOfDirectory);
```
`Entries` are in listing order and never contain "." or ".."; `NextCursor` continues after the last
entry; `EndOfDirectory` is true when there is nothing after these entries.

### `CreateRequest`

`sealed record` — everything `Tcreate`, `Tlcreate`, `Tmkdir`, `Tsymlink` and `Tmknod` need, unified
into one shape (§7). A handler writes one create, not five.

```csharp
public required string Name { get; init; }
```
The name to create; validated against §8 rule 3 by the core.

```csharp
public required FileKind Kind { get; init; }
```
What to create.

```csharp
public required uint Perm { get; init; }
```
Permission bits after the core applied the parent mask and the 07777 mask.

```csharp
public OpenMode Mode { get; init; }
```
The access mode the new file is opened with; Read for a directory.

```csharp
public OpenFlags Flags { get; init; }
```
Open flags accompanying the create.

```csharp
public string? Target { get; init; }
```
The symlink target when `Kind` is `FileKind.Symlink`.

```csharp
public DeviceId? Rdev { get; init; }
```
The device numbers when the kind is a character or block device.

```csharp
public uint Gid { get; init; } = Constants.NONUNAME;
```
The group id a .L create supplied; NONUNAME when the dialect carries none.

```csharp
public required Identity Identity { get; init; }
```
The identity the create runs as: the implicit user of the fid (§5.2).

### `IFileHandler`

`interface : IHandler` — a regular file (architecture §4).

```csharp
ValueTask<IOpenFile> OpenAsync(
    OpenMode mode, OpenFlags flags, CancellationToken cancellationToken = default);
```
Opens the file; the core has already checked permissions and the open state, and disposes the
returned instance when the fid is clunked.

### `IOpenFile`

`interface : IAsyncDisposable` — one open instance of a file (architecture §4). `ReadAsync` writes
straight into the outgoing frame's own payload buffer, so a read costs no copy between the handler
and the wire.

```csharp
ValueTask<int> ReadAsync(
    ulong offset, Memory<byte> buffer, CancellationToken cancellationToken = default);
```
Reads into the frame's payload buffer, already clamped to the reply budget; returns the byte count,
0 at end of file. The offset is ignored on an append-only file.

```csharp
ValueTask<int> WriteAsync(
    ulong offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
```
Writes a view of the incoming frame, which the core still owns; returns the byte count actually
written, and fewer is a short write rather than an error.

```csharp
ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default);
```
The current length, used for end of file and for `Rgetattr.size`.

### `ISymlinkHandler`

`interface : IHandler` — a symbolic link (architecture §4).

```csharp
ValueTask<string> ReadlinkAsync(CancellationToken cancellationToken = default);
```
The link's target, answered to `Treadlink` and projected into the .u stat extension.

### `ILockCapability`

`interface` — optional: POSIX byte-range locks (.L). A handler that does not implement it answers
`EOPNOTSUPP` from the core, never a crash.

```csharp
ValueTask<LockStatus> LockAsync(LockRequest request, CancellationToken cancellationToken = default);
```
Acquires or releases a lock; the answer says whether it was granted, blocked, refused or in grace.

```csharp
ValueTask<LockQueryResult> GetLockAsync(
    LockRequest request, CancellationToken cancellationToken = default);
```
Reports the lock that conflicts with a range, or `LockType.Unlock` when there is none.

### `IXattrHandler`

`interface` — optional: extended attributes (.L). Absent means `EOPNOTSUPP`.

```csharp
ValueTask<ReadOnlyMemory<byte>> ListXattrAsync(CancellationToken cancellationToken = default);
```
The NUL-separated name list a `Txattrwalk` with an empty name returns.

```csharp
ValueTask<ReadOnlyMemory<byte>> GetXattrAsync(
    string name, CancellationToken cancellationToken = default);
```
Reads one attribute.

```csharp
ValueTask SetXattrAsync(
    string name, ReadOnlyMemory<byte> value, XattrFlags flags,
    CancellationToken cancellationToken = default);
```
Writes one attribute; `flags` says whether it must or must not already exist.

```csharp
ValueTask RemoveXattrAsync(string name, CancellationToken cancellationToken = default);
```
Removes one attribute.

### `ILinkCapability`

`interface` — optional: hard links (.L `Tlink`), implemented on the directory that receives the
link.

```csharp
ValueTask LinkAsync(string name, IHandler target, CancellationToken cancellationToken = default);
```
Creates a hard link to an existing file in this directory.

### `IStatFsCapability`

`interface` — optional: filesystem statistics (.L `Tstatfs`), implemented on the filesystem or on a
handler.

```csharp
ValueTask<StatFs> StatFsAsync(CancellationToken cancellationToken = default);
```
The statistics of the filesystem behind this object.


## Reviewed behavioral contracts

The surface signatures remain unchanged by the review fixes. `Limits.MaxInFlightPerConnection`
includes the flush reserve; its ordinary portion and `MaxInFlightPerListener` limit admitted
ordinary work. Exhaustion returns EAGAIN, while Tflush retains progress. `ClientOptions.InFlightWindow` must be
positive and is validated before dialing/negotiating. `NinePFid` rejects operations after release;
closing waits for started wire operations, including a full chunked transfer. Directory enumeration
leases individual fetches: buffered entries can still be yielded, but its next fetch rejects a
released fid. Explicit xattr commit errors/timeouts reach the caller, while cleanup preserves an
earlier failure. Append transfers use one outstanding write and retry only unacknowledged bytes.

`IHandler.ClunkAsync` is called per fid; it does not imply no other fid uses the handler. Server
finalization drains operations and preserves ORCLOSE, xattr and exclusive-use effects on every
close path. Excess ordinary requests receive EAGAIN while Tflush remains processable. TLS/WS/WSS
handshakes use the existing TCP cap for pending, queued and active connections, and custom TLS roots
retain certificate application-purpose validation. See the maintained client/server/transport
guides for details and the generated API pages for current XML documentation.
