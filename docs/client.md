# The client

Three layers, and you can drop between them at any point:

| Layer | Type | What it speaks |
| --- | --- | --- |
| paths | `NinePSession` | `"/a/b/c"`, `byte[]`, `DirEntry`, `Attr` |
| handles | `NinePFid` | one open file, at an offset |
| messages | `INinePMessages` | one method per T-message, returning the typed R-message |

Nothing above the message layer knows which dialect the session negotiated. A `session.GetAttrAsync`
is a `Tstat` in 9P2000 and a `Tgetattr` in 9P2000.L, and both come back as an `Attr`.

## Connecting

```csharp
await using NinePSession session = await NinePClient.ConnectAsync(
    NinePAddress.Parse("tcp://fileserver:5640"),
    new ClientOptions
    {
        Dialects = [Dialect.P9_2000_L, Dialect.P9_2000_u, Dialect.P9_2000],
        MinDialect = Dialect.P9_2000_u,
        Uname = Environment.UserName,
        Credential = new TokenCredential(secret),
    });
```

`ConnectAsync` has three overloads: by address (the shipped transport for the scheme dials it), by
`ITransport` plus address (your own transport, or a `MemoryTransport` instance), and by
`INinePConnection` (you dialled it yourself; the session owns it from then on).

`Dialects` is a preference list, offered most-preferred first. Negotiation walks it: the client asks
for the first, and an answer of `"unknown"` refuses the version that was offered and not the
connection (§5.1), so the client offers the next dialect on the list; the exception comes only when
the list runs out.

**What counts as an answer** (§8 rule 18). The `Rversion` is judged against the offer that drew it,
not against `MinDialect` alone. Exactly two strings are answers to an offer:

- the dialect that was just offered, which is the ordinary case; or
- plain `"9P2000"`, when the offer carried a `.` suffix — the suffix-stripping fallback of
  version(5):70–78. **This is the only downgrade there is**, and `MinDialect` is what gates it: a
  floor of `Dialect.P9_2000` takes it, a floor of `.u` or `.L` makes it a `NinePVersionException`.

Everything else is a `NinePVersionException` whatever `MinDialect` says — `"9P2000.u"` answering a
`.L` offer (neither the offer nor the base version), a dialect **higher** than was offered
(`"9P2000.L"` to a `9P2000` offer would run the session in a dialect this side never proposed), and
any string that is not a dialect at all. A floor of `9P2000` is permission to be downgraded to the
base version, not permission to accept anything.

An `Rversion` that arrives with **no** `Tversion` outstanding is not a late answer to anything: it
is an unexpected reply under §8 rule 12 and terminates the session, like every other reply the
client cannot place.

`Msize` defaults to 1 MiB when `.L` is offered first and 128 KiB otherwise, and is clamped into
`Limits`. `session.Msize` and `session.MaxPayload` (`msize − IOHDRSZ`) are what was actually agreed.

The default is chosen from the dialect **offered first**, which is the only one that exists when
`Tversion` is written, and not from the dialect that ends up negotiated — nothing has been
negotiated yet. A client that offers `.L` and is answered `9P2000` therefore runs 9P2000 with a
1 MiB msize, while a client that offered `9P2000` in the first place runs the same dialect with
128 KiB. Both are legal: msize is not dialect-scoped, and the server clamps whatever it is asked
for. Set `Msize` explicitly to make the number independent of what is offered.

## Attaching, and fids

`AttachAsync` runs the afid exchange when `ClientOptions.Credential` is set and attaches with
`NOFID` when it is not. It returns the root fid.

```csharp
await using NinePFid root = await session.AttachAsync();
await using NinePFid lib = await root.WalkAsync(["usr", "glenda", "lib"]);
```

A `NinePFid` is a handle on one file and **disposing it clunks the fid**. That is why every example
in this repository uses `await using`: a fid released only when a finaliser eventually runs is a fid
the server still holds a handler and possibly an open file for. `session.DisposeAsync` clunks
whatever is left and closes the transport, so a leak is bounded by the session, not unbounded.
Those clunks go out **together** and share one deadline — `ClientOptions.DisposeTimeout` (5 s), or
`RequestTimeout` when that is shorter — so a peer that has stopped answering costs one deadline in
total rather than one per fid; the transport close makes the server forget the rest.

Every operation acquires a lifetime guard before using the fid number. Starting `DisposeAsync`
or `RemoveAsync` rejects new operations with `ObjectDisposedException` and waits for already
started operations, including complete chunked transfers and walks, before closing and recycling
the number. Independent operations remain pipelined. Directory enumeration guards each network
fetch: a pending fetch finishes before close; buffered entries may still be yielded, but resuming
a listing that needs another fetch after close throws. Repeated disposal is safe and waits for the
same close. Repeated removal throws. The first attached `session.Root` retains its identity even
after it is released; attaching another tree never makes operations through the old root valid.
The low-level `Messages` API accepts caller-managed numeric fids and therefore requires the caller
to manage those lifetimes.

Disposing a `NinePFid` never throws for a clunk the peer refused, never answered, or that a session
already going away could not send: it is documented as safe to call from a `finally`, and a
disposal that threw there would replace the failure the caller was actually being told about.

`session.DisposeAsync` carries the same promise, and for the same reason. A connection that has
already died — a peer that crashed, a socket closed under the writer — is the ordinary way a
session ends, not an exceptional one, so the courtesy clunks it can no longer deliver are
swallowed whether the transport reports them as a protocol failure or raises its own
`IOException`. `await using` on a session whose server has gone away is exactly when a caller can
least afford a new exception, and until 0.2.0 it got one depending on whether the write or the
reader noticed the death first.

**A `Tclunk` that was flushed.** Disposal itself is never cancelled — the clunk goes out under no
token — so the only thing that flushes it is `RequestTimeout` elapsing, at which point the session
sends `Tflush` as it would for any request. The fid **number** is not returned to the session's fid
pool while that is in flight: it goes back only when the clunk unwinds, which is when the server
has answered — `Rclunk` (one that raced ahead of the `Tflush` still counts), an `Rerror`/`Rlerror`
to the clunk (clunk(5): the fid is gone whatever the reply says), or the `Rflush` that says the
clunk never ran. In that last case the server still holds the fid, and holds it until the
connection closes; a conforming server refuses the number's next use with `duplicate fid` /
`EINVAL` (walk(5): `newfid` must not be in use), which the caller sees as a `NinePException` on that
walk rather than as two handles quietly sharing a number. The one case with no confirmation at all
is a `Tflush` that the peer does not answer within a second `RequestTimeout`: both tags stay
quarantined as described under *Pipelining, cancellation and flush* below, but the clunk has
unwound and the fid number **does** return to the pool, `2 × RequestTimeout` after the `Tclunk`
went out — a peer that has answered nothing in that time is treated as one the session is about to
lose. `session.DisposeAsync` and the cleanup clunk of a failed walk bound their *wait* for such a
clunk (`DisposeTimeout`) but do not cancel it: the number returns when it unwinds as above, and the
transport close makes the server forget every fid on the connection.

`WalkAsync` walks element-wise and throws on a **partial** walk — the fid it would have bound is not
bound, which is walk(5)'s rule. A path longer than `MAXWELEM` (16) elements becomes several
`Twalk`s, each walking the new fid onto itself. `".."` is legal; `"."` is not.

`CloneAsync` duplicates a fid without walking, which a directory read needs and which the server
refuses on an already-open fid.

## Reading and writing

```csharp
await file.OpenAsync(OpenMode.Read);
int read = await file.ReadAsync(offset: 0, buffer);          // one Tread, capped at Iounit
byte[] all = await file.ReadAllAsync();                      // chunked, windowed
await file.WriteAllAsync(bytes);
```

`ReadAsync` and `WriteAsync` are one message each and are capped at `NinePFid.Iounit` — the value
the server reported at open, or `msize − IOHDRSZ` when it reported 0. `ReadAllAsync` and
`WriteAllAsync` chunk at that size and keep `ClientOptions.InFlightWindow` requests outstanding
(default 4, minimum 1). Every connection overload rejects a smaller value with
`ArgumentOutOfRangeException` before dialing or sending negotiation frames. Transfers observe
cancellation even before their first request, including an empty write. That window is where throughput comes from: one request per round trip would cost a
round trip per chunk, and a 9P chunk is small. [docs/benchmarks.md](benchmarks.md) has the numbers.

Append transfers use one outstanding write when `OpenFlags.Append` was requested or the open
reply's qid reports `QTAPPEND`. A short acknowledgement retries the unacknowledged suffix before
sending later data, preserving byte order and preventing duplicate appends. Ordinary offset-based
transfers retain the configured window and repair a short-write gap by rewriting later chunks.
Every response in a batch is validated, including responses after an earlier short result.

`SetXattrAsync` writes through a temporary sink fid and commits with an explicit `Tclunk`. It
returns only after that commit succeeds. A commit refusal, timeout or cancellation is propagated;
cleanup after an earlier error is best effort and preserves that original error. An empty value
requests attribute removal and has the same commit guarantees. The source fid remains usable.

The `NinePFid` read methods copy into your buffer. On the low-level `INinePMessages.ReadAsync` the
`Rread.Data` you get back aliases the decoded frame and is valid only until that frame is
released; a caller who keeps it copies (workspace architecture §12 rule 5).

`ReadDirAsync` is an `IAsyncEnumerable<DirEntry>` and reads both record formats — 9P2000's stat
records and `.L`'s dirents — into the same `DirEntry`. It resumes from the cursor the server
returned, so a directory larger than one message is a sequence of reads and not a failure.

## The path API

`OpenFileAsync`, `ReadFileAsync`, `WriteFileAsync`, `ReadDirAsync`, `MkdirAsync`,
`CreateFileAsync`, `RemoveAsync`, `RenameAsync`, `GetAttrAsync`, `SetAttrAsync`, `SymlinkAsync`,
`ReadlinkAsync` and `StatFsAsync` take a path and do the walk, the operation and the clunk. They are
the 60-second API; when you need the offset, the mode or the fid to live longer than one call, walk
a `NinePFid` and use that.

`RenameAsync` sends `Trenameat` in `.L`, falling back once to `Trename` when the server answers
`EOPNOTSUPP` (diod implements only the latter; Linux v9fs falls back the same way), and a `Twstat` carrying a name in 9P2000 and 9P2000.u, which
is the only rename those dialects have — and it renames **within one directory**, so a move across
directories in those dialects is refused rather than performed as a rename that ignored the
destination. `SymlinkAsync` and `ReadlinkAsync` throw on 9P2000, which has no symlinks;
`StatFsAsync`, `LockAsync`, `GetLockAsync`, `GetXattrAsync` and `SetXattrAsync` are `.L` only for
the same reason.

## What the client refuses to send

Reference §8 rules 15 and 16. The principle: **a request that names something the negotiated
dialect cannot carry is refused before anything goes on the wire**, never sent with the
untranslatable part quietly missing. The refusal is a `NinePException` raised by the call itself —
no frame is written, and no fid or tag is spent.

| Asked for | Where | Refused with |
| --- | --- | --- |
| `OpenFlags.RemoveOnClose` on `.L` | `NinePFid.OpenAsync`, `CreateAsync` | `EOPNOTSUPP` — Linux `open(2)` has no `ORCLOSE` |
| `OpenFlags.Exclusive`, `Directory`, `NoFollow` on 9P2000 / `.u` | `NinePFid.OpenAsync`, `CreateAsync` | `EOPNOTSUPP` — `Topen.mode` is one byte and has no bit for any of them |
| `SetAttr.Name` or `SetAttr.GroupName` on `.L` | `NinePFid.SetAttrAsync` | `EINVAL` — `Tsetattr` has neither field; `.L` names groups by number and renames with `Trename` |
| `SetAttr.Gid` on plain 9P2000 | `NinePFid.SetAttrAsync` | `EINVAL` — `n_gid` is a `.u` field |
| `SetAttr.Uid` on 9P2000 / `.u` | `NinePFid.SetAttrAsync` | `EPERM` — stat(5): the owner may never change through a `wstat` |
| `SetAttr.ATime` on 9P2000 / `.u` | `NinePFid.SetAttrAsync` | `EPERM` — stat(5) lists `atime` among the fields a `wstat` may not set |
| `SetAttr.ATimeToNow` / `MTimeToNow` / `CTimeToNow` on 9P2000 / `.u` | `NinePFid.SetAttrAsync` | `EINVAL` — a `Twstat` carries a time *value*; there is no "use the server's clock" |
| `SetAttr.Flags` on `.L` | `NinePFid.SetAttrAsync` | `EINVAL` — `Tsetattr.mode` is a POSIX word with no bit for `DMAPPEND`, `DMEXCL` or `DMTMP` |
| `FileKind.Directory` on `.L` | `NinePFid.CreateAsync` | `EOPNOTSUPP` — `Tlcreate` makes a plain file; a directory is `Tmkdir`, so `MkdirAsync` |
| `fileFlags` other than `FileFlags.None` on `.L` | `NinePFid.CreateAsync` | `EOPNOTSUPP` — `Tlcreate.mode` has only the `07777` bits, and no bit for `DMAPPEND`, `DMEXCL` or `DMTMP` |
| a kind other than `File` or `Directory` | `NinePFid.CreateAsync` | `EOPNOTSUPP` — a symlink's target and a device's numbers travel in the `.u` extension field, which this create does not send; use `SymlinkAsync` or `Tmknod` (rule 15) |
| `FileFlags.Auth` or `FileFlags.Mount` in `fileFlags` | `NinePFid.CreateAsync` | `EPERM` — the server's own bits; stat(5) lets a client set only the other three (rule 19) |
| a rename across directories on 9P2000 / `.u` | `NinePSession.RenameAsync` | `"cannot rename across directories"` |
| symlink creation, statfs, locks, xattrs outside `.L` | `NinePSession`, `NinePFid` | `EOPNOTSUPP` |

One update goes out as two messages: a `SetAttr` that states `Perm` without `Flags`, or `Flags`
without `Perm`, on 9P2000 or `.u`. A `Twstat` mode word carries both halves, so `SetAttrAsync`
reads the file's record with a `Tstat` and completes the unstated half from it — a chmod keeps the
file append-only, and setting a flag keeps the permission bits — which is what Plan 9's `chmod` and
Linux v9fs do (reference §8 rule 19). State both halves to skip the `Tstat`.

The time cases are the sharpest of these, and they are why the rule is worth its cost. A `Twstat`
whose every field is "don't touch" is not an empty update: stat(5) defines it as **fsync** (§4.2).
An update carrying only `ATimeToNow` therefore used to project to that record and go out as a
request to flush the file, and the caller was told its change had been applied.

Two projections are **not** drops, and are not refused:

- `OpenMode.Exec` on `.L` goes out as `O_RDONLY` (§8 rule 16). Execute permission is the Linux
  client's own concern, and access mode 3 is `O_NOACCESS`, which never leaves a client.
- `FsyncAsync(dataOnly: true)` on 9P2000 and `.u` goes out as the all-don't-touch `Twstat`, which is
  the full sync. Only `.L`'s `Tfsync` carries `datasync`; a full sync commits the data as well, so
  the weaker request is satisfied by the stronger one and nothing the caller asked for is left
  undone.

## What a 9P2000 session can tell you about a file

`Attr.Kind` and `Attr.Qid.Type` always agree (§8 rule 17), and what they can say is bounded by the
dialect (reference §7):

- **9P2000** has a mode bit for directories and nothing else. A fifo, a socket and a device are
  therefore reported as **plain files** — the reference's own words are "not representable (plain
  file)" — and there is no field that could say otherwise. A server that marks the qid `QTSYMLINK`
  does get a `FileKind.Symlink` back, because the qid byte is evidence and disagreeing with it would
  break the invariant; `SymlinkTarget` stays null, since plain 9P2000 has no extension field to
  carry one. In the other direction, a server in this repository refuses to `stat` a symlink into a
  9P2000 record at all rather than describe it as a plain file.
- **9P2000.u** has `DMSYMLINK`, `DMNAMEDPIPE`, `DMSOCKET` and `DMDEVICE`, and the `extension` field
  carries a symlink's target or a device's `"b maj min"` / `"c maj min"`. Every kind is
  representable.
- **9P2000.L** carries the POSIX `st_mode`, so every kind is representable and `Rgetattr.rdev`
  gives the device numbers directly.

In `.L`, an `Attr` reports only what `Rgetattr.valid` marked (§8 rule 17). A field the server did
not mark keeps its `Attr` default — `NLink` 1, `Uid`/`Gid` `NONUNAME`, zero times and sizes — rather
than the zero the 160-byte reply pads with, and with `MODE` unmarked the kind comes from the qid
type byte, which §4.6 says is always valid. "The server did not say" and "the server said zero" are
different answers, and the client keeps them apart.

## Pipelining, cancellation and flush

Every request goes through a tag multiplexer with a bounded tag pool, so requests may be issued
concurrently on one session and replies are routed by tag as they arrive, in whatever order.

Cancelling a request's `CancellationToken` sends `Tflush` and follows flush(5) exactly:

- the tag is **not** reused until the `Rflush` arrives, even if the original reply arrives first;
- a reply that races the flush is delivered to the caller rather than dropped — a `Tflush` is a
  request to stop, not a guarantee that nothing happened;
- if the request was flushed and no reply ever came, the caller sees `OperationCanceledException`
  when its own token fired, and `TimeoutException` when `ClientOptions.RequestTimeout` elapsed.
  Collapsing those two would make a server that is merely slow indistinguishable from a caller that
  changed its mind.

The wait for that `Rflush` carries `RequestTimeout`. If it too goes unanswered, **both** tags stay
out of circulation for the life of the session — the flushed request's and the `Tflush`'s own —
because the server confirmed neither. A late `Rflush` arriving afterwards is therefore recognised
and dropped rather than meeting the unknown-tag rule below, which would end the session over a peer
that was only slow.

A server that answers the `Tflush` with `Rerror`/`Rlerror` instead of `Rflush` — flush(5) allows no
such reply, but it is seen in the wild — is taken at its word all the same: it has answered the
`Tflush`, so both tags go back to the pool exactly as on an `Rflush`, promptly or late, the caller
sees what an `Rflush` would have given it, and the refusal is logged at `Warning`.

## What the client refuses to believe

Reference §8 rules 12 and 13 are the client's half of the validation, and they are enforced, not
assumed. A reply with an unknown tag, a type that is neither `T + 1` nor the dialect's error type,
or a size above the negotiated msize **terminates the session**: the stream is no longer
trustworthy. An `Rread` or `Rreaddir` carrying more bytes than were asked for, an `Rwrite` claiming
more bytes than were sent, an `Rwalk` with more qids than the `Twalk` had names, a stat record whose
inner size disagrees with its outer one, and a dirent split across a reply boundary are each a
`NinePProtocolException` and not a silently-clamped success.

## What escapes a client call

`NinePException` and its two derived types (`NinePProtocolException`, `NinePVersionException`), plus
the `ArgumentException` family, `ObjectDisposedException`, `OperationCanceledException` and
`TimeoutException`. Nothing else: a `SocketException` from a closed socket, for instance, is turned
into an orderly end of stream by the transport rather than surfacing to a caller who cannot act on
it.

## Options

`ClientOptions` is a `sealed record` with `init`-only members; every default is documented on the
member and repeated in [docs/api.md](api.md). The ones worth setting deliberately:

| Member | Default | Why you would change it |
| --- | --- | --- |
| `Dialects` / `MinDialect` | `.L, .u, 9P2000` / `9P2000` | to insist on `.L` semantics, or to speak to a legacy server |
| `Msize` | 1 MiB for `.L`, 128 KiB otherwise | smaller for a memory-constrained peer, larger never — the server clamps |
| `MaxReadAll` | 256 MiB | lower the cap on whole-file and whole-xattr materialization |
| `InFlightWindow` | 4 | at least 1; more for a high-latency link; append transfers use 1 |
| `RequestTimeout` | 60 s | a slow filesystem behind the server, or a much tighter service objective |
| `Credential` | none | anything but an already-authenticated transport — see [docs/auth.md](auth.md) |
| `Limits` | `Limits.Default` | to bound what this side will accept from a server it does not trust |
| `Logger` | `null` (uses `NullLogger.Instance`) | anything you want to see |


## Whole-file limits and directory memory

`ClientOptions.MaxReadAll` defaults to 256 MiB. `NinePFid.ReadAllAsync`, `ReadFileAsync` and
whole-xattr reads enforce it before reading when metadata already exceeds the limit, and again
against actual bytes as data arrives. Missing/stale/understated size cannot bypass it. Exceeding
the limit raises EFBIG, never a silently truncated result. Zero permits only empty values;
negative values and values above the runtime array limit are rejected before connecting.
The cap bounds returned bytes; accumulation and the final array can temporarily use more memory.
Use `ReadAsync(offset, buffer)` and reuse a bounded buffer to stream large files.

`NinePSession.ReadDirAsync` (the `Session.ReadDirAsync` convenience API) materializes every entry
in memory. `NinePFid.ReadDirAsync` streams pages; dispose the fid after breaking enumeration.
Repeated nonadvancing .L cookies are rejected. Cookies are opaque 64-bit values, not array indices.

Names are compared without Unicode normalization. `Limits.MaxNameLength` counts UTF-8 bytes,
up to the protocol's hard 255-byte limit. A legal name over the configured limit is ENAMETOOLONG;
a malformed name is a protocol error. Long paths are split by both MAXWELEM and encoded frame
size. A partial walk stopping at a file with components remaining is ENOTDIR.

## Empty file writes

`NinePFid.WriteAllAsync` with empty content sends no `Twrite` and leaves bytes unchanged. `NinePSession.WriteFileAsync` opens with truncate, so empty content empties a plain file at open; an append-only file ignores truncation and keeps its bytes. A zero-byte wire write is tested separately.
