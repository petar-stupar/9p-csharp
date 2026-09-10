# The example servers

Two example servers and one client ship with this repository. None of them is a published package:
they exist to show what the handler model looks like in practice and to give the conformance
scenario something to run against.

| | Assembly | What it is |
| --- | --- | --- |
| [`jsonfs`](../examples/NineP.JsonFs) | `jsonfs` | one JSON document served as a 9P tree |
| [`todofs`](../examples/NineP.TodoFs) | `todofs` | a SQLite-backed to-do tree, per-user, OIDC-authenticated |
| [`ninep`](../examples/NineP.Cli) | `ninep` | the conformance client, with output formats frozen by the workspace fixture |

## Running them from a clone

The examples target `net10.0` and are not packed, so they are run out of the build tree:

```text
dotnet build
dotnet run --project examples/NineP.JsonFs -- --listen tcp://127.0.0.1:5640 --file docs/9p/fixtures/sample.json
dotnet run --project examples/NineP.Cli -- --addr tcp://127.0.0.1:5640 ls -l /
dotnet run --project examples/NineP.Cli -- --addr tcp://127.0.0.1:5640 cat /name
```

A `--listen` of port **0** makes the kernel choose one, and the server prints `listening <address>`
on standard output before it accepts anything — which is exactly how the conformance driver finds
it. Everything below is the detail behind those three commands.

## `jsonfs`

```text
jsonfs --listen <url> [--listen <url>...] --file <path.json> [--writable] [--write-back]
       [--write-back-delay <ms>] [--max-entries <n>]
       [--dialects 9P2000,9P2000.u,9P2000.L]
       [--auth none|token:<secret>|password-file:<path>]
       [--tls-cert <pem> --tls-key <pem> --tls-client-ca <pem>]
       [--ws-origin <origin>...] [--msize <bytes>] [--log <level>]
```

`jsonfs` serves one JSON document as a 9P tree, using the mapping of
[ARCHITECTURE.md §7](9p/ARCHITECTURE.md):

| JSON | Filesystem |
| --- | --- |
| object | directory; the entry names are the keys, percent-encoded |
| array | directory; the entry names are `0`, `1`, … in order |
| string | a regular file holding the string's UTF-8 bytes, exactly, with nothing added |
| number | a regular file holding the number's text |
| `true` / `false` | a regular file holding `true` or `false` |
| `null` | a regular, empty file |

### Key encoding

A JSON key may contain anything; a 9P name may not contain `/` or NUL, may not be `.` or `..`,
and may not be empty. Keys are therefore escaped in this order and no other:

1. `%` becomes `%25` — **first**;
2. then `/` becomes `%2F` and NUL becomes `%00`;
3. if the result then reads `.` or `..`, it becomes `%2E` or `%2E%2E`;
4. the empty key becomes the single character `%`.

Escaping `%` first is what keeps the key `a/b` (which becomes `a%2Fb`) distinct from the literal
key `a%2Fb` (which becomes `a%252Fb`). Lookups apply the exact inverse, so the mapping is
injective and reversible.

### Number text

Numbers are rendered explicitly, never by a bare `ToString()`, and always through
`CultureInfo.InvariantCulture`:

- an integral value inside the 64-bit range is written as plain integer text (`42`, `-3`);
- otherwise, a value whose magnitude is at least `1e-6` and below `1e21` is written as its
  shortest round-trip decimal with no exponent (`2.5`, `0.0000015`);
- anything else keeps the source token as it was written in the document (`1e300`).

### Writing

`jsonfs` is **read-only by default**; a write, create, remove or rename is answered
`read-only file system` / `EROFS`, which is the strerror errno 30 carries and what
[the conformance fixture](9p/fixtures/conformance.md) now expects. With `--writable`:

- **Writing a scalar file replaces the value. The written text keeps the value's JSON type only
  when the original was already of that type and the text still parses as it: writing `42` over a
  number leaves a number, writing `yes` over a boolean leaves the string `yes`.** Everything else
  is demoted to a string. This is deliberate: a JSON document has types and a file has bytes, and
  the only alternative — guessing a type from the bytes — would turn the string `true` into a
  boolean behind the writer's back.
- Creating a file makes an empty string; `mkdir` makes an empty object.
- In an array only the **next** index may be created and only the **last** element removed, so
  that the remaining elements keep the names they had.
- Removing a key deletes it; renaming a key moves the value; removing a non-empty directory is
  `directory not empty` / `ENOTEMPTY`.
- **A `wstat` / `Tsetattr` is applied whole or refused whole** (reference §8 rule 27). jsonfs keeps
  no attributes of its own — the mode, the owner and the times are all derived — so the two fields
  it can honour are a length of **zero** on a scalar and a **name**. An update naming both performs
  both; an update that also names a mode, a file flag, a group, an owner or a time is answered `EOPNOTSUPP` and
  changes nothing. A non-zero length on a scalar is `EOPNOTSUPP` (a JSON scalar has no
  representation for "padded out to n bytes"), and any length on a directory is `EISDIR`.
- **Creates ignore the mode.** `CreateRequest.Perm`, `.Gid` and `.Flags` are dropped: a jsonfs file
  is always `0644` and a directory always `0755`. JSON has nowhere to keep a mode, a group or a
  create flag, and refusing one would break every ordinary `mkdir` from Linux, because v9fs always
  sends a mode. The file flags are not dropped: a create asking for `DMAPPEND`, `DMEXCL` or
  `DMTMP` is refused with `EOPNOTSUPP`, since JSON has nowhere to keep one and a create answered
  with success must make the file it was asked for (rule 19).

`--write-back` **implies `--writable`** — there is nothing to write back from a read-only server,
so the flag turns writing on rather than being inert without `--writable` beside it, and the usage
text and the README both say so. It rewrites the source document after each change, atomically: a temp file beside the
target, flushed to disk, then a rename over the original, then an `fsync` of the directory so that
the rename itself survives a crash (a no-op on Windows, where NTFS journals the rename). A write
whose directory sync fails is answered `EIO`: the document has been renamed, but the durability the
flag promises was not delivered, and a write that says so is retried where one that hides it is
trusted.

`--write-back-delay <ms>` **implies `--write-back`**, as `--write-back` implies `--writable`: a
window is only ever measured for a write-back. With `0`, the default, the document is rewritten
inside every change, as before. With a positive value the first change opens a window of that many
milliseconds and every change inside it rides along; when the window closes the document is
rewritten once, through the same temp file, rename and directory `fsync`. A rewrite the window
cannot make leaves the old document whole and the document dirty, and the next window retries; an
`fsync` on any file rewrites at once and reports the failure. A graceful stop writes back what the
open window still owes before exiting, so a change made just before the stop survives a restart
(conformance Part B step 8); a stop that is not graceful loses at most one window, which is the
trade the flag names. If that final rewrite fails, jsonfs prints the error and exits **1** rather
than exiting clean over a stale document.

`--max-entries <n>` (default `100000`) bounds the entries — every object key and every array
element, anywhere in the document — the served document may hold, beside the 64 MiB and 256-level
caps. The create or `mkdir` that would go past it is answered `No space left on device` / `ENOSPC`
and changes nothing (reference §8 rule 27); a rename moves an entry without adding one; a remove
frees the count, so the next create succeeds. It is the handler's half of reference §8 rule 40:
the library bounds what a peer may spend on the wire, and only a handler knows what an entry costs
to store.

### Refusals at startup

A document of 64 MiB or more, one nested deeper than 256 levels, or one holding more than
`--max-entries` entries is refused before it is served. The process prints a message naming the limit and exits **3**.

A `--tls-cert` / `--tls-key` / `--tls-client-ca` with no `tls://` or `wss://` `--listen`, and a
`--ws-origin` with no `ws://` or `wss://` one, are **accepted and unused**: the listeners jsonfs was
told to bind are the ones it binds. It logs a warning naming the flag at startup, through the same
`ILogger` everything else uses, because a certificate that is loaded and never presented reads as a
server that is protected when it is not. `todofs` does the same for its `--tls-*` flags.

## `ninep`

```text
ninep [--addr <url>] [--dialect 9P2000|9P2000.u|9P2000.L] [--uname <u>] [--aname <a>]
      [--auth none|token:<secret>|bearer:<token>] [--auth-optional] [--msize <bytes>]
      [--tls-ca <pem>] [--tls-cert <pem> --tls-key <pem>]
      <command> [args]
```

`ninep` is the conformance client. Its output formats are **frozen** by
[docs/9p/fixtures/conformance.md](9p/fixtures/conformance.md): every language in this workspace
diffs its own cli against one expected file, so a byte that changes here changes the
cross-language contract.

| Command | Output |
| --- | --- |
| `version` | `dialect=<negotiated> msize=<n>` |
| `ls PATH` | one entry per line, bytewise-sorted **by name** (C locale), directories suffixed `/`, no `.` or `..` |
| `ls -l PATH` | the same order, each line prefixed `<kind> <size> `, where kind is `dir`, `file` or `symlink` and size is the entry's own size in bytes |
| `cat PATH` | the file's raw bytes, with nothing added |
| `stat PATH` | `kind=dir\|file\|symlink size=<n> perm=<octal> qid=<type>.<version>.<path>` |
| `write PATH` | writes standard input at offset 0 with truncate, creating the file if it is absent; prints `wrote <n>` |
| `mkdir PATH`, `rm PATH`, `mv OLD NEW`, `readlink PATH` | nothing at all on success |
| any failure | `error: <ename> (errno <n>)` on standard error |

Exit codes: **0** success, **1** protocol or transport error, **2** server error (`Rerror` or
`Rlerror`), **3** usage.

Sorting is bytewise over the UTF-8 bytes, never `string.CompareTo` and never ordinal UTF-16: the
last two disagree above U+FFFF, where a surrogate pair sorts before U+E000..U+FFFF in UTF-16 and
after it in UTF-8. It is the **name** that is sorted, in both forms of `ls`: ordering the composed
`-l` lines instead would sort by kind, because the kind is the first column.

`-l` is this repository's own extension — `docs/9p/fixtures/conformance.md` fixes only plain `ls`,
and the fixture never runs the flag — so its format is documented here and nowhere else. It costs
one `stat` per entry, because a directory read carries qids and names and not lengths:

```text
$ ninep --addr tcp://127.0.0.1:5640 ls -l /dir
file 15 a b
file 100 big
file 19 file.txt
dir 0 sub/
```

`--uname` defaults to the local user name rather than to the empty string, because a server owns
an attached tree by the attaching user and an anonymous attach owns nothing.

`--auth-optional` falls back to a `NOFID` attach when the server refuses `Tauth`; without it, the
refusal is reported and the exit code is 2. The fallback prints
`ninep: server requires no authentication; attached anonymously` on **standard error** — a
credential that was presented and then dropped is exactly the case where a caller believes the
session is authenticated and it is not. Standard output is byte-identical either way, which is what
the fixture freezes.

**A flag that cannot apply to the rest of the command line is a usage error (exit 3)**, checked
before anything is dialled, rather than a flag that is read and then ignored:

| Flag | Needs | Otherwise |
| --- | --- | --- |
| `-l` | the `ls` command | `-l is a flag of ls, not of <command>` |
| `--tls-ca`, `--tls-cert`, `--tls-key` | a `tls://` or `wss://` `--addr` | `<flag> needs a tls:// or wss:// address; --addr is <scheme>://` |
| `--oidc-issuer`, `--oidc-client-id` | `--auth oidc-device` or `--auth oidc-password` | `<flag> is only for --auth oidc-device or oidc-password; --auth is <value>` |

The TLS row is the one that matters most: loading a certificate for a `tcp://` address and carrying
on is how a caller ends up believing a plaintext connection was encrypted.

## `todofs`

`todofs` serves a SQLite database as a per-user to-do tree. Its store is the only place in this
repository that speaks SQL, and every statement in it is parameterised; a repository test greps
the rest of the tree for SQL verbs and fails if one appears anywhere else.

### The database

The schema is created when the file is empty. SQLite is opened with `journal_mode=WAL`,
`foreign_keys=ON` and `busy_timeout=5000` on **every** connection: WAL so that readers do not
block the writer, foreign keys because SQLite has its cascades off by default and the schema
relies on them, and the busy timeout so that a lock contended for a moment waits rather than
fails.

There is **one writer connection**, behind a semaphore of one, and a read path that takes a pooled
connection per query. SQLite allows exactly one writer, so serialising writes in the process is
what keeps `SQLITE_BUSY` off the table entirely rather than retrying it away.

### Schema versions

`meta.schema_version` starts at `1`. **Migration strategy: none is needed for 0.1.0.** The store
creates the schema when the database is empty, opens a database at its own version, and **refuses**
a database whose `schema_version` is greater than the code's, with a message naming both versions.
Backward compatibility is therefore "same version or refuse".

### Qids

`path = (tableTag << 56) | rowId`, with `1 = users`, `2 = lists`, `3 = items` and `4 =` the fixed
nodes (`/`, `/users`, `/users/ctl`) and the item field files, whose low bits encode which
field. `version = updated_at` truncated to 32 bits. A qid is therefore stable across restarts —
it is derived from the row and not from a counter in the process — and it changes exactly when the
row does.

### The tree

```text
/users/                                    directory
/users/ctl                                 admin control file
/users/<user>/                             one per user; a user sees ONLY their own
/users/<user>/<n>/name                     list name (writable)
/users/<user>/<n>/<m>/label                item field (writable)
/users/<user>/<n>/<m>/description          item field (writable)
/users/<user>/<n>/<m>/status               item field, "open" or "done"
```

**Isolation.** The root handler an attach receives can reach only rows scoped by the attaching
user's id: `/users` resolves a name through a query that carries that id, and every list and item
query carries it too. There is no path through the tree to another user's rows, and a user cannot
even learn that another exists.

**`users/ctl`.** Reading it lists the users, one per line, **sorted bytewise by name** — the same
order `ninep ls` produces, and not the order the rows were created in. Writing it takes **exactly one command
per write**, `add <name>` or `remove <name>`, with an optional trailing newline; anything else is
`EINVAL`. Both directions need the realm role given by `--admin-role` (default `todofs-admin`),
read from the token's `realm_access.roles`; everyone else gets `EACCES` on the read and on the
write. `remove` takes the user's lists and items with it, through the schema's cascade. A user who
authenticates but has no row is created on attach.

**Quotas.** `--max-lists <n>` (default 1000) is the most lists one user may hold and
`--max-items <n>` (default 10000) the most items one list may hold. The `mkdir` that would exceed
either is refused with `ENOSPC` and nothing is created; an `rmdir` frees a slot. The store enforces
both inside the create's own writer transaction — the count and the insert are one atomic step — so
two creates racing at the cap yield exactly one row, and the handler layer never pre-checks. This is
the handler's half of reference §8 rule 40: the library bounds what a peer may spend on the wire,
and only a handler knows what an entry costs to store.

**Lists and items.** `mkdir /users/<u>/<n>` creates a list, where `n` must be the **next** number;
anything else is `EINVAL`, so the lists that already exist never change name. Its `name` file is
empty until written. `mkdir .../<n>/<m>` creates an item with an empty label and description and
`status = open`. Writing `status` accepts `open` or `done` with an optional trailing newline and
nothing else. `rmdir` removes an item, or a list that has no items. A partial write at an offset is
honoured, and every field is capped at **64 KiB**. A partial write splices into a buffer rather than
into the row, because a client may keep several writes outstanding on one fid and they arrive in any
order — and that buffer belongs to the **file**, so two opens of one field (two fids, or two
connections) see one another's writes and the later one splices into what the earlier one stored. It
is dropped when the last open closes. It is a **write** buffer: a read always answers from the
database, including on the fid that just wrote, so `done\n` written to `status` reads back as
`done` and `add bob` written to `/users/ctl` reads back as the user list.

**Creates ignore the mode.** `CreateRequest.Perm`, `.Gid` and `.Flags` are dropped: a todofs file is
always `0644` and a directory always `0755`. There is no column for a mode, a group or a create
flag, and refusing one would break every ordinary `mkdir` from Linux, because v9fs always sends a
mode.

### Truncation

Reference §8 rule 27 makes a handler perform the truncation it answered with success, or refuse it.
todofs has two different requests to answer, and it answers them differently on purpose.

An **`OTRUNC` open** is performed when the open is answered, not deferred to a write that may never
arrive — `ninep write` and `echo … > file` both open with `O_TRUNC`, and an open clunked without a
write must leave the file in the state the truncation asked for:

| File | A truncating open | A `wstat` / `Tsetattr` length of 0 |
| --- | --- | --- |
| `<n>/name`, `<n>/<m>/label`, `<n>/<m>/description` | the field becomes empty | the field becomes empty |
| `<n>/<m>/status` | becomes `open`, the value a new item carries | `EINVAL` |
| `/users/ctl` | nothing to remove; the users are untouched | `EINVAL` |

`status` has a vocabulary of two and the schema's own `CHECK (status IN ('open','done'))` forbids
anything else, so it has no zero-length value: its empty state is its initial one. Refusing the open
instead would refuse `echo done > status` and `ninep write .../status`, which is every documented
way of setting it. A `wstat` asking for a length of **zero bytes** is a different request, one
`status` can never satisfy, so that one is refused.

`/users/ctl` is the one file here whose truncating open changes nothing: it keeps no bytes of its
own — its read side renders the user table and its write side takes one command — so there is
nothing for a truncation to remove, and emptying it by deleting every user is not what a truncation
means. Its length is a rendering it computes, not a value a client sets, which is why the `wstat`
form is refused rather than performed.

**Every other field is derived**, so a `wstat` or `Tsetattr` naming a mode, a file flag, an owner,
a group, a time or a name is refused with `EOPNOTSUPP` and changes nothing — including the truncation that was
set beside it, because rule 27 makes the update all-or-nothing. A non-zero length is `EOPNOTSUPP`
(a database column is text, not a buffer padded out to a byte count) and any length on a directory
is `EISDIR`.

### Authentication

todofs authenticates against a Keycloak realm over the standard `Tauth` exchange. The client writes
an OIDC access token to the afid and the server answers `ok\n` when every one of these holds:

| Check | Rejection |
| --- | --- |
| The signature verifies against a key from the realm's JWKS | `EACCES` / `authentication failed` |
| The algorithm is `RS256` or `ES256`, and the token is signed | `alg=none` and HS256 key confusion are refused |
| `iss` is `--oidc-issuer` | refused |
| `aud` is `--oidc-audience` | refused |
| `exp` and `nbf` hold, with 60 s of leeway | expired or not yet valid is refused |
| The `kid` is one the realm published | an unknown `kid` asks the realm once, rate-limited, then refuses |
| `Tattach.uname` is the identity the token proved, or empty | a mismatched attach is refused |

The identity is `preferred_username`, falling back to `sub`, and the realm roles of
`realm_access.roles` become the identity's groups. **An unknown `kid` costs the realm one JWKS
fetch, not one per request**: the refresh is rate-limited by the configuration manager's own
interval, and a failed refresh is not retried per request.

**`--jwks-cache` has a floor of five minutes.** That is
`ConfigurationManager<OpenIdConnectConfiguration>.MinimumAutomaticRefreshInterval` in the identity
library todofs uses, and the library will not go below it; a shorter value is raised to it rather
than honoured, so `--jwks-cache 30` caches for five minutes and not for thirty seconds.

Tokens are never logged and never stored.

### Pointing todofs at a development issuer

The realm's discovery and JWKS documents are fetched over HTTPS, and a realm that is not reachable
over HTTPS is refused before a single token is read. `--allow-insecure-issuer` turns that check
off, and it exists for one case: an OIDC issuer on the loopback interface during development, such
as the repository's own fake issuer. With it off, anything on the path between the server and the
realm can substitute the realm's signing keys, and every token the server then accepts is
forgeable — so it belongs on a developer's command line and on no other.

```text
todofs --listen tcp://127.0.0.1:5641 --db /tmp/todo.sqlite \
       --oidc-issuer http://127.0.0.1:8080 --oidc-audience todofs --allow-insecure-issuer
```

### Running the fake issuer by hand

The suite validates tokens against an in-process fake issuer (spec §8.4) rather than a container,
and the same issuer runs as a program, so todofs and the cli's grants can be exercised without one
either:

```text
$ dotnet run --project tests/NineP.Conformance -- fake-issuer
issuer             http://127.0.0.1:52781
audience           todofs
client-id          todofs-cli
password grant     glenda / hunter2

token glenda       eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCIs…
token glenda+admin eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCIs…
token bob          eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCIs…
token expired      eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCIs…

the issuer serves plain HTTP on loopback, so todofs needs --allow-insecure-issuer
press Ctrl-C to stop
```

The four tokens are pre-minted because the password grant knows exactly one user
(`glenda` / `hunter2`), and the isolation and administrator paths need more than one; each is a
bearer credential for `ninep --auth bearer:<token>`. Point todofs at the printed issuer with
`--oidc-issuer <url> --oidc-audience todofs --allow-insecure-issuer`.

A [docker-compose.yml](../examples/NineP.TodoFs/docker-compose.yml) with a real Keycloak is
provided for **manual runs only**; no test touches it.

### The cli's OIDC grants

`ninep --auth bearer:<token>` uses a token the caller already holds. `--auth oidc-device` runs the
RFC 8628 device grant against `--oidc-issuer`, printing the verification URL and the user code to
**standard error** and polling the token endpoint at the interval the issuer asked for, honouring
`authorization_pending` and widening the interval by five seconds on `slow_down`. `--auth
oidc-password` runs the resource-owner password grant with `--oidc-client-id`, the `--uname`
argument and a password read from `$NINEP_PASSWORD` or from the terminal — never from the command
line, where it would reach the process table. It prints
`warning: the password grant is for development and test only` on every use.

**The three grants live in the cli and in no published package.** The libraries never perform a
login: a caller who holds a token hands it over through `BearerTokenCredential`, and how they got
it is their business.


### jsonfs mutation boundaries

Writable jsonfs checks the indented, escaped JSON representation after each mutation. It must
remain strictly below 64 MiB and within 256 container levels so write-back can be reloaded.
Growth beyond either bound returns ENOSPC and restores existing nodes, names, versions and data.
Deletion or truncation frees capacity. Directory cookies remain stable across unrelated removals.
Renaming a name to itself succeeds; array indices still cannot otherwise be renamed in place.

A write ending inside a UTF-8 rune may stage up to three trailing bytes on that open fid. The
next contiguous write completes the rune; committed JSON always stays valid. Invalid UTF-8 is
EINVAL without changing the committed value. An unfinished rune at clunk returns EINVAL and
still releases the fid. Persistence failures report failure and keep the in-memory mutation;
the disk contains the complete old or new document depending on whether replacement occurred.

The server preserves arrival order for pipelined operations on one fid. The UTF-8 regression
suite includes whole-file writes spanning several wire chunks with the default in-flight window,
as well as every split within a two-, three- or four-byte rune.
