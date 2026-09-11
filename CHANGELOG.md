# Changelog

All notable changes to this repository are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the versions follow
[semantic versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.3.0] — 2026-09-11

### The shared test suite — 2026-09-11

- **This port's suite becomes the workspace's shared test suite** (workspace ARCHITECTURE.md §13,
  exit criterion 10). `docs/9p/fixtures/test-index.json` lists **785 obligations** over 43 areas —
  744 `required`, 41 `recommended` — seeded from the 808 test methods of `NineP.Protocol.Tests`,
  `NineP.Client.Tests` and `NineP.Server.Tests`, with 23 declared this port's own. An id names a
  behaviour (`create/server-owned-flag-refused`), never a method name, so every port maps the same
  obligations onto its own naming convention.

- **`docs/test-map.json` and `TestIndexTests` keep the two honest, in both directions.** An
  obligation nobody discharged fails; a test nobody classified fails too, which is what stops the
  index falling behind the suite. `scripts/gen-test-index.py` is the one-off bootstrap that seeded
  the index; from here both files are edited by hand. See CONTRIBUTING.md §Adding a test.

### Permission bits are a flags enum — 2026-09-11

- **Breaking: `FilePermissions` replaces the raw `uint` permission word.** `Attr.Perm`,
  `SetAttr.Perm` and `CreateRequest.Perm` are now `NineP.Protocol.FilePermissions`, a
  `[Flags] enum : uint` naming every bit of the `07777` mask (reference §4.7) plus the combinations
  a handler actually writes. The reference spells these in octal and C# has no octal literal, so
  `Perm = 0x1ED` had to be decoded by hand; it is now
  `Perm = FilePermissions.OwnerAll | FilePermissions.GroupReadExecute | FilePermissions.OtherReadExecute`.
  The numeric values are the POSIX ones, so `(uint)perm` is the wire value and a cast is the whole
  migration for a caller that already had the bits right. Nothing on the wire changed.

- **Breaking: `NinePFid.CreateAsync` takes a kind and file flags instead of a raw `Tcreate.perm`
  word.** It was the one client method whose `perm` was not a permission: `DMDIR` in it made a
  directory and `DMAPPEND` / `DMEXCL` / `DMTMP` set the file flags, which is why `MkdirAsync` had
  to write `perm | 0x80000000u`. The signature is now
  `CreateAsync(name, kind, perm, mode, flags, fileFlags, cancellationToken)`, mirroring
  `CreateRequest` on the server side, so what used to be a bit test is a type. A create of a
  symlink or a device through it is refused rather than sent with the `.u` extension field empty
  (§8 rule 15); `SymlinkAsync` and `Tmknod` carry those. `.L` refuses a directory (that is
  `MkdirAsync`) and refuses `fileFlags` as before. `MkdirAsync` and `CreateFileAsync` keep their
  0755 and 0644 defaults, now spelled as enum values.

- **Two refusals the old signature could not make** (reference §8 rules 15 and 19, rule-index rows
  188 and 189). A create through a fid naming a symlink, device, fifo or socket is refused with
  `EOPNOTSUPP`: their payload travels in the `.u` extension field, which this create does not send,
  so the old spelling — `DMSYMLINK` in the perm word — went out as a `Tcreate` with the field empty
  and was answered `Rcreate`, a symlink to nowhere. `SymlinkAsync` and `Tmknod` carry those. And a
  create asking for `FileFlags.Auth` or `FileFlags.Mount` is refused with `EPERM` before a
  `Tcreate` is built, the same refusal the server makes on receipt, one hop earlier.


## [0.2.0] — 2026-09-10

### Abuse budgets — 2026-09-10

- **`Limits` gains four bounds and a window** (reference §8 rule 40, ticket 015).
  `MaxRequestsPerSecondPerConnection` and `MaxRequestsPerSecondPerListener` meter ordinary
  requests, answering `EAGAIN` at once past the budget and never metering `Tversion` or `Tflush`;
  both default to 0, which is off, because only the operator knows what a request costs their
  handler. `MaxConnectionsPerAddress` (64) stops one host holding every slot of
  `MaxConnectionsPerListener`. `MaxAuthFailuresPerAddress` (32) with `AuthFailureWindow` (1 min)
  refuses a `Tauth` from an address that keeps beginning exchanges without one reaching a
  successful attach, **before the authenticator is asked**, so a peer that is guessing stops
  making the server pay for a PBKDF2 derivation. The in-process transport is exempt from the two
  address budgets. `ServerCounters` gains `RequestsMetered` and `AuthAttemptsThrottled`.

- **jsonfs bounds its entries and can coalesce write-back.** `--max-entries` (100000) refuses the
  create or `mkdir` that would exceed it with `ENOSPC`, whole or nothing, beside the existing byte
  and depth caps, and a document already over the cap is refused at startup. `--write-back-delay`
  rewrites the document once per window instead of once per change, flushing what it owes on a
  graceful stop so a change made just before it survives a restart.

- **todofs enforces per-user quotas.** `--max-lists` (1000) and `--max-items` (10000) refuse the
  `mkdir` that would exceed them with `ENOSPC`, counted and inserted inside one writer
  transaction so two creates racing at the cap yield exactly one row.

- **Interop reads a multi-chunk file.** Every external peer row now also reads a 32 KiB file whose
  every eight-byte block encodes its own offset, at `msize 4096` where the peer allows it, so a
  `Tread` answered out of order, twice or not at all changes the bytes and not only the count.

### Test audit 002 — 2026-09-10

- **Server:** a `Tsetattr` with `valid = 0` validates the fid and answers `Rsetattr` without calling the handler; `ATIME_SET` / `MTIME_SET` without their base bit are `EINVAL`; a directory length change is refused in the core before the handler (`.L`: any size; 9P2000/.u: a non-zero length, per stat(5)).
- **Tests:** the three test projects are organised into seven suites (Conformance, Robustness, Regression, Chaos, StateMachine, Security, Compat) as folders, namespaces and one `Category` trait, enforced by `TestSuiteHygieneTests`; empty and zero-count boundary coverage on the wire, the client API and the codec; a `FaultyTransport` fault injector with chaos tests on both sides; generated fid-lifecycle and tag-multiplexer models. The golden corpus grows from 77 to 88 vectors.
- **Docs:** `NinePFid.WriteAllAsync` documents that an empty buffer sends no `Twrite`; `docs/client.md`, `docs/server.md`, `docs/security.md`, `docs/transports.md` and `CONTRIBUTING.md` describe the new semantics and the suites.

### Added

- **The settable file flags reach the handler.** `SetAttr` gains `Flags` and `CreateRequest`
  gains `FileFlags`: a `Tcreate` whose `perm` carries `DMAPPEND`, `DMEXCL` or `DMTMP`, and a
  `Twstat` that changes them, now reach `IDirectoryHandler.CreateAsync` and
  `IHandler.SetAttrAsync` instead of being refused with `EPERM`, as open(2) ("an exclusive-use
  file if the DMEXCL bit is set, and an append-only file if the DMAPPEND bit is set") and stat(5)
  ("the directory bit cannot be changed by a wstat; the other defined permission and mode bits
  can") require. The core reads the file back and refuses with `EOPNOTSUPP` — removing a file it
  just created — when a handler answered without applying them, so a handler written against
  0.1.0 that ignores the new member still cannot answer success for a plain file. `DMAUTH` and
  `DMMOUNT` stay the server's and are refused with `EPERM` rather than masked away. A file
  created `DMEXCL` is held by its creator from the create. On the client, `NinePFid.SetAttrAsync`
  completes a mode word that states only `Perm` or only `Flags` from a `Tstat`, so a chmod keeps
  a file append-only; a `.L` session refuses `Flags` before the wire, whose POSIX mode word has
  no bit for them, and `NinePFid.CreateAsync` on `.L` refuses a `perm` outside the `07777` bits.
  jsonfs and todofs refuse a create or an update asking for a flag. Wire-visible for 9P2000 and
  `.u` peers; `.L` is unchanged. Reference §8 rule 19 rewritten; rule-index rows 91, 92 and
  141–154. Owner decision of 2026-09-10, conditional on the standard requiring it, which it does.

### Changed

- Reference §8 gains rules 37–39 — the `Trename` fallback, an error answering the `Tversion`
  being a version error, and the Linux ename table — promoted from this port's named tests
  (rule-index rows 139, 140 and 155). `ClientOptions.Dialects` stays by owner decision; the
  question left open on 2026-09-08 is closed.

- **Error strings match the Linux kernel's 9P table.** Over plain 9P2000 an `Rerror` carries only
  the ename, and v9fs maps it through the exact-match table in `net/9p/error.c`; only six of the
  27 enames this table sent were in it, so a refused write on a `version=9p2000` mount read as
  error 526. `ErrorTable` now sends, for every one of the 74 errnos Linux can name, a string Linux
  maps to that errno (a Plan 9 wording where Linux lists one, `strerror` text otherwise), `Errno`
  gains the 48 missing constants, and every string in Linux's table plus every wording this
  table used to send is understood on receipt. Wire-visible for 9P2000 and 9P2000.u peers:
  `unknown fid` is now `fid unknown or out of range`, `too many fids` is `Too many open files in
  system`, `bad message` is `protocol botch`, `read-only file system` is `Read-only file system`,
  and so on; errno values and every `.L` reply are unchanged. One visible consequence for plain
  9P2000 peers: `EPERM` travels as `Operation not permitted` rather than sharing `permission
  denied` with `EACCES`, so a 9P2000 client now recovers `EPERM` where it used to recover
  `EACCES` (conformance E9 and F5 assert it in every dialect). The fixture
  `docs/9p/fixtures/linux-9p-errors.json` is generated from the kernel source at a pinned commit
  and `ErrorTableTests` holds the table to it. Owner decision of 2026-09-10 after the interop runs.

### Interop

- The Linux kernel client (v9fs) mounts `jsonfs` in all three dialects, diod and `p9ufs` serve
  our cli, and plan9port's `9p` reads our server; every row of `docs/interop.md` is now an opt-in
  test in `InteropTests`, and `tests/interop/setup.sh` fetches the peers at pinned versions so the
  results reproduce on another machine.

### Fixed

- Client: a 9P2000.L rename falls back to `Trename` when the server answers `Trenameat` with
  `EOPNOTSUPP`, as Linux v9fs does; diod 1.0.24 implements only the former. Named test
  `ClientInteropRegressionTests.RenameFallsBackToTrenameWhenTheServerLacksTrenameat`.
- Client: an `Rerror` or `Rlerror` carrying `NOTAG` while a `Tversion` is outstanding fails the
  connection with a `NinePVersionException` that quotes it, instead of terminating the session
  over "unknown tag 65535"; diod answers a dialect it does not speak that way. Named test
  `ClientInteropRegressionTests.AnErrorAnsweringTheVersionRequestIsAVersionError`.
- Client: disposing a session whose connection has already died no longer throws (reference §8
  rule 41). The courtesy
  clunks `NinePSession.DisposeAsync` issues cannot reach a dead peer, and the send path rethrows
  whatever the transport raised without wrapping it, so an `IOException` from a real socket — or an
  `InvalidOperationException` from a pipe whose writer was completed — escaped `DisposeAsync` when
  the write lost the race against the reader noticing the termination. `await using` on a session
  whose server has gone away is exactly when a caller can least afford a new exception. Named test
  `ClientLifetimeRegressionTests.DisposingASessionWhoseServerIsGoneIsQuiet`.
- Tests: `FakeOidcIssuer.Dispose` bounds the wait for its serving loop at five seconds. Closing an
  `HttpListener` is meant to wake a pending `GetContextAsync` and the managed listener does not
  always do so, so an unbounded wait could park a whole `dotnet test` run indefinitely — observed
  on macOS at zero CPU. No shipped code is affected.

## [0.1.0] — 2026-09-08

First release of `NineP.Protocol`, `NineP.Client` and `NineP.Server`, published to nuget.org from
the `v0.1.0` tag. The sections down to "The initial build" record what changed between the
initial build of 2026-09-06 and this release; that last section describes the build itself.

### Fixed

- Server: a request's in-flight budget (the per-connection window and the per-listener bound) is
  returned when its reply is queued, under the same gate that frees the tag, and no longer when the
  worker unwinds. The reply could be on the wire before the slot was back, so a client that sent
  its next request the instant it had a reply could draw `EAGAIN` on a legal request; with a
  window of one the macOS CI runner did exactly that. `Tversion` reset and close still wait for
  every worker to unwind. Named test:
  `BackpressureTests.AWindowReusedTheInstantItsReplyArrivesIsNeverRefused`; rule-index row 138.

### Packaging

- The three packages follow the NuGet package authoring best practices they did not yet: the
  author's name rather than a handle, a copyright line, release notes pointing at this file, a
  128×128 icon, and per-package tags on top of the shared ones. The README the packages carry links
  with absolute URLs so that it renders on nuget.org as well as on GitHub. `PackagingTests` pins
  every one of them in the nuspec.

### Releasing

- A `release` workflow publishes the three packages to nuget.org from a `v<version>` tag on
  `main`, after checking the tag against `Directory.Build.props` and `CHANGELOG.md`, that the
  version is not on nuget.org yet, and that the gate is green on that commit; it creates the
  GitHub Release with the changelog section as its notes. Trusted Publishing by default, an API
  key as the fallback. [docs/releasing.md](docs/releasing.md) documents it.

### Continuous integration

- Windows joins Linux and macOS in the CI matrix, so the packages are built, tested and
  scratch-installed on all three; `.gitattributes` checks every text file out with LF everywhere.
  The suite runs there too: the peak-RSS probe reads the kernel's peak working set on Windows
  instead of throwing, the CLI harness compares stderr with one line ending, the fake OIDC issuer
  aborts a response it cannot finish instead of closing it, and a WebSocket close the reader
  initiates mid-message drains the peer's remaining frames for a bounded moment before the socket
  is disposed, since disposing with unread bytes makes Windows reset the connection and the
  peer never sees its 1009 or 1002.
- The checkout, setup-dotnet and upload-artifact actions move to the majors that run on Node.js 24,
  which ends the Node.js 20 deprecation warning on every job.
- A `dependabot-locks` workflow regenerates every `packages.lock.json` on a Dependabot branch and
  reruns `ci` on the result: Dependabot rewrites only the lock file of the project that references
  the bumped package, and the locked-mode restore refused every dependent project's stale lock.

### Review remediation

An independent four-persona review of 2026-09-08 raised 22 findings (15 Critical, 5 High,
2 Medium); all are fixed. The identifiers below are the review's own.

- **C01, C10, C11:** retain TLS peer-purpose constraints with custom trust; accept TLS/WS/WSS
  handshakes independently within the connection cap; safely cancel pending TCP accepts and
  retain only active server-session bookkeeping.
- **C02, C15:** check xattr read permissions before invoking handlers; apply Plan 9 permission
  alternatives in plain 9P2000 and selected Unix classes in `.u`/`.L`.
- **C03, C07, C08:** lease client/server fid lifetimes, reject retired handles before recycled
  numbers can be used, drain active operations before close, and share ORCLOSE/exclusive/xattr
  finalization across explicit close, reset and disconnect.
- **C04, C05, C06:** propagate xattr commit failures; serialize append transfers after short
  writes; honor DMAPPEND metadata and synchronize EOF selection with writes across connections.
- **C09:** return EAGAIN (`resource temporarily unavailable` in plain 9P2000) for excess ordinary
  requests while continuing to process Tflush. This replaces the documented no-refusal behavior.
- **C12–C14:** preserve fid ancestry, return valid partial walks after later-element failures,
  and check directory/search permissions before dot-dot traversal.
- **H01–H03:** reject nonpositive transfer windows before connecting, require every executable
  rule-index obligation to resolve in the current build, and fail incomplete benchmark workloads
  before publishing throughput, including an independent server write-total check.
- **H04:** update shared protocol rules, backport ledger and maintained guides; regenerate API
  pages, remove obsolete pages and verify generated types against the current public assemblies.
  DocFX and the public API analyzer now receive each baseline once.
- **H05:** remove the duplicate full-conformance CI step and duplicate allocation test while
  preserving the complete integration scenario and canonical allocation coverage.
- **M01:** update example logging packages NLog and NLog.Extensions.Logging to 6.2.0. Their
  nonpublished application/test hosts resolve logging abstractions 10.0.11; the published
  libraries retain their compatible 8.0.3 minimum. Lock files record both contexts.
- Add regression coverage for changed protocol, lifetime, transport, transfer and benchmark
  behavior, plus rule-index and generated-documentation validation.

### Changed

- **Breaking.** Optional numeric ids are `uint` with `Constants.NONUNAME` meaning absent, and errno
  is a signed `int` everywhere with `0` meaning none (owner decision of 2026-09-08, workspace
  architecture §12 rule 2; see improvement request IR-5). `Identity.Uid` is `uint` (was `uint?`)
  and defaults to `NONUNAME`; `Identity.Anonymous(string user, uint uid = Constants.NONUNAME)`;
  `StatRecord.NUid`, `NGid` and `NMuid` are `uint` and read `NONUNAME` in a 9P2000 session;
  `Tauth.NUname` and `Tattach.NUname` are `uint`, encoded in .u and .L only and decoded as
  `NONUNAME` from a 9P2000 frame; `Rerror.Errno` is `int` (was `int?`, `0` = none, which is every
  9P2000 reply); `Rlerror.Ecode` is `int` (was `uint`). `SetAttr` keeps its nullable fields: there
  `null` means *do not touch this field*, a different concept. The wire field is `u32`, so
  `MessageCodec.Encode` refuses a negative errno with `ArgumentException` wherever it would go on
  the wire, the decoder rejects an `errno[4]` / `ecode[4]` above `int.MaxValue` as
  `ProtocolErrorKind.Overflow`, and the server projects a handler's negative errno to `EIO` in the
  errno field alone.
- **Breaking.** The client and the server handlers use the protocol's verbs for the same operation:
  `IDirectoryHandler.ListAsync` is `ReadDirAsync`, `ISymlinkHandler.GetTargetAsync` is
  `ReadlinkAsync`, `NinePSession.StatAsync` and `NinePFid.StatAsync` are `GetAttrAsync`, and
  `NinePSession.ListDirAsync` is `ReadDirAsync`. A verb that tracks the `.L` message name maps onto
  the wire without a table, and the thirteen ports that mirror this surface copy the names;
  `ListAsync` and `StatAsync` needed the table, and a reader could not guess one side's verb from
  the other's. `INinePMessages` is unchanged: its methods *are* the message names
  (`StatAsync(Tstat)`, `GetattrAsync(Tgetattr)`, `ReaddirAsync(Treaddir)`), and there they are the
  wire. Owner decision of 2026-09-08, workspace architecture §12 rule 1; see improvement request
  IR-4.
- **Breaking.** Logging is `Microsoft.Extensions.Logging.Abstractions` 8.0.3. `ServerOptions.Logger`,
  `ClientOptions.Logger` and the three transport options now take `Microsoft.Extensions.Logging.ILogger`,
  defaulting to `NullLogger.Instance`. Records carry message templates and event ids (1xxx protocol,
  2xxx client, 3xxx server) rather than a pre-formatted string, so a structured sink keeps the fields.
  A logger that throws now takes the connection with it, where `INinePLogger` was contractually
  forbidden to throw. Superseding Decision Log S-16; see improvement request IR-1.
- `NinePLogger.Sanitize` and `NinePLogger.SanitizedMaxBytes` moved to the new
  `NineP.Protocol.UntrustedText`, unchanged. They implement reference §8 rules 10 and 11 and are used
  for exception messages as well as log records, so they outlive the logger they sat on.

### Removed

- **Breaking.** `INinePConnection.PreservesMessageBoundaries` is removed, together with its four
  implementations. Nothing in the packages read it and the frame reader re-frames every transport
  alike from the `size[4]` field, so a connection is a byte stream and nothing more. In the same
  change `Rread.Data` and `Twrite.Data` state the frame-lease rule on the members themselves: the
  memory aliases the decoded frame and is valid only until that frame is released; a caller who
  keeps it copies. Owner decision of 2026-09-08, workspace architecture §12 rules 5 and 6; see
  Improvement request IR-7.
- **Breaking.** `ClientOptions.TimeProvider` is removed. It was documented as the timeout clock
  and read nowhere in `NineP.Client`: every client timeout runs on the system clock, and a promise
  the client did not keep would have been copied into thirteen ports as if it were kept.
  `ServerOptions.TimeProvider` stays, because the server reads it for qid versions and server-set
  times. Owner decision of 2026-09-08, workspace architecture §12 rule 7; see
  Improvement request IR-6.
- **Breaking.** `NineP.Protocol.UntrustedText` is internal. It was public only because the
  `NinePLogger.Sanitize` it replaced had been, and nothing outside these packages needs it: rule 11
  is an obligation on what the packages log, and an `IRequestLogSink` already receives a sanitised
  summary. `NineP.Protocol` exports 133 public types, down from 136.
- **Breaking.** `NineP.Protocol.INinePLogger`, `NineP.Protocol.NinePLogLevel` and
  `NineP.Protocol.NinePLogger`, including `NinePLogger.Null` (use `NullLogger.Instance`) and
  `NinePLogger.ToWriter` (configure a sink on your logging provider instead — the examples show NLog
  writing to standard error).

### Added

- `ClientOptions.DisposeTimeout` (default 5 s): the total time `NinePSession.DisposeAsync` may
  spend clunking the fids that are still open. The clunks now go out together under that one
  deadline — or `RequestTimeout`, when it is shorter — instead of one after another, so a peer that
  has stopped answering costs one deadline rather than one per fid.

- jsonfs and todofs log a startup warning when a `--tls-*` flag has no `tls://` or `wss://`
  listener, and jsonfs when a `--ws-origin` has no `ws://` or `wss://` one (IR-9). The flags stay
  accepted and unused; the warning is what stops them reading as protection that is not there.

- `Errno.ENXIO` (6) and its `ErrorTable` row, `"no such device or address"` (IR-9, rule 23).

### Changed

- `Twstat`: a field that carries the value the file **already has** is a no-op rather than an
  `EPERM`. A client that fills the record from the `Rstat` it just read — Linux v9fs does exactly
  this for a `chmod` or a `truncate` on a `.u` mount — copies back the file's own `uid`, `muid`,
  `type` and `dev`, and asks for no change by doing so. A value that would actually change one of
  §5.8's unsettable fields is refused as before.

- `ClientOptions.MinDialect` and `NinePClient` document the same negotiation contract (IR-9); the
  class doc's "never silently accepts less than it asked for" contradicted `MinDialect`'s documented
  downgrade. `NinePFid.FsyncAsync(dataOnly: true)` documents that 9P2000 and `.u` send the full
  sync, which satisfies a data-only sync (rule 16). `docs/client.md` gains the negotiation
  contract, a table of every request the client refuses locally, and what a 9P2000 session can
  report about a file; `docs/protocol.md` gains the "refuse, never drop" statement and the §8 rule
  map; `docs/server.md` gains the new refusals.

- The same-tag stress regression (`TagReuseStressTests`) runs in every `dotnet test` at a
  five-second budget from `NINEP_TAG_STRESS_SECONDS`, rather than only in CI's explicit step, which
  now runs it once more at 45 s. Measured: the defect it guards fails the five-second run within a
  second, three runs of three.

- The test projects host the Microsoft.Testing.Platform runner (`UseMicrosoftTestingPlatformRunner`
  in `Directory.Build.targets`). They ran xunit's in-process runner, which rejects every platform
  option as unknown, so the `--timeout 20m` the build props pass, CI's TRX report switch and any
  method filter made `dotnet test` report "Zero tests ran" for the whole solution. CI's test step
  asks for `--report-xunit-trx`, xunit's own reporter, and its scratch-install step creates the
  NuGet config before adding a source to it; neither step could have passed as written.
  `ReadPathTests`, which measures process-wide allocation, runs in a collection with
  parallelisation off: under the platform runner the results streamed to `dotnet test` while the
  rest of the assembly ran landed in every measured window.

### Fixed

- **TLS: renegotiation is off by this library's word, not the platform's.** `TlsTransport` never set
  `AllowRenegotiation` on the `SslAuthenticationOptions` it built, so `docs/security.md`'s claim rode
  on the BCL defaults — `true` on the client side, `false` on the server side. Both builders now
  set it to `false`, and `TlsSecurityTests.RenegotiationIsOffOnBothSides` asserts it (security
  review S-2).

- **Client: a `Tflush` answered with an error stranded `oldtag`.** A server that answers a `Tflush`
  with `Rerror`/`Rlerror` rather than `Rflush` — non-conformant, but seen in the wild — released the
  `Tflush`'s own tag through the ordinary error path and left the flushed request's tag rented for
  the life of the session, and the caller was handed the server's error in place of its own
  cancellation. The error is now treated as the `Rflush` it stands in for: both tags go back to the
  pool, promptly or late, the caller sees its cancellation or timeout as before, and the refusal is
  logged at `Warning` (event 2003).

- **jsonfs: the write-back rename was not itself durable.** `--write-back` flushed the temp file to
  disk and renamed it over the document, but never synced the directory, so a crash after the
  rename could bring the old document back or leave neither name. `JsonFsMutator` now fsyncs the
  directory after the rename — `open(2)`/`fsync(2)`/`close(2)` through libc, since .NET will not
  open a directory; a no-op on Windows — and a directory sync that fails is answered `EIO` rather
  than hidden.

- **todofs: a field open twice lost the first writer's change.** Each open cached the field's bytes
  for the life of the fid, so a write through one open was invisible to the other's reads and the
  other's next partial write merged into its stale copy: `hello` then `J` at offset 0 then `!` at
  offset 5 stored `hello!` and acknowledged it. The merge buffer now belongs to the file and is
  shared by every open of it, and it is dropped when the last one closes.

- **Server: every `Tread` allocated its whole budget.** The read path took a fresh
  `byte[min(count, msize − IOHDRSZ)]` per request regardless of the file's size, against
  architecture §9's rule that the hot path allocates no payload copies. A client that asks for a
  whole `iounit` on every read — the shipped client does, and so does Linux v9fs — therefore paid a
  1 MiB allocation to carry ten bytes at the `.L` default. The payload is now a rental from
  `ArrayPool<byte>.Shared`, returned once the reply is encoded, and it is clamped to what the file
  has left to give. Measured: **5 813 bytes allocated per read of a ten-byte file at an msize of
  1 MiB, against 1 054 899 before**; the 1 GiB throughput rows moved by less than the run-to-run
  spread (`docs/benchmarks.md`).

- **Server: a reply that could not be encoded was dropped in silence.** `ServerSession.CompleteAsync`
  claimed the request's tag and only then encoded the reply, so a stat record too long for its own
  `size[2]` — which a client provokes by attaching with a 30 000-byte `uname` to a tree that reports
  the attaching user as the owner — left the request answered by nobody, with the request-log hook
  told it had been answered `Rstat`. The reply is now encoded first, so the overflow is answered
  `Rerror "value too large"` / `Rlerror EOVERFLOW` like any other error.
- **Server: a legally reused tag could be refused.** `ServerSession.CompleteAsync` queued the reply
  before it freed the tag, and the write loop is not held by the reply gate, so a client that
  reuses a tag the moment its reply arrives — reference §5.3, and what Linux v9fs does on every
  request — could have its next request answered `Rerror "duplicate tag"` / `Rlerror EINVAL` and
  dropped. The tag is now freed first. Measured: 16 connections reusing one tag saw 22 150 334
  round trips with no refusal, where the same run before the fix failed within 1.5 s.
- **Server: the same refusal on the flush path.** `Rflush` was queued while the flushed request's
  tag stayed in the table until the cancelled handler had unwound, so the reuse reference §5.3
  permits the moment the `Rflush` is in hand — which is what v9fs does after a Ctrl-C — was refused
  for as long as the handler took to notice. The tag is now freed before the `Rflush` goes out.
  Measured against the shipped `jsonfs` over TCP with a 512 KiB `Twrite`: 21 of 67 flushed rounds
  refused before, 0 of 104 after.
- **Client: a failed walk waited out its cleanup clunk.** A walk that failed because the peer had
  gone silent then clunked its cloned fid against that same silent peer, costing another
  `2 × RequestTimeout` before the caller heard the walk's own error — four minutes at the defaults.
  The cleanup now carries the session's disposal bound.
- **Client: a late `Rflush` reclaimed neither quarantined tag.** Both were held for the life of the
  session although the server had by then confirmed both. They now go back to the pool.
- **Client: a failed walk could report the wrong error.** `NinePFid.DisposeAsync` swallowed only
  `NinePException`, so a cleanup clunk that timed out replaced the caller's real failure with a
  `TimeoutException`. It now swallows the timeout and cancellation cases too, as its "safe to call
  from a `finally`" contract always promised.
- **Client: disposal against a silent peer grew with the fid count** — five open fids cost twelve
  minutes at the defaults. See `DisposeTimeout` above.
- **Client: a late `Rflush` could terminate the session.** When no `Rflush` arrived inside
  `RequestTimeout`, the `Tflush`'s own tag went back to the pool although the server had not
  confirmed it; an `Rflush` arriving later then met the unknown-tag rule. Both tags are now held
  for the life of the session and a late `Rflush` is dropped.

- **Nothing silent maps to success (IR-9, reference §8 rules 15–27).** An audit of 2026-09-08
  found twenty-odd places where a request was accepted and answered with success while part of it
  was silently dropped. Every protocol violation and every breach of the repository's own rule is
  fixed below, each with a named test that is now part of the specification for the other ports;
  improvement request IR-9 recorded the full table, including what was documented or dropped instead.
- Client: a request the negotiated dialect cannot carry is refused, not sent with the field dropped
  (rule 15). `OpenFlags.RemoveOnClose` on `.L`, and `Exclusive` / `Directory` / `NoFollow` on
  9P2000 and `.u`, are `EOPNOTSUPP`; `SetAttr.GroupName` on `.L`, a numeric `SetAttr.Gid` on plain
  9P2000, and the "use the server's clock" time flags on 9P2000 / `.u` are `EINVAL`;
  `SetAttr.ATime` on 9P2000 / `.u` is `EPERM`. The time cases were the worst: an update carrying
  only `ATimeToNow` projected to the all-don't-touch `Twstat`, which stat(5) defines as fsync, so the
  client asked the server to flush the file and told its caller the change had been applied.
- Client: `OEXEC` on `.L` is sent as `O_RDONLY` (rule 16); it went out as access mode 3, which is
  `O_NOACCESS`.
- Client: an `Rgetattr` is read only as far as `valid` marks it (rule 17). Mode, size, block
  counts, the four times, `gen` and `data_version` were read regardless of the mask, turning "the
  server did not say" into epoch times and zero sizes. An unmarked field keeps its `Attr` default,
  and with `MODE` unmarked the kind comes from the qid type byte.
- Client: a qid marked `QTSYMLINK` is a symlink in every dialect (rule 17). On plain 9P2000 it was
  reported as `FileKind.File` while `Attr.Qid.Type` still said symlink; the target stays unknown
  there, since 9P2000 has no extension field. Directory listings are fixed with it.
- Client: the `Rversion` must answer the offer that drew it (rule 18). Only `MinDialect` was
  checked, so a server could answer a `9P2000` offer with `"9P2000.L"` or a `.L` offer with
  `"9P2000.u"` and be accepted. The only downgrade is version(5)'s suffix-stripping to `"9P2000"`,
  and that is what `MinDialect` gates.
- Client: an `Rversion` with no `Tversion` outstanding terminates the session (rules 18 and 12)
  instead of being discarded.
- Server: `Tunlinkat` honours `AT_REMOVEDIR` (rule 20): required for a directory (`EISDIR` without
  it), refused for anything else (`ENOTDIR`), any other flag bit `EINVAL`. The flag was never read.
- Server: `Txattrcreate` with `attr_size` 0 removes the attribute through
  `IXattrHandler.RemoveXattrAsync`, the way v9fs and diod spell `removexattr(2)`, instead of storing
  an empty value (rule 21). `RemoveXattrAsync` had no caller.
- Server: `Rgetattr.valid` marks `btime`, `gen` and `data_version` only when the handler supplied
  a non-zero value (rule 22); the reply is still 160 bytes and the qid is still valid.
- Server: `Topen` / `Tlopen` of a symbolic link is `ELOOP`, and of a fifo, socket or device the
  server cannot open `ENXIO` (rule 23). An open reply with no open file behind it, whose reads
  answered zero bytes for ever, is no longer sent. `O_DIRECTORY` on a non-directory is `ENOTDIR`
  and `O_NOFOLLOW` on a symlink is `ELOOP`; both flags were decoded and never enforced. A `.u`
  create of a symlink, fifo or socket still succeeds, and leaves its fid not open.
- Server: `Tcreate.perm` carrying `DMAPPEND`, `DMEXCL` or `DMTMP` is refused with `EPERM` instead
  of being masked away, and so is a `Twstat` that changes one of those bits; a bit echoed back
  unchanged stays a no-op (rule 19).
- Server: a `.u` `Tcreate` with `DMDEVICE` parses `extension` as `"b maj min"` / `"c maj min"`
  into `CreateRequest.Rdev` (rule 24); the text used to reach the handler as a symlink target, and
  a missing or malformed one is `EINVAL`.
- Server: `Tcreate` of a directory whose mode carries `OTRUNC` or `ORCLOSE` is `EISDIR`, the answer
  an open of a directory already gives (rule 25); an accepted `ORCLOSE` removed the new directory at
  clunk.
- Server: a handler's clunk-time error is the reply (`Rerror` / `Rlerror`), with the fid freed
  regardless, rather than being swallowed into `Rclunk` or `Rremove` (rule 26).

- jsonfs applies a `Twstat` / `Tsetattr` whole or refuses it whole (rule 27). It used to return
  after the first field it honoured, so an update carrying a length of zero and a name truncated
  the file and silently dropped the rename, and a mode or a time set beside either was answered
  with success and discarded.
- todofs no longer answers success for a `SetAttr` of `Size = 0` while changing nothing (rule 27).
  A length of zero truly empties a list's `name` and an item's `label` and `description`, and is
  `EINVAL` for `status`, whose schema allows only `open` or `done`, and for `/users/ctl`, whose
  length is a rendering of the user table. Any other field in the same update refuses the whole
  update with `EOPNOTSUPP`; a non-zero length is `EOPNOTSUPP` and a length on a directory `EISDIR`.
- todofs performs an `OTRUNC` truncation when the open is answered instead of deferring it to a
  write that may never arrive: the free-text fields become empty and `status` becomes `open`. A
  truncating open of `/users/ctl` is the one documented exception: it changes nothing, because the
  file keeps no bytes of its own and refusing the open would refuse every documented way of writing
  to it.
- todofs answers reads from the database rather than from the write buffer. A fid that wrote
  `add alice` to `/users/ctl` read its own command back instead of the user list, and `status` read
  back `done\n` while the row held `done`, until the last open of the file closed.
- ninep refuses a flag that cannot apply, with exit 3, instead of loading it and dropping it:
  `--tls-ca` / `--tls-cert` / `--tls-key` with a `tcp://` or `ws://` address, `-l` on any command
  but `ls`, and `--oidc-issuer` / `--oidc-client-id` without an oidc grant. The TLS case is the one
  that mattered: a certificate loaded for a plaintext address is how a caller believes a connection
  was encrypted when it was not.
- ninep prints `ninep: server requires no authentication; attached anonymously` on standard error
  when `--auth-optional` falls back to a `NOFID` attach. Standard output is unchanged, so the frozen
  conformance formats are untouched.

- `ENXIO` no longer degrades to `"i/o error"` for 9P2000 peers (rule 23), and the refusals of
  rules 15 and 19 keep their errno on a 9P2000 wire: `ErrorTable` had no row for the `create` /
  `wstat` DMAPPEND / DMEXCL / DMTMP enames, for `"wstat cannot change DMDIR"`, for the six
  `wstat cannot …` refusals the projector raises, or for `"cannot rename across directories"`, so
  each arrived, or was raised locally, as `EIO`.
- A `.u` `chmod u+s` is honoured rather than dropped (rule 19): v9fs sends setuid, setgid and
  sticky as the `DMSETUID` / `DMSETGID` / `DMSETVTX` bits in the high half of `Twstat.mode`, which
  the `07777` mask could never see. Both directions carry them now; plain 9P2000, whose mode word
  has no such bits, refuses the chmod with `EINVAL` (rule 15).

### Documented

- The `Twstat` refusal rules (`uid`, `n_uid`, `muid`, `n_muid`, `atime`, `type`, `dev`, `qid` and
  the `DMDIR` bit) in `docs/server.md` and `docs/api.md`.
- The `EOVERFLOW` refusal of a stat record that will not fit its own 16-bit length field, in the
  same two documents.

- IR-9: `docs/examples.md` gains a todofs "Truncation" section, states that `--write-back`
  implies `--writable`, that both servers create at fixed `0644` / `0755` and ignore
  `CreateRequest.Perm`, `.Gid` and `.Flags`, and that `--jwks-cache` has a five-minute floor; the
  three example READMEs say the same where their flags are listed.

### The initial build — 2026-09-06

The initial build, as it stood before the review and the changes above. Built by
`dotnet pack -c Release` and installed from a local feed by the CI scratch step.

#### Added

**Protocol (`NineP.Protocol`)**

- All three dialects — 9P2000, 9P2000.u and 9P2000.L — as separately negotiated protocols, with the
  negotiation algorithm of protocol-reference §5.1 and a 56-row oracle regenerated from the shipped
  code and byte-compared on every test run.
- Typed message records for all 34 T/R pairs (66 records), with the `.u` and `.L` field deltas
  modelled as fields whose presence the session dialect decides, never sniffed from the bytes.
- A bounded, streaming codec: the size field is validated against the active bound — 8192 before
  `Tversion`, the negotiated msize after it — before any further byte is waited for; payloads are
  borrowed views of the frame; a split frame is copied once into a pooled rental. Every failure is a
  typed `ProtocolErrorKind`.
- Both `Tfsync` forms: 15 bytes are always sent, 11 bytes are also accepted.
- The unified `Attr` / `SetAttr` model of protocol-reference §7, so a handler never sees `Tstat`
  from `Tgetattr`.
- One error model, `NinePError { Ename, Errno }`, projected by dialect to `Rerror`,
  `Rerror + errno` or `Rlerror`, with the fixed ename/errno table of `ErrorTable`.
- Transports: TCP, TLS (1.2 floor, 1.3 preferred, optional mutual TLS), WebSocket (RFC 6455, one 9P
  message per binary frame, origin allow-list) and an in-memory transport, behind an `ITransport`
  seam a caller can implement.
- Authentication interfaces plus `TokenAuthenticator`, `PasswordAuthenticator`
  (PBKDF2-HMAC-SHA-256, 600 000 iterations), `PasswordFileStore`, `TlsClientCertAuthenticator` and
  the four client-side credentials.
- `Limits`, the configurable bound on every resource a client controls, and `INinePLogger` /
  `NinePLogger`, an injected logger with no dependency on any logging framework.

**Client (`NineP.Client`)**

- `NinePClient.ConnectAsync` in three forms — by address, by transport, by connection — with
  dialect preference, a minimum dialect, and msize negotiation.
- A fully pipelined tag multiplexer with a bounded tag pool, replies routed by tag, and
  cancellation mapped onto `Tflush` with the exact flush(5) rules, including waiting for `Rflush`
  before a tag is reused.
- `NinePFid`, a deterministic handle: disposing it clunks the fid.
- A path API — read, write, list, mkdir, create, remove, rename, stat, setattr, symlink, readlink,
  statfs — and `INinePMessages`, one method per T-message, underneath it.
- Chunked whole-file transfers at `iounit` with a configurable in-flight window (default 4).
- The client-side validation of protocol-reference §8 rules 12 to 14.

**Server (`NineP.Server`)**

- `NinePServer` over one or more listeners, with graceful stop and counters.
- The per-file-type handler model: `IFilesystem`, `IHandler`, `IDirectoryHandler`, `IFileHandler`,
  `IOpenFile`, `ISymlinkHandler` and the four optional capability interfaces
  (`ILockCapability`, `IXattrHandler`, `ILinkCapability`, `IStatFsCapability`), where a missing
  capability is `EOPNOTSUPP` from the core and never a crash.
- The core owns version negotiation, the bounded fid and tag tables, `Tflush` semantics with a CAS
  that makes "send the reply" and "suppress it" mutually exclusive, element-wise walk with the
  partial-walk rule, open state including `DMEXCL` and `ORCLOSE`, directory packing for both record
  formats, `iounit`, the attach identity, the dialect projection of every attribute, and permission
  checks **before** the handler is called.
- Two in-flight bounds — per connection and per listener — with a reserve that only `Tflush` may
  draw on, so a client that filled its window can still cancel what is in it.
- The afid exchange: the triple binding of `(uname, n_uname, aname)`, the byte and time bounds, and
  the rule that the session's identity is the authenticator's output and never the client's claim.
- Structured logging through an injected logger, counters by message type, protocol-error kind and
  errno, and a request-log hook whose strings arrive escaped and capped.

**Examples and tooling (not published)**

- `jsonfs`, a JSON document served as a 9P tree, read-only by default.
- `todofs`, a SQLite-backed to-do tree with per-user isolation and a Keycloak bearer-token
  authenticator, tested against an in-repo fake OIDC issuer.
- `ninep`, the conformance cli, with output formats frozen by the workspace fixture and three OIDC
  grants for obtaining a token.
- A conformance driver that runs Parts A to D of the workspace scenario against this repository's
  own `jsonfs` and `ninep` as processes, in all three dialects over TCP, TLS, WebSocket and the
  in-memory transport.
- A libFuzzer target over the decoder, seeded from the 77 golden vectors, with a deterministic
  fallback loop that needs no instrumentation.
- Benchmarks for the four measurements of ARCHITECTURE.md §9, with the numbers in
  [docs/benchmarks.md](docs/benchmarks.md).

#### Known limitations

- Plan 9's `p9any` and `p9sk1` are **out of scope**: `p9sk1` is DES-based and is not production
  security.
- `unix://` is part of the address grammar; no transport in this release binds it.
- `OpenFlags.RemoveOnClose` (`ORCLOSE`) is unreachable in a 9P2000.L session, because `open(2)` has
  no flag that means it. It works in 9P2000 and 9P2000.u, where it is a mode bit.
- Interop has been run against this repository's own peers only. `docs/interop.md` records what was
  and was not run, and why.
- The client's default msize is chosen from the dialect it **offers first**, because nothing has
  been negotiated when `Tversion` is written. A client that offers 9P2000.L and is downgraded to
  9P2000 therefore runs 9P2000 with a 1 MiB msize, while a client that asked for 9P2000 gets
  128 KiB. Both are legal — msize is not dialect-scoped — and `ClientOptions.Msize` makes the
  number explicit. See [docs/client.md](docs/client.md).
- `ninep ls -l` is this repository's own extension. The cross-language fixture fixes only plain
  `ls`, so the `-l` format is documented in [docs/examples.md](docs/examples.md) and is not part of
  the conformance contract.
- The fake OIDC issuer runs from the conformance driver
  (`dotnet run --project tests/NineP.Conformance -- fake-issuer`) rather than from a program of its
  own: the project map of this release is closed at fifteen projects. Its password grant knows one
  user, so the second identity an isolation test needs comes from a directly minted token.
