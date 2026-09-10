# The protocol, and how it maps onto C#

The normative description of the wire protocol is
[docs/9p/protocol-reference.md](9p/protocol-reference.md), vendored from the workspace and **not
edited here**. This document does not repeat it. It says which C# type implements which part of it,
so that a reader with the reference open can find the code, and a reader with the code open can
find the rule.

Section numbers below are the reference's.

## Dialects

`NineP.Protocol.Dialect` is `{ P9_2000, P9_2000_u, P9_2000_L }` — three separately negotiated
protocols, not one protocol with flags. The umbrella name "9P2000.uL" never goes on the wire.

`NineP.Protocol.Negotiation.Negotiator` implements §5.1 exactly: `VersionString` and
`TryParseVersion` for the three strings, and `Negotiate` for the algorithm, whose 56-row oracle is
regenerated from the shipped code and byte-compared against [negotiation.json](negotiation.json) on
every test run. Two rules the algorithm makes easy to get wrong, and which the oracle pins:

- the answer is conditioned on the **configured dialect set**, so a server told to speak only
  `9P2000.L` answers `"unknown"` to a `9P2000` client rather than downgrading to something it was
  not configured for;
- an `Rversion "unknown"` echoes the client's msize back unchanged, because no msize was agreed.

Before a dialect is agreed the only message a connection accepts is `Tversion`. Anything else draws
a **9P2000-shaped** `Rerror "version not negotiated"` — never an `Rlerror`, because no dialect is
agreed and only that form is universally decodable — and the connection is then closed (§8 rule 9).

## Messages (§2, §3)

One `readonly record struct` per T- and R-message, in `NineP.Protocol.Messages`, named exactly as
the protocol names them: `Tversion`, `Rwalk`, `Twrite`, `Rgetattr`. There are 34 T/R pairs and 66
records — `Terror` and `Tlerror` do not exist, which is why the count is 66 and not 68.

Dialect variants are **fields whose presence the session dialect decides**, never separate types and
never sniffed from the bytes:

| Message | `.u` and `.L` add | Only `.u` adds |
| --- | --- | --- |
| `Tauth`, `Tattach` | `n_uname[4]` | — |
| `Tcreate` | — | `extension[s]` |
| `Rerror` | — | `errno[4]` |
| stat record | — | `extension[s] n_uid[4] n_gid[4] n_muid[4]` |

`MessageTypes` holds the numbers, the `R == T + 1` rule, the name table and the per-dialect legality
bitmap; a type a dialect does not carry never reaches a handler.

`Tfsync` is the one message whose length is ambiguous in the sources: diod sends
`fid[4] datasync[4]` (15 bytes) and hugelgupf/p9 sends `fid[4]` (11 bytes). The encoder **always**
writes 15; the decoder accepts both and defaults `datasync` to 0. Both forms are golden vectors.

## Codec (§4, §8)

`NineP.Protocol.Codec.MessageCodec` is the public façade: `Decode<T>`, `TryDecode<T>`, `Encode`,
`GetEncodedSize`, and `PeekSize` / `PeekType` / `PeekTag` for reading a frame's header without
decoding it. The decoder and encoder themselves are internal, because their signatures carry
`ReadOnlySequence<byte>`, `PipeReader` and `ArrayPool<byte>`, none of which belongs in a public
signature.

Framing is bounded before it is anything else. The 4-byte `size` is read and validated against the
**active bound** before a single further byte is waited for:

- until `Rversion` the bound is the constant `Limits.PreNegotiationFrameCap`, 8192 bytes — never the
  configured maximum, which an unauthenticated peer could otherwise make every connection reserve
  by lying in one field;
- after `Rversion` it is the negotiated msize;
- a violation **closes** the connection: a stream that lied about a length cannot be resynced.

A peer that begins a frame and then stops sending is closed with `CloseReason.Timeout` once
`Limits.ReadHeaderTimeout` has elapsed. The deadline runs from the first byte of a frame to its
last, so a connection idle *between* frames is untouched.

Payloads are borrowed, not copied: `Twrite.Data` and `Rread.Data` are `ReadOnlyMemory<byte>` views
of the frame, valid for the duration of the handler call. A frame that arrives in one piece is
decoded in place; a frame split across pipe segments is copied **once** into a pooled rental.

Every validation failure is a `NinePProtocolException` carrying a machine-readable
`ProtocolErrorKind`: `Size`, `Bounds`, `Utf8`, `Nul`, `Name`, `NWName`, `Trailing`, `Overflow`,
`Stat`, `Type`. The mutation matrix of reference §9 is a test, and every row of it produces the
kind the reference names.

## Strings and names (§8 rule 3)

Strings are strict UTF-8: `System.Text.Encoding.UTF8` is **banned** in this repository because it
substitutes U+FFFD for invalid input, and a protocol decoder that silently repairs its input is a
protocol decoder that disagrees with its peers. The codec uses a private
`UTF8Encoding(false, throwOnInvalidBytes: true)`. A NUL byte in a string is a `Nul` error. A name
may not contain `/`, may not be `.`, may not exceed 255 bytes, and may be `..` only in a `Twalk`.

## Qids, modes and the unified attribute model (§4.1, §7)

`Qid` is `type[1] version[4] path[8]`. The low two type bits follow **Linux**
(`QTSYMLINK 0x02`, `QTLINK 0x01`), not the `.u` draft, because Linux is what v9fs speaks. In
`9P2000.L` the type is derived from the POSIX file type: `S_IFDIR → QTDIR`,
`S_IFLNK → QTSYMLINK`, everything else `QTFILE` — a client that needs to know a socket from a FIFO
reads `Rgetattr.mode`, because 9P's qid has no bit for either.

Above the codec there is exactly one attribute model, `Attr` and `SetAttr`, and handlers see only
that. `Tstat` versus `Tgetattr`, `Twstat` versus `Tsetattr`, `DM*` versus `S_IF*`, a 9P2000 stat
record versus a 160-byte `Rgetattr` — all of that is the codec's, in one internal projector, in one
place, in both directions.

**Projection is honest** (§8 rules 15–17). Because one model serves three wires, some of what
`Attr` and `SetAttr` can say has no spelling in the dialect that has been negotiated, and the rule
for every such case is the same: **refuse, never drop**. `AttrProjector.ToWstat` refuses an update
naming the owner or `atime` (`EPERM`, stat(5)), a "use the server's clock" time or a numeric group
outside `.u` (`EINVAL`); `AttrProjector.ToSetattr` refuses a name or a group *name* (`EINVAL`;
`Tsetattr` has neither field); `ModeBits.ToOpenByte` refuses `O_EXCL`, `O_DIRECTORY` and
`O_NOFOLLOW`, and `ModeBits.ToLinuxFlags` refuses `ORCLOSE` (`EOPNOTSUPP`). Dropping any of them
would answer success for work nobody did — and in the `Twstat` case would do something else
entirely, since the record with no field set is stat(5)'s fsync (§4.2).

What a dialect genuinely cannot represent stays unrepresented rather than becoming a fabricated
answer. Plain 9P2000 has a mode bit for directories and nothing else, so a fifo, a socket and a
device are reported there as plain files (reference §7: "not representable (plain file)") — but a
qid marked `QTSYMLINK` still yields `kind = symlink`, because `Attr.Kind` and the qid type byte
always agree, and the symlink's target stays unknown because 9P2000 has no extension field. In the
other direction, projecting a symlink *into* a 9P2000 stat record is refused outright. Reading an
`Rgetattr` honours `valid`: an unmarked field keeps the `Attr` default rather than the zero the
fixed-size reply pads with, and with `MODE` unmarked the kind comes from the qid.

## Errors (§8 rule 10)

One value type, `NinePError`, carrying `(Ename, Errno)`. The codec projects it by dialect:
`Rerror ename` in 9P2000, `Rerror ename + errno` in `.u`, `Rlerror errno` in `.L`. A handler raises
a `NinePException` carrying a `NinePError`; the ename for a bare errno comes from the fixed table in
`ErrorTable`, which uses Plan 9's wording where one exists (`"file not found"`,
`"permission denied"`) and `strerror` text otherwise.

Because 9P2000 carries only the ename and `.L` carries only the errno, **only a pair that is a row
of `ErrorTable` round-trips identically in both directions**. An error invented as a free pair will
come back as something else in one dialect or the other.

An `ename` is truncated to `ERRMAX − 1` bytes on a rune boundary and never carries a path, a stack
trace, or the untrusted input verbatim.

## Where each rule of §8 lives

| Rule | Implemented in | Pinned by |
| --- | --- | --- |
| 1 size bounds, close on violation, the 8192 pre-negotiation cap | `FrameReader` | `FrameReaderTests`, `HostileClientTests` |
| 2 counted fields, `nwname`, trailing bytes, stat inner size | `MessageDecoder`, `StatCodec` | `MutationMatrixTests`, `TfsyncTests` |
| 3 UTF-8, NUL, `/`, `.`, `..`, 255 bytes | `WireReader`, `NinePText` | `WireReaderTests` |
| 4 `Twrite.count == size − 23`; the service bound clamps | `MessageDecoder`, `Dispatcher` | `MutationMatrixTests`, `TwriteTests` |
| 5 u64 overflow guards | `MessageDecoder` | `OverflowTests` |
| 6 duplicate tag, `NOTAG` | `TagTable` | `TagTableTests` |
| 7 fid rules, the cap, the afid triple | `FidTable` | `FidTableTests`, `AfidTests` |
| 8 two in-flight bounds and the flush reserve | `ServerSession` | `BackpressureTests`, `HostileClientTests` |
| 9 pre-negotiation `Rerror` and close; mid-session `Tversion` resets | `ServerSession` | `ServerVersionTests` |
| 10 errors carry no internals; `ERRMAX − 1` on a rune boundary | `NinePError` | `ErrorTests` |
| 11 untrusted strings escaped and capped at 256 bytes | `UntrustedText.Sanitize` | `LoggingTests` |
| 12 client: unknown tag, unexpected type, oversize reply | `TagMultiplexer` | `TagMultiplexerTests` |
| 13 client: over-count replies, `nwqid > nwname`, split dirents | `NinePSession`, `NinePFid` | `ClientProtocolErrorTests` |
| 14 client: wait for `Rflush` before reusing a tag | `TagMultiplexer` | `ClientFlushTests` |
| 15 client: refuse locally what the dialect cannot express | `ModeBits`, `AttrProjector`, `NinePSession` | `ClientProjectionTests`, `ModeBitsTests`, `AttrProjectorTests` |
| 16 client: `OEXEC` → `O_RDONLY`; a full sync satisfies a data sync | `ModeBits`, `NinePFid` | `ModeBitsTests`, `ClientProjectionTests` |
| 17 client: read only what `Rgetattr.valid` marks; kind agrees with the qid | `AttrProjector` | `AttrProjectorTests`, `ClientProjectionTests` |
| 18 client: the `Rversion` must answer the offer; a stray one is rule 12 | `NinePClient`, `TagMultiplexer` | `ClientProjectionTests`, `TagMultiplexerTests` |
| 19 `DMAPPEND`/`DMEXCL`/`DMTMP` honoured in `Twstat.mode` and `Tcreate.perm` and read back; `DMAUTH`/`DMMOUNT` refused; the `.u` `DMSETUID`/`DMSETGID`/`DMSETVTX` honoured | `Dispatcher`, `AttrProjector`, `NinePFid` | `CreateTests.CreateCarriesTheFileFlagsToTheHandler`, `WstatTests.ChangingAFileFlagReachesTheHandler`, `ClientProjectionTests.AHalfStatedModeWordIsCompletedFromTheRecord`, `WstatTests.TheDotUPermissionBitsRoundTripThroughWstat` |
| 20 `Tunlinkat.flags`: `AT_REMOVEDIR` required for a directory, refused otherwise | `Dispatcher` | `UnlinkatTests.DirectoryWithoutRemovedirIsEisdir`, `FileWithRemovedirIsEnotdir`, `AnUnknownFlagIsEinval` |
| 21 `Txattrcreate` with `attr_size = 0` removes the attribute | `Dispatcher` | `DispatcherTests.XattrcreateWithZeroSizeRemovesTheAttribute` |
| 22 `btime`, `gen` and `data_version` marked valid only when non-zero | `Dispatcher`, `AttrProjector` | `GetattrTests.FieldsTheHandlerLeftZeroAreNotMarkedValid` |
| 23 open of a symlink is `ELOOP`, of an unopenable node `ENXIO`, `O_DIRECTORY` on a file `ENOTDIR` | `OpenState`, `Dispatcher` | `OpenStateTests.OpenOfASymlinkIsEloop`, `OpenOfAFifoIsEnxio`, `ODirectoryOnAPlainFileIsEnotdir`, `NofollowOnASymlinkIsEloop` |
| 24 `.u` `Tcreate` with `DMDEVICE` parses `"b maj min"` / `"c maj min"` | `Dispatcher`, `AttrProjector` | `CreateTests.DeviceCreateParsesTheExtension` |
| 25 `Tcreate` of a directory with `OTRUNC` or `ORCLOSE` is refused | `OpenState`, `Dispatcher` | `CreateTests.DirectoryCreateRefusesTruncateAndRemoveOnClose` |
| 26 a handler's clunk-time error is the reply, with the fid freed regardless | `Dispatcher` | `ClunkRemoveTests.AHandlersClunkErrorIsTheReply` |
| 27 handlers apply an update whole or refuse it whole | the example handlers | example tests, see [docs/examples.md](examples.md) |

[docs/rule-index.md](rule-index.md) is the full map — every rule, acceptance criterion and exit
criterion to the named test that pins it — and a test reads it, so it cannot fall behind the code.

## Lifetime and progress rules

The current [rule index](rule-index.md) includes reference rules 28–36 for the reviewed lifetime,
append, walk, permission and transport behavior. Rule 8 now returns EAGAIN for ordinary requests
that exceed either worker budget while continuing to receive Tflush. Each dialect carries the same
resource error in its own error shape. A client should retry ordinary work after capacity recovers;
it must continue reading replies. See [server.md](server.md) and [client.md](client.md) for the
observable API behavior and ownership rules.

Rules 37 and 38 are the client's behaviour against real peers, found by the interop runs
(`ClientInteropRegressionTests`): the `Trename` fallback when `Trenameat` is `EOPNOTSUPP`, and an
error answering the `Tversion` being a version error rather than a stray tag. Rule 39 is the ename
table: every string sent over 9P2000 is one the Linux kernel maps to the same errno
(`ErrorTableTests`, held to `docs/9p/fixtures/linux-9p-errors.json`).
