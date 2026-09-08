# Security

The seven items of [ARCHITECTURE.md §8](9p/ARCHITECTURE.md), what this repository does about each,
and the test that says so. Nothing here is a claim without a test behind it; where a check could be
deleted and the code still pass, the deletion has been performed and the named test watched to fail
(the qode task notes, kept per machine under `.qode/contexts/`, record every mutation with its date).

## 1. Named tests for the current reference rules

The current reference rules cover framing, identity, lifetime, projection and progress. The table in
[docs/protocol.md](protocol.md) says where each one lives and which test pins it;
[docs/rule-index.md](rule-index.md) maps the current rules and acceptance obligations to named
executable tests; non-test workflow gates are explicitly identified. The gate rejects unresolved
tests, missing assemblies and skipped placeholders in the current build configuration.

The security-relevant ones have been mutation-tested: the check is deleted, the build run, the named
test watched to fail, and the mutation reverted by a reverse edit. Among them — the pre-negotiation
cap, the strict UTF-8 decoder, the flush CAS, the two in-flight semaphores, partial-walk binding, the
`n_uname` half of the afid triple, the identity source, the per-user scope of the `todofs` queries,
the fid cap, the `nwname` guard, the frame-size bound, the read-header deadline, and the `Tread`
count clamp.

## 2. Fuzzing

`tests/NineP.Fuzz` is a SharpFuzz / libFuzzer target over the decoder, its corpus seeded from the 77
golden vectors of `docs/9p/fixtures/wire-vectors.json` (committed as hex, because the repository
refuses to track a file containing control bytes, and materialised as `.bin` by
`--seed-corpus`). CI runs a 60 s budget of the deterministic loop on Linux, macOS and Windows.

The gate never depends on libFuzzer building: the same program runs a deterministic mutation loop
with no instrumentation at all, and `CodecProperties` runs FsCheck properties plus a 20 000-input
in-test mutation loop on every platform. A local run of the deterministic loop covered **2 524 788
inputs in 3 s** with no exception other than `NinePProtocolException`.

## 3. No secret in a log, an error or an `Rerror`

Secrets are compared through the single `ConstantTime.Equals` path, which is
`CryptographicOperations.FixedTimeEquals`; credential material is zeroed with
`CryptographicOperations.ZeroMemory` wherever its lifetime allows. Tokens are never logged, never
stored and never readable back.

Untrusted strings reach a log only through the internal `NineP.Protocol.Internal.UntrustedText.Sanitize`, which escapes control characters
and caps at 256 bytes (reference §8 rule 11). An `ename` is truncated at `ERRMAX − 1` on a rune
boundary and never carries a path, a stack trace or the input verbatim (rule 10).

## 4. Safe TLS and WebSocket defaults

TLS 1.2 is the floor and 1.3 is preferred, fixed in the library rather than inherited from the
machine. Renegotiation is off on both sides for the same reason: `TlsTransport` sets
`AllowRenegotiation = false` on the server and the client `SslAuthenticationOptions` it builds, and
`TlsTransportTests.RenegotiationIsOffOnBothSides` asserts it, so the guarantee is this code's and
not the BCL default's. The chain and the host name are verified by default. `AdditionalPeerCheck` may only add a
check, never rescue a certificate the platform rejected; the only way to accept an untrusted
certificate is `AllowInsecureCertificates`, which is explicit, logged at `Warning` every time it is
used, and client-side only — a listener still verifies whatever a client presents. Mutual TLS is optional and exposes the certificate as `PeerIdentity.ClientCertificate`.

WebSocket connections are binary-only — a text message closes the socket 1002 — carry an optional
origin allow-list that also refuses a request with no `Origin` at all, and enforce their size cap
**during** accumulation rather than after the final fragment, so an oversize message is refused
rather than buffered.

## 5. Resource caps, proven against a hostile client

Resource bounds and their defaults are
[ARCHITECTURE.md §4](9p/ARCHITECTURE.md)'s:

| Bound | Default | What happens at the bound |
| --- | --- | --- |
| `MaxMsize` / `MinMsize` | 1 MiB / 4096 | a frame above it closes the connection; an msize below it is answered `"unknown"` |
| `PreNegotiationFrameCap` | 8192 | a pre-`Tversion` frame above it closes the connection — the configured maximum is **not** the bound here |
| `MaxFidsPerConnection` | 65 536 | `Rerror "too many fids"` / `Rlerror ENFILE`; the connection stays up |
| `MaxInFlightPerConnection` | 256, of which 8 are reserved | excess ordinary requests receive EAGAIN; the reader continues processing Tflush |
| `FlushReservePerConnection` | 8 | `Tflush` keeps being read and answered while the general window is full |
| `MaxInFlightPerListener` | 4096 | excess ordinary requests receive EAGAIN; the partial connection slot is released |
| `MaxConnectionsPerListener` | 1024 | the accept loop stops accepting; queued connections wait, they are not reset |
| `ReadHeaderTimeout` | 30 s | a connection that has begun a frame and not finished it is closed `Timeout` |
| `MaxAuthBytes` / `AuthTimeout` | 64 KiB / 30 s | the afid exchange is aborted and the attach fails `EACCES` |
| `MaxNameLength` / `MAXWELEM` | 255 / 16 | a typed codec error, `Name` or `NWName` |

Two of those numbers exist because of each other. 256 in flight per connection with 1024 connections
would admit 262 144 concurrent handler calls, so there is a second, listener-wide bound; and a
per-connection bound on its own would stop `Tflush` being read as well, so a slice of it is reserved
for `Tflush` alone — otherwise a client that filled its window could never cancel anything in it.

`HostileClientTests` and conformance **Part D** prove all of this against a running server: a size
lie of `0xFFFFFFFF`, an oversize pre-`Tversion` frame, a `Twalk` with `nwname = 17`, 70 000 distinct
fids, 300 concurrent tags, an oversize `Tread` count, and half a header followed by 31 s of silence.
These tests check isolation from an offending connection; the transport regression tests also
cover pending handshakes, and lifecycle tests cover repeated closed sessions. Per-connection allocation is measured with `GC.GetTotalAllocatedBytes` and stays a small
multiple of the negotiated msize: a connection claiming a 4 GiB frame costs about 23 KiB at an msize
of 8 KiB, within a couple of kibibytes of what an ordinary frame costs.

## 6. Dependencies

Exact pins, no floating versions, central package management, a `packages.lock.json` per project,
and `dotnet restore --locked-mode` in CI. Every **runtime** dependency has a Decision Log row in
[ARCHITECTURE.md](../ARCHITECTURE.md) with its version and why the BCL was not enough. No package in
the list runs install scripts.

All three published packages depend on `Microsoft.Extensions.Logging.Abstractions` 8.0.3, with
`Microsoft.Extensions.DependencyInjection.Abstractions` 8.0.2 transitively. `NineP.Protocol` also
requires `System.IO.Pipelines` 8.0.0 on net8.0. SQLite, JWT validation and NLog belong to the
examples. Example/test hosts resolve newer logging abstractions for NLog 6.2; those host choices
do not raise the published packages' minimum versions.

CI audits with `dotnet list package --vulnerable --include-transitive`. That command exits 0 even
when it finds something, so the step greps its output for the severity words and fails on a hit;
`NuGetAudit` at level `low` is the belt to that pair of braces.

## 7. SQL only through parameters

The only SQL in the repository is in `examples/NineP.TodoFs/Storage/`, and
`RepoHygieneTests.SqlIsConfinedToTheTodoFsStore` walks every tracked file to keep it that way. Every
value is a parameter. The one interpolated fragment anywhere in the store is a column name that
comes from a closed enum and never from a client, and the `CA2100` suppression at that site says so.

Per-user isolation is a **query** and not a comparison in a handler: `WHERE name = @name AND id =
@uid`, scoped by the attached user's id. A name comparison in the handler would have passed the same
tests, and deleting the scope would then have broken nothing;
`TodoFsIsolationTests.UserACannotWalkReadStatOrListUserB` bites the query.

## Input validation

Every field of every message is bounds-checked **before** anything is allocated: a peer that claims
65 535 names costs nothing until the count has been checked against `MAXWELEM`. Strings are strict
UTF-8 with no NUL. Names reject `/`, reject `.`, allow `..` only in a `Twalk`, and are capped at 255
bytes. Arithmetic that could overflow `u64` — `Twrite.offset + count`, `Tsetattr.size`,
`Tlock.start + length` — is guarded explicitly, because C# unsigned arithmetic does **not** throw.

## Permissions

`PermissionChecker` evaluates `Attr.Perm` against the fid's **implicit identity** — the user of the
attach that created the fid, never a field of the message being answered — and it runs in
the core, **before** the handler is called. A handler may check more; it can never be reached with
less. The table is in [docs/server.md](server.md).

## Data at rest

The packages persist nothing. `jsonfs` holds its document in memory and writes it back only with
`--write-back`, atomically. `todofs` stores user names and list and item text in SQLite and **never**
stores a token. Logs contain no secrets and no raw untrusted strings.

## Reporting

This repository is an implementation exercise and has no published security contact. Treat anything
found here as you would any unreleased code: the version is `0.1.0` and nothing has been published
to nuget.org.

## Lifetime and connection progress

Each fid operation holds a lease. Closing rejects new work and waits for active work before disposing
handlers, streams or synchronization primitives. Reset and disconnect use the same ORCLOSE, xattr
commit and exclusive-open cleanup as explicit clunk, logging errors when no reply exists. A handler
that ignores cancellation keeps its resources until it returns; cancellation cannot safely destroy
an object still executing user code. Completed sessions are removed from listener bookkeeping.

TLS, WS and WSS process handshakes concurrently within `TcpTransportOptions.MaxConnections`.
Pending handshakes, completed unclaimed connections and handed-off connections share that cap.
Silent peers retain only their own slots until the handshake deadline; disposal releases pending and
unclaimed connections. Custom roots preserve server/client authentication EKUs, including intermediate
restrictions, and do not bypass hostname checks. Xattr values and name lists require read permission
before their handlers run. The new named regression tests supplement the historical mutation checks;
a claim of a complete new mutation campaign is not implied.

After a connection has closed, cleanup failures are observed and logged. If the logging sink itself
throws during that cleanup, remaining fids and synchronization resources are still released.
This cleanup exception does not change the documented live-connection policy for throwing loggers.
