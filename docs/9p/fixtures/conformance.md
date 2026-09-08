# Conformance scenario

The language-neutral acceptance test every implementation runs against itself, against every
previously merged language, and against the external reference peers. Two tools take part:

- **server under test**: the repo's `jsonfs` serving [sample.json](sample.json) read-only, or
  with `--writable` for the mutation part;
- **client under test**: the repo's `cli` (ARCHITECTURE.md §7), whose output formats are fixed
  below so that a `diff` against [sample.expected.txt](sample.expected.txt) is the verdict.

Both halves are exercised: *our client vs their server* and *their client vs our server*.

## Fixed cli output formats

| Command | Output |
| --- | --- |
| `ninep ls PATH` | one entry per line, **bytewise-sorted** (C locale), directories suffixed `/`, no `.`/`..`, trailing newline |
| `ninep cat PATH` | the raw bytes, nothing added |
| `ninep stat PATH` | `kind=dir` (or `file`, `symlink`) `size=<n> perm=<octal> qid=<type>.<version>.<path>` |
| `ninep version` | `dialect=<negotiated> msize=<n>` |
| `ninep write PATH` | writes stdin at offset 0 with truncate; prints `wrote <n>` |
| `ninep mkdir PATH`, `rm PATH`, `mv OLD NEW`, `readlink PATH` | nothing on success; non-zero exit + `error: <ename or strerror> (errno <n>)` on failure |

Exit codes: 0 success, 1 protocol/transport error, 2 server error (`Rerror`/`Rlerror`), 3 usage.

## Part A — read-only walk (all dialects, all transports)

For each dialect `d ∈ {9P2000, 9P2000.u, 9P2000.L}` and each transport `t ∈ {tcp, tls, ws}`
the server supports (a peer that lacks one skips it and records why):

1. Start `jsonfs --listen <t>://127.0.0.1:<port> --file sample.json --dialects <d>`.
2. Run `ninep --addr <t>://127.0.0.1:<port> --dialect <d> version` → must print `dialect=<d> msize=<n>` with `n ≥ 4096`.
3. Produce the listing exactly as [gen-conformance-expected.mjs](gen-conformance-expected.mjs)
   does: for every directory (depth-first, `/` first) print `ls PATH`, its entries, a blank
   line; for every file print `cat PATH`, `size <bytes>`, `sha256 <hex of the bytes>`, a blank
   line. The driver script in each repo (`tests/conformance/run.*`) composes this from `ls` and
   `cat` calls.
4. `diff` against `sample.expected.txt`. Any difference fails.
5. Negative checks (each must fail with exit 2 and the expected errno / ename):
   - `ninep cat /missing` → `ENOENT` / `file not found`
   - `ninep cat /dir` → `EISDIR` / `is a directory` (9P2000 dialects: read of a directory via
     `cat` is the client's own error because `cat` opens for reading — the driver instead runs
     `ninep ls /name` → `ENOTDIR` / `not a directory`)
   - `ninep write /name` on the read-only server → `EROFS` / `read-only file system`
   - `ninep ls "/dir/../.."` → the root listing (`..` at root is root)
   - a 17-element walk in one message is impossible through the cli; the codec test covers it.

## Part B — mutation (server started with `--writable`)

1. `printf 'changed' | ninep write /name` → `wrote 7`; `ninep cat /name` → `changed`.
2. `ninep mkdir /newdir`; `printf 'x' | ninep write /newdir/f`; `ninep ls /newdir` → `f`.
3. `ninep mv /newdir/f /newdir/g`; `ninep ls /newdir` → `g`.
4. `ninep rm /newdir/g`; `ninep rm /newdir`; `ninep ls /` must not contain `newdir/`.
5. `ninep rm /dir` (non-empty) → `ENOTEMPTY` / `directory not empty`.
6. `printf '7' | ninep write /list/6` (append to array) → ok; `printf '7' | ninep write /list/9` → `EINVAL`/`ENOENT`.
7. `printf 'yes' | ninep write /enabled` → `ninep cat /enabled` → `yes` (type demoted to string, documented).
8. With `--write-back`, restart the server and confirm step 1's value persisted.

## Part C — authentication

1. Server with `--auth token:s3cret`: `ninep --auth token:s3cret ls /` succeeds;
   `ninep --auth token:wrong ls /` → attach fails with `EACCES` / `authentication failed`;
   `ninep ls /` (no credential) → attach fails.
2. Server with `--auth none`: `ninep --auth token:x version` → `Tauth` refused (9P2000/.u:
   `Rerror "authentication not required"`; `.L`: `Rlerror ECONNREFUSED`, errno 111 — see
   protocol-reference §5.2), client falls back to `NOFID` attach only if `--auth-optional`;
   otherwise reports the refusal.
3. Dialect set: a server started `--dialects 9P2000.L` answers a `--dialect 9P2000` client with
   `Rversion "unknown"` and the client exits 1 with a version error; it must **not** downgrade to a
   dialect the server was told not to speak (protocol-reference §5.1 step 4). Symmetrically,
   `--dialects 9P2000` answers a `9P2000.L` client with `9P2000`.
4. todofs OIDC (run in the repo's own test suite against the fake issuer, not cross-language):
   `ninep --auth oidc-device --oidc-issuer <fake> --oidc-client-id <id> ls /users` completes the
   RFC 8628 device grant and lists exactly the authenticating user's directory; `--auth
   bearer:<expired token>` fails the attach.

## Part D — robustness (codec + hostile client, in-repo only)

Run by each repo's test suite, not cross-language: the mutation matrix of
`protocol-reference.md` §9 over `wire-vectors.json`, plus a hostile client that sends a size lie
(`size = 0xFFFFFFFF`), a `Twalk` with `nwname = 17`, 70 000 distinct fids, 300 concurrent tags,
and a half-header followed by silence for 31 s. The server must stay up, answer the legal
requests of a second connection, and close only the offending connection.

For the concurrent-tag case, each ordinary request receives exactly one normal reply or EAGAIN
(`resource temporarily unavailable` in plain 9P2000) when worker capacity is exhausted. A Tflush
placed after the flood must still receive Rflush; an unknown oldtag keeps all ordinary responses
observable. Verify unique reply tags and a successful ordinary request after capacity drains.
This follows reference §8 rule 8 as revised for review finding C09 of 2026-09-08.

## Part E — edge cases (cli-driven; all dialects; tcp and the in-memory transport)

The ordinary mistakes a user of a filesystem makes, frozen so that every language answers them
the same way. Part E is not a transport question, so it runs over tcp and over the in-memory
transport only, once per dialect, and it is part of the cross-language matrix (loop stage 7) like
Parts A–C. Every negative step must fail with **exit 2** and the errno / ename named, unless the
step says otherwise; every positive step must exit 0 with exactly the output named. Paths are
resolved by the cli as in Part A: empty elements are dropped, so `/dir/` and `//dir` name `/dir`.

**Read-only server** (`jsonfs --file sample.json`):

1. **Missing paths.** `ninep ls /missing`, `ninep ls /dir/missing`, `ninep ls /missing/deeper`,
   `ninep stat /missing`, `ninep cat /dir/sub/missing` → `ENOENT` / `file not found`. A walk that
   fails at its first element and one that fails later give the same answer to the user.
2. **Empty directories.** `ninep ls /emptyobj` and `ninep ls /emptyarr` → exit 0 and **zero
   bytes** of output (no newline). On the wire the first `Tread` (9P2000/.u) or `Treaddir` (.L)
   of the directory is answered with `count = 0`.
3. **A path through a file.** `ninep ls /name/x`, `ninep cat /name/x`, `ninep stat
   /dir/file.txt/x` → `ENOTDIR` / `not a directory`. The server answers the walk with the prefix
   it could resolve (protocol-reference §5.4); the client sees that the last qid it reached is not a
   directory and reports `ENOTDIR`, not the `ENOENT` of a name that is simply absent.
4. **Slashes and dot-dot.** `ninep ls /dir/` and `ninep ls //dir` print what `ninep ls /dir`
   prints; `ninep ls /dir/../dir` prints the same; `ninep ls /../..` prints the root listing;
   `ninep stat /` → `kind=dir …`.
5. **Names.** `ninep ls /names` prints, in this byte order and nothing else:
   `.hidden`, `héllo wörld`, the 255-byte name (85 × U+4E16 `世`), `Ａ` (U+FF21), `🚀`
   (U+1F680) — a listing sorted by UTF-16 code units or by locale puts `Ａ` after `🚀`, and a
   name-length check that counts characters instead of bytes is caught by the next step. `ninep
   cat /names/🚀` → `astral`; `ninep stat "/names/世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世世"` → `kind=file size=9 …`.
6. **A name over 255 bytes never reaches the wire.** `ninep mkdir "/$(printf 'x%.0s' {1..256})"`
   (256 ASCII bytes) and `ninep mkdir "/$(printf 'é%.0s' {1..128})"` (128 characters, 256 bytes)
   → **exit 1** (a protocol error: the client refuses to encode the name, protocol-reference §8
   rule 3) and no T-message for it is sent — on the read-only server a request that did reach it
   would have drawn `EROFS`, exit 2, so the exit code tells the two apart.

**Writable server** (`jsonfs --file sample.json --writable`):

7. **Creating what exists.** `ninep mkdir /dir`, `ninep mkdir /name`, `ninep mkdir /emptyobj` →
   `EEXIST` / `file already exists`; `printf 'x' | ninep write /dir` → `EISDIR` / `is a
   directory`.
8. **A missing parent.** `ninep mkdir /nope/x`, `printf 'x' | ninep write /nope/x`,
   `ninep mv /name /nope/x` → `ENOENT`.
9. **Removing what is not there, and the root.** `ninep rm /missing` → `ENOENT`; `ninep rm /`
   and `ninep rm /dir/..` → `EPERM` / `permission denied` (remove(5) works through the parent
   directory, and the root of the tree has none); `ninep ls /` afterwards still lists `dir/`.
10. **Renames.** `ninep mkdir /e; printf 'a' | ninep write /e/f; printf 'b' | ninep write /e/g;
    ninep mv /e/f /e/g` → `EEXIST` (stat(5): the target must not exist; the same answer in .L);
    `ninep cat /e/g` → `b`; `ninep mv /missing /e/h` → `ENOENT`; `ninep mv /dir /dir/sub/inside`
    → `EINVAL` / `invalid argument` (a directory cannot move into its own subtree).
11. **A zero-byte write.** `printf '' | ninep write /name` → `wrote 0`; `ninep cat /name` → zero
    bytes; `ninep stat /name` → `… size=0 …`.
12. **Remove, then reuse the name.** `ninep mkdir /gone; ninep rm /gone; ninep ls /gone` →
    `ENOENT`; `ninep mkdir /gone` → exit 0.
13. **An empty directory's lifecycle.** `ninep mkdir /e2; ninep ls /e2` → exit 0, zero bytes;
    `ninep rm /e2` → exit 0; `ninep ls /` no longer contains `e2/`.

## Part F — scale and wire-level edge cases (in-repo only)

Run by each repo's test suite like Part D: a wire-level client from the repo's test support
against the shipped server hosting the repo's in-memory test filesystem, over the in-memory
transport unless a case says loopback TCP. "Session stays up" means the same connection then
answers an ordinary `Twalk` + `Tclunk`. Cases marked **[approved 2026-09-08]** freeze a behaviour the reference
implementation does not have yet; the owner may overrule them in the Decision Log.

1. **A fid that was never opened.** `Twalk` to `/name` then `Tread` (and, in .L, `Treaddir` on a
   walked directory) → `Rerror "bad open mode"` / `Rlerror EINVAL`; session stays up. `Twrite` on a
   fid opened `OREAD` and `Tread` on a fid opened `OWRITE` → `EACCES`.
2. **An empty directory at the wire.** 9P2000/.u: `Tread offset=0` → `Rread count=0`, and a second
   `Tread offset=0` → `count=0` again. .L: `Treaddir offset=0` → `Rreaddir count=0`.
3. **`count = 0`.** `Tread count=0` on an open file → `Rread count=0`; on an open directory →
   `count=0` too **[approved 2026-09-08]** — `ERANGE` is for `0 < count <` one record (Part D's packer rule),
   never for a request that asked for nothing.
4. **Reads at and past the end.** `offset = size`, `offset = size + 1`, and
   `offset = 2⁶⁴ − 1` → `Rread count=0`, session stays up.
5. **The root fid.** `Tclunk` of the attach fid → `Rclunk`, and a fresh `Tattach` works.
   `Tremove` of the attach fid → `EPERM`, **and the fid is freed anyway** (remove(5) clunks even on
   error): a following `Tclunk` of the same fid → `EBADF` / `unknown fid`.
6. **Creating what exists, every verb.** Against a directory that already holds `f` and `d`:
   `Tcreate f`, `Tlcreate f`, `Tmkdir d`, `Tmkdir f`, `Tsymlink f`, `Tmknod f`, `Tlink … f` →
   `EEXIST` / `file already exists`, session stays up; the directory listing is unchanged.
7. **Name limits.**
   - A 256-byte name in `Twalk`, `Tcreate`, `Tlcreate`, `Tmkdir`, `Trenameat` and `Tunlinkat` is
     malformed (§8 rules 2–3): `Rerror "bad message"` / `Rlerror EPROTO` and the connection is
     closed; a second connection is unaffected.
   - A 255-byte name works end to end — create, walk, list, stat, remove — in every dialect at
     **msize 4096**, where a stat record for it is a third of the payload.
   - With `Limits.MaxNameLength = 64`, a legal 65-byte name in any of those messages →
     `ENAMETOOLONG` / `file name too long`, session stays up **[approved 2026-09-08]**; the limit bounds what a
     handler is asked to store, and today no port enforces it.
   - Names are bytes: create `é` as NFC (`C3 A9`), walk `é` as NFD (`65 CC 81`) → `ENOENT`; the
     listing holds one entry. No normalisation anywhere.
8. **A listing while the directory changes.** A directory of 5 000 entries at msize 4096, read
   one `Tread`/`Treaddir` at a time; after every reply a second connection creates one new entry
   and removes one existing entry. The listing terminates with `count=0`, draws no error, contains
   no duplicate, and contains every entry that the mutations did not touch exactly once. Then the
   directory itself is removed by the second connection while a listing fid is open: the next read
   answers an error (`ENOENT`) or `count=0`, never a close or a hang, and `Tclunk` of the listing
   fid → `Rclunk`.
9. **A stale fid.** Fid on `/d/f`; another connection removes `/d/f`; then `Tstat`/`Tgetattr`,
   `Topen`/`Tlopen`, `Tread` and a `Twalk` from a fid whose directory was removed answer an error
   (`ENOENT`) or keep serving the old object (§5.7: implementation-defined) — but never a close,
   a hang, or bytes of a different file.
10. **A file larger than 4 GiB** (loopback TCP, `.L` at msize 1 MiB and 9P2000 at 64 KiB). A
    synthetic file handler of **4 GiB + 1 MiB** whose eight-byte blocks encode their full 64-bit block index with per-byte lane masks,
    computed on demand — nothing is stored. The client streams it with `read(offset, buf)` and the
    default window, checking every byte; the bytes above offset 2³² are correct; the client's
    peak RSS grows by less than 64 MiB and the server's by less than 8 × msize (Part D's bound).
    `read_all()` on that file → `EFBIG` **before the first `Tread`**, decided from `getattr`/`stat`
    size against `ClientOptions.max_read_all` (default 256 MiB) **[approved 2026-09-08]**; a whole-file read
    must never exhaust memory or return a truncated buffer, and the cap is documented in
    `docs/client.md`.
11. **A directory of one million entries.** A lazy directory handler whose entries are derived
    from the cursor (`e0000000` … `e0999999`, no list held) served over loopback TCP at msize
    1 MiB: `Fid.readdir()` streams exactly 1 000 000 entries in cursor order with no duplicate;
    the client's peak RSS grows by less than 64 MiB; the server's per-connection memory stays
    within Part D's bound. A second run of **10 000 entries with 255-byte names at msize 4096**
    checks the record boundary at the minimum msize: every reply carries at least one whole
    record and no record is split. `Session.readdir(path)` — the convenience that returns a
    list — documents that it holds every entry in memory **[approved 2026-09-08]**.
12. **Creating one million entries.** 1 000 000 × (`Twalk` + `Tcreate`/`Tmkdir` + `Tclunk`)
    into the in-memory filesystem, in process, with the client's window of in-flight requests.
    Every request is answered — `Rcreate`, or `EAGAIN` that the driver retries — and the
    connection is never closed; the fid table never holds more than the window; the server's RSS
    afterwards is at most baseline + N × K bytes, with K recorded in `docs/benchmarks.md`; a
    listing afterwards yields exactly 1 000 000 entries. A handler that answers `ENOSPC` sees its
    error relayed unchanged, the session stays up, and a remove followed by a create succeeds.
    jsonfs `--writable`: a create that would take the document past its size cap → `ENOSPC`
    **[approved 2026-09-08]**.

Each Part F case is a named test. Ordinary CI runs bounded versions only. Full F10/F11/F12
are local-only opt-ins and are explicitly skipped in CI even when opt-in variables are set.
Full F12 uses the fixed larger local budget below. Sixty seconds is a reference-machine benchmark
observation, not a hard target on shared runners. The measured RSS and predeclared K go into
`docs/benchmarks.md`.

## Approved clarifications and expanded cases — 2026-09-08

These additions are required in subsequent ports. The owner approved the proposed behaviors.
E6 forbids sending the invalid mutation, not prior negotiation/attach frames. E8/E10 moves across
directories report EOPNOTSUPP outside .L. Same-name rename succeeds without changing identity.
F3 uses Treaddir for .L directories; .L directory Tread remains EISDIR. F8 bounds mutations to
32 pages and empties a directory before removing it. F9 additionally removes and recreates the
same name and checks that stale fids cannot read/write the replacement.

F7 covers every applicable name field, including symlink/mknod/link/rename and Twstat names,
and tests accented/astral byte boundaries. F10b enforces the actual byte cap during accumulation
as well as checking metadata; missing or stale size must not bypass it. F12 counts persistent
attach/directory fids in its allowance. F12c also covers writes, longer renames, JSON escaping,
concurrent growth and space reclamation, with all-or-nothing quota/depth refusals.

Scale data is generated, with no large file in the repository or whole-file expected buffer.
CI runs 8 MiB streamed data, 10000 lazy directory entries and 10000 real creates. Full F10
(4 GiB + 1 MiB in .L and 9P2000), F11 (1000000 lazy entries) and F12 (1000000 real creates)
are local-only opt-ins. All three full tests explicitly skip when `CI` or `GITHUB_ACTIONS` is
true or 1, even if opt-in variables are set.

Each workload owns child processes with two runtime processors and a default 128 MiB managed
heap cap per process. The combined process budget is 512 MiB. File warmup streams 256 MiB
(or the full workload when smaller); directory warmup lists 10000 entries. The server's
8 × msize check applies to retained managed growth after warmup; peak RSS is reported
separately. Client peak RSS growth remains bounded by 64 MiB. Full F12 uses a larger fixed
local budget: 1536 MiB resident memory and a 1024 MiB managed heap cap. Its per-entry allowance
is predeclared as 1024 bytes plus 32 MiB total startup overhead. Exceeding a workload's budget
is a failure, never an automatic skip.

For C#, `NINEP_FULL_SCALE=1` opts into full F10/F11 locally. Full F12 requires
`NINEP_FULL_CREATE_SCALE=1 NINEP_SCALE_MEMORY_MIB=1536 NINEP_SCALE_HEAP_LIMIT=0x40000000`.
Run `dotnet test --project tests/NineP.Server.Tests -f net10.0 -c Release -- --filter-class
NineP.Server.Tests.AdditionalScaleTests` as one shell line. Each port records its commands
and measured resource costs in its own `docs/benchmarks.md`.

| CLI id | Expected behavior |
| --- | --- |
| E14 | Read-only create, mkdir, remove, rename and truncate; refuse with EROFS and preserve contents/listing. |
| E15 | Overwrite a longer value with a shorter UTF-8 value; no old suffix and stat reports byte length. |
| E16 | Remove the last array member and append again; reject non-last removal, negative/skipped/noncanonical indices without renumbering. |
| E17 | Create/read/rename/remove encoded percent, slash, dot and empty keys; write-back/reload keeps distinct keys distinct. |
| E18 | Rename collisions across file/directory combinations preserve both objects, descendants and contents. |
| E19 | Writes and renames preserve qid.path; writes change version; remove/recreate allocates a fresh identity. |

| Wire/handler id | Expected behavior |
| --- | --- |
| F13 | Walk 15/16/17/32/33 components, including missing/non-directory failures around a chunk boundary; no temporary fid leaks. |
| F14 | At msize 4096, split walks with 255-byte names by encoded frame size as well as MAXWELEM. |
| F15 | Read lengths 0/1/iounit−1/iounit/iounit+1/exact multiples; repeated short and reordered replies produce exact bytes. |
| F16 | Read-all cap below/at/above the limit, zero cap, invalid configuration and convenience APIs; tiny limits exercise production code. |
| F17 | Unknown/zero/understated metadata and growth after stat cannot bypass the incremental read-all cap; no truncated success. |
| F18 | Small reads/writes across 2³¹ and 2³² preserve offsets and high-bit-sensitive generated content. |
| F19 | Zero-byte writes at start/EOF/huge offsets do not extend or allocate according to the offset. |
| F20 | Zero/small/oversized advertised iounit is interpreted or clamped to the negotiated payload limit. |
| F21 | Independent directory fids, rewind before/after EOF, and zero-count reads preserve the correct positions. |
| F22 | Record-size−1/exact/+1 budgets with long UTF-8 names and both record formats; ERANGE retry loses no entry. |
| F23 | Early exit, cancellation and page failure stop enumeration, release owned resources and preserve session usability. |
| F24 | Opaque cookies above 2³² round-trip unchanged; a nonempty page ending at the requested cookie is a protocol error, not an infinite loop. |
| F25 | Failed create preserves the directory fid; retry after EEXIST/ENOSPC succeeds and successful create adopts the new object. |
| F26 | Two synchronized creates of one name yield exactly one success and one EEXIST. |
| F27 | Mid-transfer server error, cancellation or disconnect cannot report a complete success; pending operations drain. |
| F28 | UTF-8 characters split across writes round-trip. Only an incomplete trailing rune (at most 3 bytes) is staged per open; invalid input preserves the prior committed value. Clunk with an unfinished rune returns EINVAL and releases the fid. |
| F29 | Growth by mkdir or move cannot exceed reloadable JSON depth; ENOSPC refusal preserves the tree. |
| F30 | Injected write-back failures report an error, preserve a complete old/new on-disk document and remove temporary files; retry succeeds. |

## External peers

- **hugelgupf/p9 `p9ufs`** (`.L` server): export a directory materialised from `sample.json` by
  [gen-conformance-tree.mjs](gen-conformance-tree.mjs) (same mapping; directories and files on
  disk), then run Part A step 3 with our cli in `9P2000.L` over TCP. Expected output: identical to
  `sample.expected.txt` **minus** the `emptyarr/`/`emptyobj/` entries only if the tree generator
  cannot create them (it can — they are empty directories).
  - **Known caveat — `Tfsync`.** p9 decodes `Tfsync` as `fid[4]` only (11 bytes,
    `hugelgupf-p9-messages.go:1678–1692`), while our encoder always sends diod's 15-byte
    `fid[4] datasync[4]` (protocol-reference §3.4). The four trailing bytes are unread by p9 and
    p9's handling of trailing bytes is not in the vendored sources, so a `Tfsync` against p9ufs may
    succeed, may error, or may desynchronise the connection. Record the observed behaviour in
    `docs/interop.md` and do not treat an `fsync` failure against p9ufs as a defect in our codec
    without first checking the frame against the two `Tfsync` golden vectors.
- **plan9port `9p`** (9P2000 client): `9p -a 'tcp!127.0.0.1!<port>' ls -l /` and
  `9p -a … read /name` against our jsonfs; compare names and bytes.

Record every run — peer, version, dialect, transport, pass/fail, and the diff on failure — in
the repo's `docs/interop.md`. "Not run" entries carry the reason.

Plain 9P2000 carries only an ename. E9/F5 therefore assert the canonical `permission denied`
projection (EACCES on the receiving client); .u and .L preserve numeric EPERM. This does not
change the server's root-removal policy. Scale child processes use `DOTNET_PROCESSOR_COUNT=2`
to bound per-thread buffer pools independently of host CPU count, with a fixed 256 MiB file
warmup (or the full workload when smaller).
