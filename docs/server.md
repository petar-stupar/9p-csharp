# The handler table

Every legal T-message maps onto **exactly one** handler method. Everything in the third column
belongs to the server core: a handler never sees a T-message, a tag, a fid, a dialect or an error
shape, which is what lets one handler answer 9P2000, 9P2000.u and 9P2000.L alike.

`ServerDocTests.HandlerTableListsEveryTMessage` reads this table and the dispatcher's own list of
routed types and asserts they agree, so this document cannot fall behind the code.

| T-message | Handler method | Owned entirely by the core |
| --- | --- | --- |
| `Tversion` | — | negotiation, session reset |
| `Tauth` | `IAuthenticator.BeginAsync` | afid allocation, triple binding, bounds |
| `Tattach` | `IFilesystem.AttachAsync` | afid verification, identity, fid bind |
| `Tflush` | (cancels the target's token) | flush semantics, CAS suppression |
| `Twalk` | `IDirectoryHandler.LookupAsync` per element | partial walk, clone, `Edupfid`, `..` |
| `Topen` | `IFileHandler.OpenAsync` | mode decode, `DMEXCL`, `ORCLOSE`, permissions, `iounit`, what cannot be opened |
| `Tlopen` | `IFileHandler.OpenAsync` | flag decode, `O_DIRECTORY` and `O_NOFOLLOW`, `DMEXCL`, permissions, `iounit` |
| `Tcreate` | `IDirectoryHandler.CreateAsync` | perm masking, refused `DM*` flags, device extension, name rules, open state |
| `Tlcreate` | `IDirectoryHandler.CreateAsync` | perm masking, name rules, open state |
| `Tmkdir` | `IDirectoryHandler.CreateAsync` | perm masking, name rules |
| `Tsymlink` | `IDirectoryHandler.CreateAsync` | name rules |
| `Tmknod` | `IDirectoryHandler.CreateAsync` | kind from the POSIX mode, perm masking |
| `Tread` | `IOpenFile.ReadAsync` / `IDirectoryHandler.ReadDirAsync` | count clamp, EOF, stat-record packing, offset rule |
| `Treaddir` | `IDirectoryHandler.ReadDirAsync` | dirent packing, cookie rule |
| `Twrite` | `IOpenFile.WriteAsync` | short-write reporting, append semantics |
| `Tclunk` | `IHandler.ClunkAsync` | fid free even on error, `ORCLOSE` remove, xattr commit |
| `Tremove` | `IDirectoryHandler.RemoveAsync` | fid free even on error, non-empty check |
| `Tunlinkat` | `IDirectoryHandler.RemoveAsync` | `AT_REMOVEDIR`, no fid is clunked |
| `Tstat` | `IHandler.GetAttrAsync` | projection, masks |
| `Tgetattr` | `IHandler.GetAttrAsync` | projection, `valid` from what was supplied, 160-byte reply |
| `Twstat` | `IHandler.SetAttrAsync` | `SetAttr` translation, don't-touch, permissions |
| `Tsetattr` | `IHandler.SetAttrAsync` | `SetAttr` translation, server clock, permissions |
| `Trename` | `IDirectoryHandler.RenameAsync` | name rules, cross-directory checks |
| `Trenameat` | `IDirectoryHandler.RenameAsync` | name rules, cross-directory checks |
| `Treadlink` | `ISymlinkHandler.ReadlinkAsync` | kind check |
| `Tlink` | `ILinkCapability.LinkAsync` | capability probe, EOPNOTSUPP |
| `Tlock` | `ILockCapability.LockAsync` | capability probe, EOPNOTSUPP |
| `Tgetlock` | `ILockCapability.GetLockAsync` | capability probe, EOPNOTSUPP |
| `Txattrwalk` | `IXattrHandler.ListXattrAsync` / `GetXattrAsync` | xattr fid state |
| `Txattrcreate` | `IXattrHandler.SetXattrAsync` / `RemoveXattrAsync` | xattr fid state, commit on clunk, `attr_size` 0 removes |
| `Tstatfs` | `IStatFsCapability.StatFsAsync` | capability probe, EOPNOTSUPP |
| `Tfsync` | `IHandler.FsyncAsync` | 11/15-byte decode |

The `data` an `IOpenFile.WriteAsync` receives is `Twrite.Data`: it aliases the decoded frame, whose
buffer is pooled, and is valid only until that frame is released, which is the end of the handler
call. A handler that keeps the bytes past that copies them (workspace architecture §12 rule 5).

## What a `Twstat` may change

`Twstat` is the 9P2000 / 9P2000.u way to change attributes, and reference §5.8 fixes what it can
carry: `name`, `mode`, `mtime`, `gid`, `length`, and `n_gid` in .u. Anything else is **refused with
`EPERM`**, not dropped and answered `Rwstat` — a success reply for work that was never done is the
one answer a server must never give.

| Field the record sets | Answer |
| --- | --- |
| `uid`, `n_uid` | `EPERM` — the owner may never change through a `Twstat` |
| `muid`, `n_muid` | `EPERM` — the last modifier is the server's to set |
| `atime` | `EPERM` — `mtime` is settable, `atime` is not |
| `type`, `dev` | `EPERM` — kernel fields |
| `qid` | `EPERM` — identity, not an attribute |
| the `DMDIR` bit of `mode`, flipped | `EPERM` — judged against what the file actually is |
| the `DMAUTH` or `DMMOUNT` bit of `mode`, changed | `EPERM` — the server's own bits (reference §8 rule 19) |
| every field "don't touch" | `Rwstat`, after `IHandler.FsyncAsync` (§4.2) |

A field counts as set only when it **differs from what the file already has**. The core stats the
file first and compares, so a client that fills the record from the `Rstat` it just read — Linux
v9fs does this for a plain `chmod` or `truncate` on a .u mount — copies back the file's own `uid`,
`muid`, `type` and `dev` and is answered `Rwstat`: it asked for no change in those fields. Only a
value that would actually change something is refused.

The refusal is atomic with the rest: a record that names one permitted field and one forbidden one
changes neither.

The `DMAPPEND`, `DMEXCL` and `DMTMP` bits are settable — stat(5) makes the directory bit the one
mode bit a wstat cannot change — and reach `IHandler.SetAttrAsync` as `SetAttr.Flags`, judged like
every other field: only a set that differs from the file's own is handed on, and an echoed set is
"do not touch". The flags are part of the mode, so the owner check applies. After the handler
answered, the core stats the file again and refuses with `EOPNOTSUPP` when the flags are not there,
so a handler written before `SetAttr.Flags` existed cannot answer success for a change it ignored
(rule 19).

## What an open, a create and a removal may ask for

The rule these share is reference §8's: **a request naming something the dialect, the wire or the
handler model cannot carry is refused, never answered with the untranslatable part dropped.** A
success reply is a statement that the work was done.

| Request | Answer |
| --- | --- |
| `Topen` / `Tlopen` of a symbolic link | `ELOOP` — 9P resolves nothing for the client, so there is no file to open (rule 23) |
| `Topen` / `Tlopen` of a fifo, socket or device with no `IFileHandler` behind it | `ENXIO` — an open reply with no open file behind it is never sent (rule 23) |
| `O_DIRECTORY` on anything but a directory | `ENOTDIR` (rule 23) |
| `O_NOFOLLOW` on a symbolic link | `ELOOP`, the same answer the open draws anyway (rule 23) |
| `OTRUNC` or `ORCLOSE` on a directory, at open **or** at create | `EISDIR` — a create is judged exactly as the open it performs (rule 25) |
| `Tcreate.perm` carrying `DMAUTH` or `DMMOUNT` | `EPERM` — the server's own bits (rule 19); `DMAPPEND`, `DMEXCL` and `DMTMP` reach the handler as `CreateRequest.FileFlags`, and a created file that lacks them is removed again and the create refused with `EOPNOTSUPP` |
| a .u `Tcreate` with `DMDEVICE` whose `extension` is not `"b maj min"` / `"c maj min"` | `EINVAL`; a well-formed one reaches the handler as `CreateRequest.Rdev` (rule 24) |
| `Tunlinkat` of a directory without `AT_REMOVEDIR` | `EISDIR`; of anything else **with** it, `ENOTDIR`; any other flag bit, `EINVAL` (rule 20) |

A .u `Tcreate` of a symlink, fifo or socket is the one create that succeeds without opening
anything: it is how v9fs spells `symlink(2)` and `mknod(2)` on a .u mount, and reference §5.5
requires it to work. The object is created and the fid names it, but the fid is **not** left
claiming to be open — a `Tread` of it is `"bad open mode"` rather than a permanent zero-byte
answer, which is the shape rule 23 forbids.

## A clunk that fails

`Tclunk` and `Tremove` free the fid whatever happens (clunk(5)), and a handler's `ClunkAsync` error
is the **reply**: `Rerror` / `Rlerror` carrying it, never an `Rclunk` or `Rremove` that hides it
(reference §8 rule 26). A handler whose flush of the last write failed has one moment to say so and
this is it. The fid is gone either way, and so is the file a successful `Tremove` removed.

The one place with no reply to carry such an error is the mass clunk of a mid-session `Tversion` or
a closing connection (§5.1): there the refusal is dropped, because the alternative is abandoning
the fids after it in the table.

## What an `Rgetattr` marks valid

The reply is always the full 160 bytes and the qid is always valid, whatever `valid` says
(reference §4.6). The mask is the intersection of what the client asked for with what the handler
**supplied** — and for `btime`, `gen` and `data_version` the core cannot tell a supplied zero from
an absent value, because `Attr` has no "unknown": those three bits are marked only when the value
is non-zero (reference §8 rule 22). Everything else comes from fields every handler must answer
with, so it stays valid. A client is then free to trust the mask, which is the point of it.

## Stat records that cannot describe themselves

A stat record's `size[2]`, and the `n[2]` that wraps it inside `Rstat`, are sixteen bits wide. A
record whose `name`, `uid`, `gid`, `muid` or .u `extension` push it past 65 535 bytes (65 533 for
the wrapped form) is refused at encode time with `NinePException(EOVERFLOW)` rather than written
with a truncated length, which would produce a frame whose outer `size[4]` disagreed with its inner
lengths.

Those strings come from the handler, but a client can still provoke the refusal: `jsonfs` reports
`uid`, `gid` and `muid` as the attaching user, and nothing bounds `Tattach.uname` below the
negotiated msize, so a 30 000-byte `uname` leaves every `Rstat` on that session unencodable. The
core therefore **encodes a reply before it claims the request's tag**: the overflow is an ordinary
`NinePException` on the handler's own path, which the dispatcher answers `Rerror "value too large"`
/ `Rlerror EOVERFLOW` like any other. The session survives it, the tag is freed by the reply that
actually goes out, and the request-log hook records the error rather than the `Rstat` that was
never sent. Encoding after the claim instead dropped the request in silence.

## Permissions

`PermissionChecker` evaluates `Attr.Perm` against the fid's **implicit identity** — the user of the
attach that created the fid, never a field of the message being answered — **before** the
handler is called. A handler may check more; it can never be reached with less.

| Request | Bits required |
| --- | --- |
| `Twalk` | execute (search) on every directory traversed |
| `Topen` / `Tlopen` | read, write or both by access mode; write as well for `OTRUNC`; write in the parent for `ORCLOSE` |
| create of any kind | write on the directory |
| `Tremove` / `Tunlinkat` | write in the parent |
| `Twstat` / `Tsetattr` | owner for mode, group and times; write on the file for a length change; write in the parent for a rename |
| `Tstat` / `Tgetattr` | none |

## Optional capabilities

`ILockCapability`, `IXattrHandler`, `ILinkCapability` and `IStatFsCapability` are separate
interfaces. A handler that does not implement one is answered `EOPNOTSUPP` (`"Operation not supported"`) by
the core — never a crash, and never a silent success.

`IXattrHandler` has one call the wire does not name: a `Txattrcreate` whose `attr_size` is **zero**
is `removexattr(2)` — that is how v9fs and diod spell it — so the core calls `RemoveXattrAsync` when
such a fid is clunked, and `SetXattrAsync` never receives an empty value (reference §8 rule 21).
Everything else about the sink is unchanged: the value is written with `Twrite` and committed on
clunk, and a byte count that disagrees with `attr_size` is `EINVAL` there.

## Fid lifetime, append and overload

All operations lease their fid entries; multi-fid requests acquire them in numeric order and
revalidate that the same entries remain active. Clunk/remove retires an entry before waiting for
active operations to finish. `IHandler.ClunkAsync` is a **per-fid** callback, not a notification that
no other fid references the handler. Handlers shared by multiple fids must remain usable by those
other fids. Reset, disconnect and shutdown perform the same ORCLOSE, xattr and exclusive-open
finalization, logging failures when no request can receive them. A handler ignoring cancellation
keeps its resources until it returns even after the connection has closed.

Append-only attributes force append even without an append flag and suppress truncation after the
requested permissions are checked. File-length selection, writing and size-changing operations are
serialized by file identity across fids and connections. Complete fid ancestry survives cloned and
separate walks. Every nonempty step (including `..`) checks directory type and search permission;
a later filesystem error produces the successful qid prefix without rebinding either fid.

Plain 9P2000 follows Plan 9 permission alternatives; the Unix dialects select one permission class.
Both named xattr reads and name lists require read access before the callback. Handlers remain
dialect-neutral and may enforce additional restrictions.

Ordinary requests that cannot acquire both in-flight budgets receive `EAGAIN` immediately, while
`Tflush` continues to be processed. This replaces the earlier promise of blocking without refusal.
A budget is returned when the reply is queued, under the same gate that frees the tag and before
the bytes can reach the wire, so a client that sends its next request the instant it has a reply is
never refused for a window it has already been given back
(`BackpressureTests.AWindowReusedTheInstantItsReplyArrivesIsNeverRefused`). A flushed request
returns its budget when its cancelled handler unwinds: `Rflush` frees the tag, not the worker.
The client must consume replies for transport progress; worker limits do not promise progress for a
peer that never reads. Listener session bookkeeping holds active tasks only.

After a connection has closed, cleanup failures are observed and logged. If the logging sink itself
throws during that cleanup, remaining fids and synchronization resources are still released.
This cleanup exception does not change the documented live-connection policy for throwing loggers.

Renames performed through this server update the ancestry of existing aliases and descendants, including moves through other
fids or connections. Subtree attaches retain their own root boundary. Mutations coordinate affected paths; walks retry
when a rename races lookup, without holding a global gate across handler calls. The registry
releases retired fids and completed traversal state.
Out-of-band handler namespace changes must preserve the relationships of live fids; the core has
no handler callback that announces external moves.


Requests using a single existing fid enter its operation queue in wire arrival order. Dispatch
moves to the thread pool after acquiring the fid lease, so synchronous handlers cannot block
the connection reader. This preserves chunk order for pipelined writes, including split UTF-8
writes in jsonfs. Different fids can still execute concurrently.

## Setattr boundary policy

After fid validation, a literal `.L` zero valid mask returns success without invoking SetAttr or fsync. ATIME_SET/MTIME_SET require their corresponding base bits; malformed masks fail EINVAL before any field changes. This is the explicit workspace §4.6 policy. Directory size changes are checked after write permission: legacy wstat permits zero to reach the handler and rejects nonzero; `.L` rejects either. All-don't-touch wstat and explicit Tfsync keep their existing semantics.
