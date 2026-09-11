# 9P Implementations — Architecture

Binding architecture for every `9p-<lang>` repository in this workspace. Each repository vendors a
copy of this file and of [protocol-reference.md](protocol-reference.md) at setup and must not
diverge from them; if code and this file disagree, this file wins until amended through the
Decision Log below. Language-specific detail lives in the repository's own `ARCHITECTURE.md`
(which imports this one) and in its ticket under [tickets/](tickets).

The ticket that drives all of this is `.qode/contexts/current/ticket.md` at the workspace root. Its
requirements, restated as exit criteria, are in §10.

## 1. What is being built

Fourteen independent, production-grade implementations of the 9P file protocol — 9P2000,
9P2000.u and 9P2000.L (the umbrella "9P2000.uL") — one per language, each shipping:

- a **protocol** package (shared): typed messages, zero-copy codec, transports, auth interfaces;
- a **client** package: connection, session, pipelined transactions, high-level file API;
- a **server** package: listener, sessions, fid/tag bookkeeping, dispatch onto **per-file-type
  handlers** supplied by the developer, authentication;
- two example servers, **jsonfs** and **todofs**, plus a **cli** used for conformance testing;
- documentation, CI, and packaging that produces installable client and server packages.

Priority order (from the ticket): C#, TypeScript, Go, Python, C++, Java, Dart, Swift, Haskell, C,
Rust, Lua, Perl, PHP. The loop in [loop.md](loop.md) works them serially in that order.

## 2. Layering (every language)

```text
 examples/  jsonfs · todofs · cli                     (not published)
     │
 server/   Server · Session · FidTable · Dispatcher · Authenticator ─┐
 client/   Client · Session · Transactions · File API                │  published
     │                                                              │
 protocol/ Message types · Codec · Dialect · Attr model · Transport ─┘
           (TCP · TLS · WebSocket · user-defined) · Auth interfaces · Errors
```

- **Dependency direction:** `examples → client|server → protocol`. `protocol` imports nothing from
  the others. `client` and `server` never import each other. Tests may import anything.
- **Transports live in `protocol`** because both sides need them (Decision Log 2026-09-05).
- **Nothing dialect-specific leaks above `protocol`'s dialect boundary:** handlers see the unified
  `Attr`/`SetAttr` model (protocol-reference §7), never `Tstat` vs `Tgetattr`.

## 3. Protocol package

- **Messages** are typed values, one per T/R type of protocol-reference §3, with the dialect
  variants of `Tauth`/`Tattach`/`Tcreate`/`Rerror`/`Rstat` modelled as optional fields whose
  presence is decided by the session dialect, never by sniffing bytes.
- **Codec** is streaming and bounded: a frame reader that reads the 4-byte size, validates it
  against msize, then parses from a borrowed slice; an encoder that writes into a caller-supplied
  buffer. No per-message heap allocation for fixed-size messages in languages that allow it; data
  payloads (`Rread.data`, `Twrite.data`) are **borrowed views**, copied only by the consumer.
  Every rule in protocol-reference §8 is enforced here with a typed `ProtocolError` carrying a
  machine-readable kind (`size`, `bounds`, `utf8`, `nul`, `nwname`, `trailing`, …).
- **Dialect** is an enum `{P9_2000, P9_2000_u, P9_2000_L}` plus the negotiation function of
  protocol-reference §5.1, unit-tested against the table there.
- **Errors**: one `NinePError` type with `{ename: string, errno: int}`; the codec projects it to
  `Rerror`/`Rerror+errno`/`Rlerror` by dialect. Handlers raise errno-bearing errors; the ename for
  a bare errno comes from a fixed table whose every string the Linux kernel's 9P client maps to
  that same errno (`fixtures/linux-9p-errors.json`, generated from `net/9p/error.c`): the Plan 9
  wording where Linux lists one (`"file not found"`, `"permission denied"`, `"i/o error"`, …),
  otherwise the `strerror` text (`"Invalid argument"`, `"Read-only file system"`, …). Over plain
  9P2000 the ename is all a Linux mount gets, and a string outside that table reaches it as error
  526. Every string in the Linux table is understood on receipt (owner decision, 2026-09-10).
- **Transport** interface: a dialer (`connect(addr) → Conn`) and a listener
  (`listen(addr) → accept() → Conn`), where `Conn` is a bidirectional byte stream with
  `read`, `write`, `close`, an optional `peerIdentity()` (TLS client certificate, WS headers) and
  a `close reason`. Address syntax is a URL: `tcp://host:port`, `tls://host:port`,
  `ws://host:port/path`, `wss://host:port/path`, `unix:///path` where the platform allows.
  - **TCP**: `TCP_NODELAY` on; keep-alive on; accept backlog and per-listener connection cap.
  - **TLS**: TLS 1.2 minimum, 1.3 preferred; server certificate + key from files or the
    platform store; client verifies the chain and hostname by default (opt-out is explicit and
    logged); optional **mutual TLS** with the client certificate exposed as `peerIdentity()`.
  - **WebSocket**: RFC 6455, **one 9P message per binary WebSocket message**; text frames,
    continuation abuse, or a message larger than msize close the socket with 1009/1002.
    Optional subprotocol name `9p`; origin allow-list on the server; ping/pong keep-alive.
  - **User-defined**: implementing the `Transport` interface is the extension point; the test
    suite ships an in-memory pipe transport that every test uses, proving the seam.
- **Auth interfaces** (§5) live here so client and server share the credential types.

## 4. Server package and handler model

The developer never writes protocol code. They supply a **filesystem** made of typed handlers;
the server owns everything in protocol-reference §5 and §8.

```text
Server(options: {transports, dialects, authenticator, limits, logger, clock})
  .serve(fs: Filesystem)          // blocks / runs until stop()
  .stop(graceful: timeout)

Filesystem
  attach(identity: Identity, aname: string) -> DirectoryHandler   // root of the tree for this user
  statfs?(handler) -> StatFs

Handler (common)      getattr() -> Attr · setattr(SetAttr) · qid · clunk(wasOpen) · fsync(dataOnly)
DirectoryHandler      lookup(name) -> Handler · readdir(cursor, max) -> DirectoryListing
                      create(CreateRequest{name, kind, perm, mode, flags, file_flags, target, rdev, gid, identity}) -> Handler
                      remove(name, kind) · rename(oldname, newdir: DirectoryHandler, newname)
                      link?(name, target: Handler)
FileHandler           open(mode, flags) -> OpenFile{ read(offset, buf) · write(offset, data) · size() · close() }
                      lock?(LockRequest) -> LockStatus · getlock?(…)
SymlinkHandler        readlink() -> string
AuthFileHandler       (server-internal; backs the afid)  read · write · verified() -> Identity?
XattrHandler?         list() · get(name) · set(name, value, flags) · remove(name)
```

- "Per file-type" means: a directory implements `DirectoryHandler`, a regular file
  `FileHandler`, a symlink `SymlinkHandler`; each handler type has **all** the operations 9P can
  address to that file type, and the server core maps every T-message onto exactly one handler
  method (table in each repo's `docs/server.md`; the names are fixed by §12). Optional capabilities (`lock`, `xattr`,
  `link`, `statfs`) are separate interfaces; a handler that lacks one gets `EOPNOTSUPP` /
  `"Operation not supported"` from the core, never a crash.
- **The core owns**: version/msize negotiation; the fid table (bounded), tag table (bounded,
  duplicate-tag rejection), `Tflush` semantics, walk element-wise semantics with the partial-walk
  rule, open-state tracking (a fid is open once; `ORCLOSE`; `DMEXCL` exclusivity), directory
  read offset validation and record packing for both `Rread` (stat records) and `Rreaddir`
  (dirents), `iounit`, msize clamping, `Tremove`'s clunk-even-on-error, attach identity binding
  (the implicit user of every fid), dialect projection of `Attr`, the read-back of the file flags
  a create or a `setattr` asked for (protocol-reference §8 rule 19), and permission checks against
  `Attr.perm` + the fid's identity **before** calling the handler (handlers may check more).
- **Concurrency**: one reader and writer per connection, bounded ordinary workers (default 248
  per connection plus 8 reserved control slots; 4096 per listener), and per-fid operation leases.
  Multi-fid operations acquire leases in a fixed order and revalidate entries after waiting.
  Excess ordinary work receives `EAGAIN` immediately, leaving the reader available for `Tflush`.
  The client must still read replies for transport progress. Different fids may execute concurrently;
  operations selecting file length and writing, truncating or changing size also synchronize by
  file identity. Retiring a fid stops new leases, drains existing work, then finalizes exactly once.
  A handler ignoring cancellation keeps its resources until it exits, including during shutdown.
  Listener bookkeeping tracks only active sessions. TLS/WS/WSS handshakes run concurrently inside
  the TCP connection cap, counting pending, ready and established connections together.
- **Limits** (all configurable, defaults): max msize 1 MiB; min msize 4096; **pre-negotiation frame
  cap 8192 bytes** (protocol-reference §8 rule 1 — the negotiated msize does not exist yet and the
  1 MiB maximum must not stand in for it); max fids per connection 65536 (over the cap:
  `Rerror "Too many open files in system"` / `Rlerror ENFILE`); max outstanding requests 256 per connection and
  4096 per listener, of which 8 per connection are reserved for `Tflush`; max connections per
  listener 1024; read header timeout 30 s; idle timeout off; max name 255 bytes; `Twalk` ≤ 16
  elements (protocol); **abuse budgets** (protocol-reference §8 rule 40): requests per second per
  connection and per listener **off** by default, live connections per peer address 64, and
  unverified authentications per address 32 in a 1-minute window. Idle timeout stays off on
  purpose: a v9fs mount sits idle for hours and does not reconnect, so the per-address cap, not a
  timeout, is what stops one host hoarding slots.
- **Observability**: structured logging through an injected logger (never a global), counters
  for messages by type, errors by kind, bytes, connections; a request-log hook receives
  `(identity, T-message summary, R-type, duration)` with strings escaped.
- **Graceful shutdown**: stop accepting, let in-flight requests finish within a deadline, close.

## 5. Authentication

Protocol-level shape (protocol-reference §5.2): `Tauth` opens an **afid**; the client writes a
credential, may read a response, then presents the afid in `Tattach`. What flows over the afid is
not 9P's business; this is the workspace's definition:

- **`Authenticator`** (server): `begin(uname, n_uname, aname) → AuthSession | refuse` ;
  `AuthSession.write(bytes)`, `AuthSession.read(max) → bytes`, `AuthSession.identity() →
  Identity | null` (null until the exchange succeeds). `Identity = {user: string, uid?: int,
  groups: string[], claims: map}`. The core enforces: an attach with an afid whose session has
  no identity → `EACCES`/`"authentication failed"`; **an afid is bound to the triple
  (`uname`, `n_uname`, `aname`) it was created with**, and an attach presenting it must match that
  triple, with an empty `uname` and an `n_uname` of `NONUNAME` accepted as "unspecified"
  (protocol-reference §5.2 and §8 rule 7 — binding on `uname`/`aname` alone lets an afid from
  `Tauth(uname="", n_uname=1000)` satisfy `Tattach(uname="", n_uname=1001)` in `.u`/`.L`); **the
  session's identity is the one `AuthSession.identity()` returned**, never the `uname`/`n_uname`
  the client claimed; afid reads/writes bounded (64 KiB total) and time-boxed (30 s).
- **Default authentication** (shipped, used when no authenticator is configured but auth is
  required): **`TokenAuthenticator`** — the client writes an opaque token (bytes) once; the server
  verifies it with a constant-time comparison against a configured secret **or** against a hashed
  credential store (`PasswordAuthenticator`: `argon2id` where the ecosystem has a vetted
  implementation, otherwise PBKDF2-HMAC-SHA-256 ≥ 600 000 iterations); the reply readable on the
  afid is `ok\n`. This is the "default authentication" of the ticket. Plan 9's `p9any`/`p9sk1`
  is **out of scope** (DES-based; not production security) — documented in every README.
- **No authentication**: `Tauth` is refused (`"authentication not required"` /
  `ECONNREFUSED`), `Tattach` with `NOFID` succeeds; the identity is `uname`/`n_uname` **as
  claimed** and the server must be behind an authenticated transport (mTLS) or trusted network.
  `TlsClientCertAuthenticator` derives the identity from the client certificate's SAN/CN and
  refuses attaches whose `uname` differs.
- **User-defined authentication**: implement `Authenticator`. The `KeycloakAuthenticator` in
  todofs is the worked example (§7).
- **Client side**: `Credential` interface with `TokenCredential`, `PasswordCredential`,
  `BearerTokenCredential` (OIDC), and a callback form; the client runs the afid exchange
  automatically inside `attach()`.

## 6. Client package

- `Client.connect(transport | address, options{dialects: preference list, msize, credential,
  uname, aname, timeouts}) → Session`. Negotiates the first dialect the server accepts; a
  downgrade below `options.minDialect` is a typed `VersionError`.
- Fully **pipelined**: a tag multiplexer with a bounded tag pool (65535), per-request
  cancellation mapped to `Tflush` with the exact flush(5) rules, and reply routing by tag.
- Low-level API: one method per T-message returning the typed R-message.
- High-level API: `Fid` objects (walk/open/read/write/getattr/setattr/dispose, `..` navigation,
  `readAll`, `readDir` returning unified `DirEntry`s for both dialect record formats),
  `openFile(path)`, `readFile(path)`, `writeFile(path)`, `readDir(path)`, `mkdir`, `remove`,
  `rename`, `getattr`, `setattr`, `symlink`/`readlink` where the dialect allows, plus `.L` extras
  (`lock`, `xattr`, `statfs`, `fsync`). The names are fixed by §12.
- Reads/writes are chunked at `iounit` or `msize − IOHDRSZ`; large transfers issue several
  in-flight requests (configurable window, default 4) — this is where throughput comes from.
- Fids are released deterministically (dispose/close/defer/RAII by language); a `Session.close`
  clunks what is left and closes the transport.

## 7. The examples

### jsonfs

`jsonfs --listen <url> [--listen <url>…] --file <path.json> [--writable] [--write-back]
[--dialects 9P2000,9P2000.u,9P2000.L] [--auth none|token:<secret>|password-file:<path>]
[--tls-cert --tls-key --tls-client-ca]`

Mapping (Decision Log 2026-09-05):

| JSON | Filesystem |
| --- | --- |
| object | directory; keys are entry names, percent-encoded so that the mapping stays injective and every name is a legal 9P name. In order: `%` → `%25` **first**, then `/` → `%2F` and NUL → `%00`; if the result then reads `.` or `..` it becomes `%2E` or `%2E%2E`; the empty key `""` becomes the single character `%`, which no other key can produce because every literal `%` was already escaped. Escaping `%` first is what keeps `a/b` (→ `a%2Fb`) distinct from the literal key `a%2Fb` (→ `a%252Fb`). Writes and lookups decode the inverse |
| array | directory; entries `0`, `1`, … in order |
| string | regular file whose bytes are the string's UTF-8, **exactly**, no added newline |
| number | regular file holding the number's text: integral values as plain integers (`42`, `-3`, `1234567890`), others as the shortest round-trip decimal without exponent (`2.5`, ECMA-262 `Number::toString` for 1e-6 ≤ abs < 1e21); values outside int64 or that range are exposed as the source token when the parser preserves it, else documented as a deviation |
| `true`/`false` | regular file holding `true` / `false` |
| `null` | regular, empty file |

Qids are stable for the life of the process (path = allocation counter; version = per-node
modification counter); mode `0644` files / `0755` directories, owner = the attaching user.
Read-only by default. With `--writable`: writing a scalar file replaces the value (text that parses
as a JSON number/boolean/null keeps that type if the original was of that type, otherwise it
becomes a string); creating a file makes an empty string; `mkdir` makes an empty object; in arrays
only the next index may be created and only the last element removed; removing a key deletes it;
renaming a key moves the value. `--write-back` rewrites the JSON file atomically (temp + rename)
after each mutation. Documents ≥ 64 MiB or nested deeper than 256 levels are refused at startup.

### todofs

Exact tree from the ticket:

```text
/users/                    directory
/users/ctl                 admin control file
/users/<user>/             one per user; a user sees ONLY their own directory
/users/<user>/<n>/name     list name (writable)
/users/<user>/<n>/<m>/label|description|status   item fields (writable; status ∈ {open, done})
```

- **Authentication**: the standard `Tauth` exchange, and nothing else. `KeycloakAuthenticator`: the
  client writes the OIDC access token (JWT) to the afid; the server validates the signature
  (RS256/ES256 via the realm's JWKS, cached with rotation, `alg=none` rejected), `iss`, `aud`,
  `exp`, `nbf` (60 s leeway), and maps `preferred_username` (fallback `sub`) to the user. The
  session runs as **that** user; `Tattach.uname` must equal it or be empty, and the afid is bound
  to its `(uname, n_uname, aname)` triple (§5). An attach that presents no afid is refused.
  - Tokens are never logged and never stored.
- **How the token is obtained** (see the `cli` subsection below): the *packages* only ever validate a
  token — no package performs a login, and `BearerTokenCredential` takes a token the caller already
  holds. Acquiring one is the **cli's** job, through three flags:
  - `--auth bearer:<token>` — the caller already has an access token (from `kcadm`, a browser
    session, a CI secret) and passes it in;
  - `--auth oidc-device` — the OAuth 2.0 **device authorization grant** (RFC 8628) against
    `--oidc-issuer`'s discovery document: `POST device_authorization_endpoint`, print the
    `verification_uri_complete` and `user_code` to stderr, poll `token_endpoint` at `interval`
    honouring `authorization_pending`/`slow_down` until `expires_in`, then use the `access_token`.
    This is the interactive login the master ticket's "authenticated via keycloak login" asks for,
    and it needs no browser redirect back to the cli;
  - `--auth oidc-password` — the resource-owner password grant, `--oidc-client-id` plus a username
    and a password read from the terminal or `$NINEP_PASSWORD`. **Development and test only**;
    Keycloak disables this grant by default and the cli prints that it is not for production.
  Refresh tokens are kept in memory for the life of the process and never written to disk. Every
  grant is exercised in tests against the in-repo fake issuer (which implements the discovery
  document, JWKS, the device endpoints and the token endpoint), never against a live Keycloak.
- **Users and ctl**: `users/ctl` accepts `add <name>\n` and `remove <name>\n` (one command per
  write, `EINVAL` otherwise) and lists users on read — **only** for identities whose token carries
  the realm role `todofs-admin`; everyone else gets `EACCES`. A user who authenticates but has no
  row is auto-created (Decision Log). `remove` deletes the user's lists (transaction).
- **Lists and items**: `mkdir /users/<u>/<n>` (`n` must be the next integer) creates a list; the
  `name` file is empty until written. `mkdir …/<n>/<m>` creates an item with `label`,
  `description` empty and `status = open`. Writing `status` accepts `open` or `done` (trailing
  newline tolerated), anything else `EINVAL`. `rmdir` on an item or an **empty** list removes it
  (items are virtual, so an item dir may be removed while its three files "exist"). Writes are
  `O_TRUNC`-style replace; partial writes at offsets are honoured within a 64 KiB per-field cap.
- **Storage**: SQLite, WAL mode, foreign keys on, parameterised statements only, one writer
  connection with a mutex + a read pool: `users(id, name UNIQUE, created_at)`, `lists(id,
  user_id→users, idx, name, UNIQUE(user_id, idx))`, `items(id, list_id→lists, idx, label,
  description, status CHECK(status IN ('open','done')), UNIQUE(list_id, idx))`, `meta(schema_version)`.
  Qid paths are derived from `(table, row id)`; versions from an `updated_at`/counter column.
- **Isolation** is enforced at the handler boundary: a user's root handler cannot reach another
  user's rows (queries are always scoped by the attached user id), and it is mutation-tested in
  every repo (delete the scope and watch the named test die).
- `todofs --listen … --db <path.sqlite> --oidc-issuer <url> --oidc-audience <aud>
  [--admin-role todofs-admin] [--jwks-cache <secs>] [--tls…]`. Tests use a local fake issuer
  (discovery document, JWKS, device and token endpoints, and a token signer in the test suite),
  never a live Keycloak; a `docker-compose.yml` with Keycloak is provided for manual runs only.

### cli (conformance tool, not published)

`ninep [--addr url] [--dialect …] [--uname u] [--aname a]
[--auth none|token:<secret>|bearer:<token>|oidc-device|oidc-password] [--auth-optional]
[--oidc-issuer <url> --oidc-client-id <id>] <cmd>` with
`version`, `ls [-l] path`, `cat path`, `stat path`, `write path` (stdin), `mkdir path`,
`rm path`, `mv old new`, `readlink path`. The three OIDC forms are described under todofs above;
they live in the cli, not in the published packages. Output formats are fixed by
[fixtures/conformance.md](fixtures/conformance.md) so any two languages can be cross-checked
with `diff`.

## 8. Security posture (all repos)

1. Every rule of protocol-reference §8 is implemented and **mutation-tested**: delete the check,
   a named test fails.
2. Fuzzing of the decoder is part of the repo (the ecosystem's fuzzer: `sharpfuzz`/property
   tests, `fast-check`, `go test -fuzz`, `hypothesis`/`atheris`, libFuzzer, `jqwik`, …) with the
   corpus seeded from `wire-vectors.json`; the CI job runs a short fuzz budget.
3. No secret in logs, errors, or `Rerror` text; tokens compared in constant time; credential
   material zeroed where the language allows.
4. TLS defaults are safe (verify on; 1.2+; no renegotiation); mTLS optional; WebSocket origin
   allow-list.
5. Resource caps (§4 limits) exist and are tested with a hostile client (fid flood, tag flood,
   size lie, slowloris header, oversize count).
6. Dependencies: pinned exact versions; a lockfile where the ecosystem has one; every runtime
   dependency justified by a Decision Log row in the repo's `ARCHITECTURE.md`; a vulnerability
   audit job in CI.
7. SQL only through parameters; the todofs store is the only module that contains SQL.

## 9. Performance and memory

- Zero-copy framing where the language permits; pooled buffers sized to msize; no per-message
  allocation of payload copies in the hot path; `Rread` written straight from the handler's
  buffer into the frame; `Twrite` payload handed to the handler as a view.
- Client window of in-flight reads/writes (default 4) so a single stream saturates loopback.
- Every repo ships `docs/benchmarks.md` with **measured** numbers for: (a) sequential read and
  write of 1 GiB over loopback TCP at msize 1 MiB (`.L`) and 64 KiB (9P2000); (b) 100 000
  `walk+stat+clunk` round trips; (c) peak RSS of a server under (a); (d) codec decode/encode
  ns/op for `Twalk` and `Rgetattr`. Numbers are reported with hardware, version, and command
  line; no targets are promised — a regression > 20 % against the previous PR is a review finding.

## 10. Exit criteria per repository ("production ready")

1. Three installable packages build from a clean checkout with the ecosystem's standard command
   (`dotnet pack`, `npm pack`, `go build ./...` + module path, `python -m build`, …) — the
   ticket's "two installable packages: client and server" plus the shared one.
2. Every message type of protocol-reference §2 encodes/decodes; all 88 golden vectors round-trip
   byte-exactly; the mutation matrix of protocol-reference §9 yields typed errors.
3. Server and client pass the **conformance scenario** ([fixtures/conformance.md](fixtures/conformance.md))
   in all three dialects over TCP, TLS, and WebSocket, and over the in-memory transport.
4. **Interop**: the repo's client passes the scenario against **every previously merged
   language's** jsonfs server, and its jsonfs passes against every previously merged language's
   cli (loop stage `interop`); plus against the external reference peers available on the
   machine: hugelgupf/p9 `p9ufs` (`.L`, server) and plan9port `9p` (9P2000, client). Missing
   peers are recorded as "not run: <reason>", never faked.
5. Default auth and a custom `Authenticator` are exercised end to end; the Keycloak path is
   tested against the in-repo fake issuer with expired / wrong-audience / `alg=none` / wrong-key
   tokens rejected.
6. jsonfs and todofs run, with README walk-throughs that a reader can paste.
7. Security §8 items 1–7 are green; code review ≥ 10/12 and security review ≥ 8/10 per
   `qode.yaml`.
8. Docs: `README.md` (install, 60-second client, 60-second server), `docs/protocol.md` (vendored
   reference), `docs/server.md` (handler table: T-message → handler method), `docs/client.md`,
   `docs/transports.md`, `docs/auth.md`, `docs/examples.md`, `docs/benchmarks.md`,
   `docs/security.md`, `CHANGELOG.md`, API docs generated by the ecosystem tool.
9. CI on Linux and macOS: build, tests, lint at zero warnings, fuzz smoke, package step, audit.
10. **Every `required` obligation of [fixtures/test-index.json](fixtures/test-index.json) has a test
   in this port, and every test in this port is either mapped to an obligation or recorded as the
   port's own** (§13). The repo carries a `docs/test-map.json` and a case that fails when the two
   drift; a `recommended` obligation a port skips records why. Interop shows two ports agree with
   each other — this is what shows a port agrees with the workspace. A port that passes 4 but has
   not discharged the index is not done.

## 11. Conventions across languages

- Package/prefix name **`ninep`** (`NineP` in Pascal-case ecosystems) — `ninep-protocol`,
  `ninep-client`, `ninep-server` or the ecosystem's equivalent (table in each ticket).
- Public API names follow the protocol: `Tversion`/`Rversion` message types keep their 9P
  names; handler methods use the verbs above; constants keep their `fcall.h`/`9p.h` names.
- Initial version `0.1.0`; semver; `CHANGELOG.md` kept from the first release.
- License: **MIT** (confirmed by the owner 2026-09-08; see Decision Log) — `LICENSE` in every repo.
- Repository layout: `protocol/ client/ server/ examples/{jsonfs,todofs,cli} tests/ docs/
  ARCHITECTURE.md CLAUDE.md README.md` adapted to the ecosystem (e.g. `src/NineP.Protocol/`).
- Zero-warning policy: warnings are build failures; suppressions carry a reason.
- Commit granularity: one spec task per commit; tests alongside.

## 12. Reference API (owner decision, 2026-09-08)

The C# public surface — `9p-csharp/docs/api.md`, 155 public types: 133 protocol, 5 client, 17
server — is the **reference API**. Every later language ticket mirrors it idiomatically: the same
type names, the same method names and parameter order, the same defaults, translated into the
ecosystem's casing, error and async conventions. Where this section and `api.md` disagree, `api.md`
is wrong and gets fixed. The rules below are what the owner settled when the C# surface was
reviewed; the shapes are the C# ones with the language-specific spelling stripped.

### Rules

1. **Verbs follow the protocol, on both sides.** `getattr` / `setattr` (never `stat` / `wstat`
   in an API name), `readdir` (never `list`, `listdir`), `readlink` (never `target`), `walk`,
   `open`, `create`, `read`, `write`, `remove`, `rename`, `fsync`, `lock` / `getlock`,
   `listxattr` / `getxattr` / `setxattr` / `removexattr`, `link`, `statfs`, `clunk` (server-side
   release) and `dispose`/`close` (client-side release, which clunks). The low-level client API
   keeps the exact message names (`Tgetattr` → `getattr`, `Twstat` → `wstat`), because there it
   *is* the wire.
2. **Optional numeric ids use the wire sentinel, never a nullable.** `uid`, `gid`, `muid`,
   `n_uname` are unsigned 32-bit values and `NONUNAME` (`0xFFFFFFFF`) means "absent" —
   `Identity.uid`, `StatRecord.n_uid/n_gid/n_muid`, `Tauth.n_uname`, `Tattach.n_uname` alike.
   **errno is a signed 32-bit value everywhere** — `NinePError.errno`, `Rerror.errno`,
   `Rlerror.ecode` — and `0` means "none" (a 9P2000 `Rerror` carries no errno). The one place a
   nullable is right is `SetAttr`, where "absent" means *do not touch this field* in a partial
   update; that is a different concept from an absent id and stays nullable.
3. **Decoding is generic by expected type** — `decode<T>(frame, dialect)`, `try_decode<T>`,
   with `peek_size`, `peek_type`, `peek_tag` for the header. A language without generics or
   static type members chooses its own equivalent (a per-type function, or a `peek_type`-driven
   union); the reference API does not prescribe a tagged-union decode.
4. **Message records are positional**, in wire field order, `tag` first — `Rgetattr` has 17
   fields and that is the shape. A language whose records are hostile to that uses its natural
   struct/record form with the same field names and order.
5. **Payloads alias the frame.** `Rread.data` and `Twrite.data` are views into the decoded frame's
   buffer and are valid only until that frame is released; a caller that keeps them copies. This
   is stated on the public members, not only on the internal lease type.
6. **Connections are byte streams.** A transport connection exposes `read(buf)`, `write(message)`
   (called once per complete message, so a message-oriented transport may frame each one),
   `close(reason)`, `remote_address`, `peer_identity`. There is **no** "preserves message
   boundaries" flag: nothing consumed it, and the framing layer re-frames every transport alike.
7. **Options are records with defaults**, init-only where the language has it. Additions the
   C# build made beyond the spec and the owner kept: `Identity.is_authenticated` (false only for
   the anonymous identity a `NOFID` attach gets), `TcpTransportOptions.logger` (so all three
   transport option types are symmetric), `ClientOptions.dispose_timeout` (5 s, the total bound on
   the clunks a session disposal issues). Dropped: `ClientOptions.time_provider` (a clock the
   client never read; the server keeps its own). `ClientOptions.dialects` stays — it is a
   preference list the client walks on `"unknown"`, tested since code review round 1 — and is
   what a cli's `--dialect` flag sets. Kept by owner decision of 2026-09-10; the question is
   closed.
8. **A permission is a permission, and a create says what it creates.** Wherever a shape carries
   permission bits — `Attr.perm`, `SetAttr.perm`, `CreateRequest.perm` — the value is the `07777`
   mask of §4.7 and **nothing else**: the file type and the `DM*` mode bits travel in `kind` and
   `file_flags`, never folded into the same integer. A language whose literals cannot spell octal
   gives them a named flag type (C#: `FilePermissions`); one that can may use its octal literal.
   `Fid.create` therefore takes `kind` and `file_flags` beside `perm`, exactly as
   `CreateRequest` does, so a client and a server describe a create the same way. A create of a
   kind whose payload needs a field the message does not carry — a symlink's target, a device's
   numbers — is refused, not sent with the field empty (§8 rule 15).

### Shapes

```text
protocol
  Dialect { P9_2000, P9_2000_u, P9_2000_L }          MessageType (66 legal numbers)
  Qid { type: QidType, version: u32, path: u64 }      FileKind, FileFlags, OpenMode, OpenFlags
  Attr, SetAttr (nullable = don't touch), DirEntry, StatFs, TimeSpec, DeviceId
  LockType, LockFlags, LockStatus, LockRequest, LockQueryResult, XattrFlags
  NinePError { ename: string, errno: i32 }             NinePException(error) · NinePProtocolException(kind) · NinePVersionException
  ProtocolErrorKind, Errno (constants), ErrorTable (ename <-> errno), Constants (NOFID, NOTAG, NONUNAME, IOHDRSZ …), Limits
  messages: one record per T/R message, positional, wire order; IMessage { type }; StatRecord; GetAttrMask, SetAttrMask
  codec:   MessageCodec.decode<T>(frame, dialect) · try_decode<T> · encode<T>(writer, msg, dialect) -> n · encoded_size<T> · peek_size/type/tag
  negotiation: Negotiator.negotiate(request: Tversion, configured: set<Dialect>, limits) -> NegotiationResult
  transports: NinePScheme {tcp, tls, ws, wss, memory} · NinePAddress · CloseReason · PeerIdentity
              Transport { schemes · connect(address) -> Connection · listen(address) -> Listener }
              Connection { remote_address · peer_identity? · read(buf) -> n · write(message) · close(reason) · dispose }
              Listener   { local_address · accept() -> Connection? · dispose }
              TcpTransport/Options · TlsTransport/Options · WebSocketTransport/Options · MemoryTransport
  auth:    Identity { user, uid (NONUNAME = absent), groups, claims, is_authenticated } · Identity.anonymous(user, uid)
           AuthRequest { uname, n_uname, aname }
           Authenticator { is_required · begin(request, peer?) -> AuthSession? }
           AuthSession   { identity? · write(bytes) · read(max) -> bytes · dispose }
           Credential    { authenticate(channel) }   AuthChannel { write(bytes) · read(max) -> bytes }
           TokenAuthenticator · PasswordAuthenticator(PasswordStore; PasswordFileStore) · TlsClientCertAuthenticator
           TokenCredential · PasswordCredential · BearerTokenCredential · CallbackCredential · ConstantTime.equals

client
  NinePClient.connect(address | transport+address | connection, options) -> Session
  ClientOptions { dialects (preference list), min_dialect, msize?, credential?, uname, n_uname, aname,
                  in_flight_window=4, request_timeout=60s, dispose_timeout=5s, connect_timeout=30s, limits, logger }
  Session { dialect · msize · max_payload · root · messages
            attach() -> Fid · attach(uname, aname, credential?) -> Fid
            walk(path) -> Fid · open_file(path, mode, flags) -> Fid · read_file(path) -> bytes · write_file(path, bytes)
            readdir(path) -> [DirEntry] · mkdir(path, perm=0755) · create_file(path, perm=0644) -> Fid
            remove(path) · rename(old, new) · getattr(path) -> Attr · setattr(path, SetAttr)
            symlink(path, target) · readlink(path) -> string · statfs(path) -> StatFs · dispose }
  Messages { one method per legal T-message (32): version, auth, attach, flush, walk, open, create, read, write,
             clunk, remove, stat, wstat, statfs, lopen, lcreate, symlink, mknod, rename, readlink, getattr, setattr,
             xattrwalk, xattrcreate, readdir, fsync, lock, getlock, link, mkdir, renameat, unlinkat }
  Fid { fid · qid · is_open · iounit
        walk(names) -> Fid · clone() -> Fid · open(mode, flags)
        create(name, kind, perm, mode, flags, file_flags)   // kind is file|directory; see rule 8
        read(offset, buf) -> n · write(offset, data) -> n · read_all() -> bytes · write_all(bytes)
        readdir() -> stream<DirEntry> · getattr() -> Attr · setattr(SetAttr) · remove() · fsync(data_only)
        lock(LockRequest) -> LockStatus · getlock(LockRequest) -> LockQueryResult
        getxattr(name) -> bytes · setxattr(name, value, flags) · dispose (clunks) }

server
  NinePServer(options) { start · stop(deadline) · counters · dispose }
  ServerOptions { listen: [address], transports, dialects: set, authenticator?, limits, logger, time_provider, request_log? }
  ServerCounters · RequestLogEntry · RequestLogSink { log(entry) }
  Filesystem       { attach(identity, aname) -> DirectoryHandler }
  Handler          { qid · getattr() -> Attr · setattr(SetAttr) · clunk(was_open) · fsync(data_only) }
  DirectoryHandler { lookup(name) -> Handler? · readdir(cursor, max) -> DirectoryListing{entries, next_cursor, end}
                     create(CreateRequest{name, kind, perm, mode, flags, file_flags, target?, rdev?, gid, identity}) -> Handler
                     remove(name, kind) · rename(old_name, new_parent, new_name) }
  FileHandler      { open(mode, flags) -> OpenFile }
  OpenFile         { read(offset, buf) -> n · write(offset, data) -> n · size() -> u64 · dispose }
  SymlinkHandler   { readlink() -> string }
  optional:  LockCapability { lock · getlock } · XattrHandler { listxattr · getxattr · setxattr · removexattr }
             LinkCapability { link(name, target) } · StatFsCapability { statfs() }
```

The T-message → handler-method table in each repo's `docs/server.md` is derived from this and is
checked by a test against the dispatcher's routing; the 66 message records and the 32 low-level
client methods are counted by tests too. A port that cannot express one of these shapes records
the deviation in its own `docs/api.md` and in its ticket's refine analysis, and the reviewer judges
it against this section.

## 13. The shared test suite (owner decision, 2026-09-11)

Interop proves two ports agree with each other. It does not prove either agrees with the
specification — two ports can be wrong in the same way and pass. What closes that gap is a **shared
body of test cases**, ported alongside the code, so every port is judged against the same
behaviours rather than only against its neighbour.

### Order of authority

When a port is written, or when a question arises about what it should do, these are consulted in
order:

1. **The official specification** — the 9P2000 / `.u` / `.L` sources, and
   [protocol-reference.md](protocol-reference.md) as this workspace reads them. A disagreement
   between a source and anything below is resolved in the source's favour, and the reference is
   corrected.
2. **The shared test index** — [fixtures/test-index.json](fixtures/test-index.json). Behaviour the
   specification leaves open but this workspace has settled is settled *here*, as a test every port
   owes, not as prose someone may miss.
3. **The C# port's implementation** — the reference API of §12 and the code behind it.
4. **The C# port's documentation** — `docs/api.md`, `docs/server.md`, `docs/client.md` and the rest.

The C# port is first in the sequence and therefore the seed for 2, 3 and 4. That is a statement
about where the material *came from*, not about its standing: once an obligation is in the index it
belongs to the workspace, and a later port that shows it wrong corrects the index rather than
working around it.

### How the index works

- **An id names a behaviour, not a method.** `create/server-owned-flag-refused`, not
  `ACreateAskingForAServerOwnedFlagIsRefused`. Each port maps ids onto its own test names in its
  repo-local `docs/test-map.json`, in whatever casing its ecosystem uses.
- **Two tiers bind.** `required` — the port is not done without it. `recommended` — the port should
  have it, and a port that skips one records the reason; these are the obligations needing a
  facility not every ecosystem has (property testing, an external peer, an OIDC issuer, a scale
  budget).
- **Everything else is declared local.** A test that is genuinely about one language's plumbing —
  a buffer pool, a target framework, a GC measurement — is listed as the port's own and owes
  nothing to the others. Declaring it is deliberate: a test is never *silently* local.
- **Both directions are checked.** Every obligation must be discharged or recorded; every test
  must be an obligation or declared local. A test added to a port fails its own build until
  someone decides which it is, which is what stops the index quietly falling behind the suites.
- **A fix anywhere is an obligation everywhere.** A regression test written in any port for a
  behaviour that is not that language's own is added to the index in the same change, and every
  other port then owes it. This is the [backports.md](backports.md) mechanism widened from rules
  to tests: a rule still becomes a numbered §8 rule, but a test needs no rule to propagate.

The index is seeded from the C# suite and grows from every port after it. It is **not** a promise
that the C# suite is right — only that what it proves is written down where the next thirteen
ports can be held to it.

## Decision Log

| Date | Decision | Notes |
| --- | --- | --- |
| 2026-09-08 | Approve E1–E19/F1–F30 edge coverage, `max_read_all` default 256 MiB, configured UTF-8 name limits, stable listing cookies, JSON growth rollback and split-rune writes | Owner approved the additional-tests plan. Tiny quotas and generated streams run in CI; all three full-scale workloads are explicitly local. See `fixtures/conformance.md` for the portable contract. |
| 2026-09-05 | Umbrella "9P2000.uL" = implement `9P2000`, `9P2000.u`, `9P2000.L` as separately negotiated dialects; the umbrella string never goes on the wire | Q1 of the June analysis. Linux, diod and Plan 9 all negotiate exact strings; a custom string would interoperate with nobody. **Confirmed** by the owner 2026-09-08: the set stays all three. |
| 2026-09-05 | Three packages per language: protocol (shared, incl. transports), client, server; examples unpublished | Ticket allows a shared package; transports are needed by both sides. |
| 2026-09-05 | Handler model = per-file-type interfaces (`Directory`, `File`, `Symlink`) with optional capability interfaces; the core owns all protocol state | The ticket's "per file-type implementation of all necessary request handlers". |
| 2026-09-05 | "Default authentication" = afid token/password exchange (`TokenAuthenticator`, `PasswordAuthenticator`); Plan 9 `p9any`/`p9sk1` out of scope | p9sk1 is DES-based; not production security. Documented in READMEs. |
| 2026-09-05 | Keycloak: bearer JWT written to the afid; validation via JWKS; `todofs-admin` realm role gates `users/ctl` | Q3/Q5. **Confirmed** by the owner 2026-09-08. |
| 2026-09-05 | jsonfs mapping per §7; read-only by default; `--writable`/`--write-back` opt-in; files hold exact value text, no trailing newline; object keys percent-encoded with `%` escaped **first** and `""`/`.`/`..` given reserved encodings | Q4. **Confirmed** by the owner 2026-09-08. Amended 2026-09-05 (code review round 1, F-26): escaping `%` last was not injective and the empty key had no encoding. |
| 2026-09-05 | todofs semantics per §7; SQLite schema per §7; auto-create authenticated users | Q5. **Confirmed** by the owner 2026-09-08. |
| 2026-09-05 | `.L` locking, xattr, statfs, fsync, link, mknod all in scope; synthetic servers answer `EOPNOTSUPP` for what they cannot do | Q6: "production ready" means the full `.L` set. |
| 2026-09-05 | Reference peers: hugelgupf/p9 `p9ufs` (`.L`) and plan9port `9p` (9P2000); cross-language interop matrix is mandatory; diod/v9fs only via Docker when available | Q7. Both peers run on macOS; diod and v9fs are Linux-only. |
| 2026-09-05 | WebSocket carries one 9P message per binary WS message; subprotocol `9p` optional | Simplest interop across 14 WS stacks; fragmentation stays inside WS. |
| 2026-09-05 | Limits: max msize 1 MiB, min 4096, **pre-negotiation frame cap 8192**, fids 65536, in-flight 256/conn **and 4096/listener** (8/conn reserved for `Tflush`), conns 1024/listener | Protocol-reference §8 rules 1 and 8; all configurable. Amended 2026-09-05 (code review round 1, F-20/F-21): the 1 MiB maximum must not bound a pre-`Tversion` frame, and 256/conn without a listener-wide cap admits 262 144 concurrent handler calls. |
| 2026-09-05 | Toolchain floors: .NET 8 (SDK 10), Node 22, Go 1.24, Python 3.12, C++20, Java 21, Dart 3.5, Swift 6, GHC 9.8, C11, Rust 1.85 (edition 2024), Lua 5.4, Perl 5.36, PHP 8.3 | Per-ticket detail. Missing toolchains are a loop gate (loop.md). |
| 2026-09-05 | License MIT | **Confirmed** by the owner 2026-09-08. |
| 2026-09-05 | Model roles inherited from the plumber loop: builder Opus, reviewer Fable, drafter Sonnet | Owner decision of 2026-08-28 in plumber; reused unchanged. |
| 2026-09-05 | `Tfsync`: encoders always emit `datasync[4]` (15-byte frame, diod `protocol.md:343`); decoders also accept the 11-byte frame hugelgupf/p9 sends (`messages.go:1678–1692`), defaulting `datasync` to 0 | Code review round 1, F-1. The sources genuinely disagree; sending the longer form keeps diod and v9fs happy, accepting the shorter one keeps p9ufs interop working. Both are golden vectors. |
| 2026-09-05 | `.L` qid type = `S_IFDIR → QTDIR`, `S_IFLNK → QTSYMLINK`, everything else `QTFILE`; `.u` qid bits follow **Linux** (`QTSYMLINK 0x02`, `QTLINK 0x01`), not the `.u` draft (`QTLINK 0x02`, no `QTSYMLINK`) | Code review round 1, F-2/F-22. hugelgupf/p9 maps sockets, FIFOs and character devices to `TypeAppendOnly` ("best approximation", `p9.go:165–177`); we do not, and clients must read the file type from `Rgetattr.mode`. |
| 2026-09-05 | Version negotiation is conditioned on the **configured dialect set**: a dialect outside it is never answered with, so a server configured without `9P2000` answers `"unknown"` rather than downgrading. Before a dialect is agreed, the only accepted message is `Tversion`; anything else draws a 9P2000-shaped `Rerror "version not negotiated"` and the connection is closed | Code review round 1, F-4/F-5. `--dialects` was previously contradicted by step 4 of the algorithm, and §5.1 and §8 rule 9 disagreed on stay-open vs close. |
| 2026-09-05 | Refusing `Tauth`: 9P2000/.u `Rerror "authentication not required"`; `.L` `Rlerror ECONNREFUSED (111)`. Fid-cap overflow: `Rerror "too many fids"` / `Rlerror ENFILE (23)` — one condition, one errno | Code review round 1, F-6/F-7. diod prescribes `Rlerror` for the auth refusal without an errno (`protocol.md:117`); lib9p's ename carries an `argv0` prefix (`srv.c:197`) that we drop. |
| 2026-09-05 | An afid is bound to the triple `(uname, n_uname, aname)`; an attach presenting it must match, with empty `uname` / `NONUNAME` `n_uname` meaning "unspecified". The session identity is the authenticator's output, never the claimed `uname`/`n_uname` | Code review round 1, F-8. attach(5):112–119 binds `uname`+`aname` only, which in `.u`/`.L` lets an afid for `n_uname=1000` attach as `n_uname=1001`. |
| 2026-09-05 | Keycloak **login** lives in the cli, not the packages: `--auth bearer:<token>`, `--auth oidc-device` (RFC 8628 device authorization grant), `--auth oidc-password` (resource-owner grant, dev/test only). Tests drive all three against the in-repo fake issuer | Code review round 1, F-11. The design validated tokens but never said how a user obtains one; the master ticket's "authenticated via keycloak login" needs the device grant. |
| 2026-09-05 | `OAPPEND (0x80)` is a **flag**, not an access mode: the access mode stays the low two bits and append semantics apply to writes on the fid | Code review round 1, F-17. `linux-9p.h:223,248` — "open the file and seek to the end"; treating it as `OWRITE` would turn `OREAD` + `OAPPEND` into a write open. |
| 2026-09-08 | **Reference API** = the C# public surface (`9p-csharp/docs/api.md`), with the rules of §12: protocol verbs on both sides (`getattr`/`setattr`/`readdir`/`readlink`); optional ids as `NONUNAME` sentinels, errno a signed 32-bit `0 = none`, `SetAttr` alone nullable; generic-by-type decode, positional message records; payload aliasing stated on `Rread.data`/`Twrite.data`; no `PreservesMessageBoundaries` flag | Owner review of ticket 001 (handoff record kept in the workspace). Every later ticket mirrors §12 idiomatically. |
| 2026-09-08 | Surface kept beyond the spec: `Identity.IsAuthenticated`, `TcpTransportOptions.Logger`, `ClientOptions.DisposeTimeout`. Dropped: `ClientOptions.TimeProvider` (never read). `ClientOptions.Dialects` kept as a walked preference list | Owner review of ticket 001. The owner's answer was to drop `Dialects` too; it was kept because the cli's `--dialect` needs it and the "not honoured" finding (N-2) had already been fixed in code review round 1 — recorded here so the owner can overrule. |
| 2026-09-08 | Provisional rows confirmed: dialect set, default authentication, Keycloak over the afid only, jsonfs and todofs semantics, MIT, one 9P message per WebSocket message | Owner review of ticket 001. |
| 2026-09-08 | Residuals fixed before ticket 001 merges: S-1 oversize-read test on a file larger than msize, S-2 `AllowRenegotiation = false` asserted, the `Tflush`-answered-with-an-error `oldTag` strand, jsonfs directory fsync after the write-back rename, the flushed-`Tclunk` fid-pool rule in `docs/client.md`. Riding, by design: `--allow-insecure-issuer`, permissive default WebSocket Origin, the v9fs `.u` wstat check owed on Linux | Owner review of ticket 001. |
| 2026-09-08 | Remote gate: no remotes are created by the loop. The owner says when to create which remote; until then a ticket stops at pr-create and reports | Owner review of ticket 001. |
| 2026-09-08 | Toolchains: the loop installs a missing language toolchain, and the interop peers `p9ufs` and plan9port, through Homebrew when the ticket that needs them starts (permission prompt expected); nothing is installed ahead of its ticket | Owner review of ticket 001. |
| 2026-09-08 | **Projection honesty**: protocol-reference §8 rules 15–27. A request naming something the dialect, the wire or the handler model cannot carry is refused, never sent or answered with the part dropped. `SetAttr` and `CreateRequest` gain **no** file-flags member yet: a `DMAPPEND` / `DMEXCL` / `DMTMP` change is refused with `EPERM` (rule 19); adding `flags` to both §12 shapes is an open follow-up | Owner decision after the audit of the C# port (26 findings across protocol, client, server and examples; `9p-csharp` improvement request IR-9). Each rule is a named test in every port. |
| 2026-09-08 | **Backports**: an issue found in one port that is transferable to previously implemented ports becomes a numbered rule (protocol-reference §8 or this file), gets a named test *and* a fix in every earlier port before the loop advances, and is tracked in `backports.md` | Owner decision. A rule that only one port tests is not a rule; the sequence is serial, so the earlier ports are the ones that can drift. Mechanics in `loop.md` §Backports. |
| 2026-09-08 | **Review findings C01–C15 and H01 of 2026-09-08** — reference rules 28–36 cover lifetime leases, honest xattr commit, append ordering, persistent ancestry, permission policies, bounded listener progress and TLS purpose checks; rule 8 now refuses excess ordinary work with EAGAIN while processing Tflush | Owner authorized all Critical/High fixes. No changes to dialect-neutral handler signatures. Documentation and named regression tests define the behavior future ports must follow. |
| 2026-09-08 | **Conformance Parts E and F** (`fixtures/conformance.md`): Part E freezes the cli answers to missing paths, empty directories, paths through files, slashes, name edge cases (`sample.json` gains a `names` directory: dot-file, accented, 255-byte, U+FF21 and astral names; the expected-output generator now sorts bytewise) and create/remove/rename mistakes; Part F freezes wire-level cases (unopened fids, `count=0`, EOF, root fid, EEXIST on every verb, name limits, listings under mutation, stale fids) and three scale cases (4 GiB file, 10⁶-entry directory, 10⁶ creates). The proposed Part F behaviors were subsequently approved and are recorded in the conformance fixture | Owner question of 2026-09-08 about large reads, huge listings and mass creates; requirements in [fixtures/conformance.md](fixtures/conformance.md). |
| 2026-09-10 | **Enames match the Linux kernel's 9P table** (protocol-reference §8 rule 39): over 9P2000 and `.u` every ename sent for an errno is one Linux's `net/9p/error.c` maps to that errno, per [fixtures/linux-9p-errors.json](fixtures/linux-9p-errors.json); every string in that table and every former wording is understood on receipt | Owner decision after the interop runs of 2026-09-10 ("we should be implementing all of the error strings, not just a handful, and they should match"): a `version=9p2000` v9fs mount read most of the C# port's enames as error 526. Landed in `9p-csharp` the same day; every later port ships the table from the start. |
| 2026-09-10 | **File flags reach the handler**: §12's `SetAttr` gains a nullable `flags` and `CreateRequest` a `file_flags`; protocol-reference §8 rule 19 rewritten — `DMAPPEND` / `DMEXCL` / `DMTMP` on `Tcreate.perm` and a `Twstat` that changes them are honoured, read back, and refused with `EOPNOTSUPP` when a handler did not apply them; `DMAUTH` / `DMMOUNT` refused with `EPERM`; a client completes a half-stated `Twstat` mode word from a `Tstat` and refuses the flags in `.L` | Owner decision of 2026-09-10, conditional on the standard requiring it: open(2) makes a create's `DMEXCL` and `DMAPPEND` the file's flags and stat(5) says "the directory bit cannot be changed by a wstat; the other defined permission and mode bits can", so the refusal of 2026-09-08 was honest but not compliant. Closes the follow-up left open in the projection-honesty row. Landed in `9p-csharp` (rule-index rows 91, 92, 141–154); `backports.md` B-4. |
| 2026-09-10 | Protocol-reference §8 rules 37 and 38: the `Trename` fallback when `Trenameat` is `EOPNOTSUPP`, and an error on `NOTAG` answering a `Tversion` is a version error | Owner decision. Found against diod by the interop runs of 2026-09-10 and pinned by the C# port's named tests; promoted so the next port does not rediscover them (`backports.md` B-3). |
| 2026-09-10 | `ClientOptions.dialects` stays in §12; the question left open on 2026-09-08 is closed | Owner decision. The cli's `--dialect` and a consumer talking to a `.L`-only server such as diod need to offer exactly one dialect; the "not honoured" finding that motivated removal is fixed. |
| 2026-09-10 | Protocol-reference §8 rule 41: releasing a client session never fails because the connection is dead. A courtesy clunk that cannot be delivered is swallowed whether it arrives as the session's own protocol error or as the transport's raw I/O failure | Owner decision. Found by the C# port's v0.2.0 release gate: `NinePSession.DisposeAsync` caught the protocol shape but not the transport shape, so disposal threw or stayed quiet depending on whether the write or the reader met the dead socket first. The 9P sources say nothing about client release semantics, so this is a workspace decision; promoted so the next port does not rediscover it (`backports.md` B-7) |


### 2026-09-08 — approved edge-test behavior and resource limits

The owner approved E1–E19 and F1–F30 in `fixtures/conformance.md`, including the proposed fixes.
ClientOptions includes `max_read_all` (C#: `MaxReadAll`), default 256 MiB, checked against
metadata and the actual accumulated bytes. Zero allows empty values only. `Session.readdir(path)`
materializes all entries; `Fid.readdir()` streams pages. Configured name limits count UTF-8 bytes
and are enforced on both sides; malformed wire names retain protocol-error semantics.

Partial walks ending at files report ENOTDIR; same-name rename succeeds as a no-op; zero-count
directory reads return zero without changing position (.L still requires Treaddir).
Writable jsonfs must remain strictly below the serialized document cap and within reloadable
depth after every mutation; rejected growth returns ENOSPC and restores existing objects in place.
An incomplete final UTF-8 rune may be staged per open (at most three bytes) until the next
contiguous write completes it; unfinished staging at clunk is EINVAL. Invalid UTF-8 writes do
not change the committed value. Write-back failures report error and keep the in-memory change;
the complete old or new disk document depends on whether replacement already occurred.

All three full-scale workloads (generated file, million-entry directory and million real creates)
are documented local opt-ins and explicit CI skips, even when opt-in variables are set in CI.
They retain isolated processes and fixed budgets. Bounded versions run normally in CI. The exact matrix, corrected offset oracle, and resource accounting are
in `fixtures/conformance.md`.
| 2026-09-11 | **The shared test suite (§13)**: [fixtures/test-index.json](fixtures/test-index.json) lists every test obligation a port owes, by behaviour id rather than method name, in two binding tiers (`required`, `recommended`) plus an explicit local declaration for a port's own plumbing. Each port carries a `docs/test-map.json` and a case that fails in **both** directions — an obligation nobody discharged, and a test nobody classified. Order of authority for a new port: specification, then the index, then the C# implementation, then the C# docs | Owner decision of 2026-09-11. Interop proves two ports agree with each other, not that either agrees with the standard; two ports can be wrong the same way and pass. Seeded from the C# reference suite at 785 obligations (744 required, 41 recommended) over 43 areas, with 23 tests declared C#-local. The seeding is a statement about provenance, not standing: a later port that shows an obligation wrong corrects the index. |
| 2026-09-11 | **Backports widen from rules to tests**: a regression test written in any port for a behaviour that is not that language's own is added to `test-index.json` in the same change, and every other port then owes it. A transferable *rule* still becomes a numbered §8 rule and a `backports.md` row; a transferable *test* needs no rule to propagate | Owner decision of 2026-09-11, following §13. The rule ledger only ever propagated what someone thought to write as a rule, which was 105 of the C# suite's 785 transferable behaviours. Catching a bug in one port should put a test in the others whether or not the fix reworded the reference. |
| 2026-09-11 | **§12 rule 8 — a permission is a permission, and a create says what it creates**: permission-bearing fields carry the `07777` mask and nothing else, with the file type in `kind` and the mode bits in `file_flags`; `Fid.create` gains `kind` and `file_flags` to match `CreateRequest`; a create of a kind whose payload needs a field the message does not carry is refused, not sent with the field empty | Owner decision of 2026-09-11, from the C# port: `Tcreate.perm` folds three vocabularies into one integer, and a client API that passes it through makes every caller do the bit arithmetic — `mkdir` was written as `perm \| 0x80000000u`. Languages without octal literals give permissions a named flag type (C#: `FilePermissions`); the rest may keep an octal literal. Landed in `9p-csharp` 0.3.0. |
