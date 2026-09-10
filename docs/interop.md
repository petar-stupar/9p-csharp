# Interop

What this implementation has been run against, in both directions, with the exact command and the
observed result. A row that was not run says so and says why: **interop that was not run is not
interop that passed.**

**Merged languages before this one: none yet (ticket 001 is the first).** The cross-language matrix
of ARCHITECTURE.md §10.4 therefore has no rows other than the external reference peers below, and
this repository is the peer the next language will be measured against.

Every row below is reproducible: it is a test in `InteropTests` (`tests/NineP.Client.Tests`),
opt-in through one environment variable per peer, and `tests/interop/setup.sh` fetches the peers at
the pinned versions and prints those variables. See [Reproducing these runs](#reproducing-these-runs).

## The machine these runs were made on

| | |
| --- | --- |
| Host | Apple M4 Pro, macOS 26.6.2 (25G83), Darwin 25.6.0, arm64 |
| This implementation | `9p-csharp` at `0.1.0` (the released tag plus the fixes these runs produced), .NET SDK 10.0.103, `Release` build |
| Date | 2026-09-10 |
| Containers and VM | Docker Desktop 7.0.12 (engine 29.7.2) for diod; lima 2.2.0 (`vz`) running Debian 12 on kernel `6.1.0-53-arm64` for v9fs |

## External reference peers

| peer | version | role | dialect | transport | result | command | notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Linux v9fs (kernel client) | Linux 6.1.0-53-arm64 (Debian 12), `9p` + `9pnet_fd` | client | 9P2000.L | tcp | **pass** | `mount -t 9p -o trans=tcp,port=<p>,version=9p2000.L,access=any,uname=<u> <host-ip> /mnt` then `ls`, `cat`, `stat`, a refused write | `InteropTests.LinuxV9fsAgainstOurJsonfs("9p2000.L")`; every file read back byte-identical, directory listing complete, refused write reports `Permission denied` |
| Linux v9fs (kernel client) | as above | client | 9P2000.u | tcp | **pass** | as above with `version=9p2000.u` | `InteropTests.LinuxV9fsAgainstOurJsonfs("9p2000.u")`; identical bytes and listing |
| Linux v9fs (kernel client) | as above | client | 9P2000 | tcp | **pass** | as above with `version=9p2000` | `InteropTests.LinuxV9fsAgainstOurJsonfs("9p2000")`; identical bytes and listing. The refused write surfaces as `Unknown error 526`: see the ename finding below |
| diod | 1.0.24-5 (Debian bookworm package) | server | 9P2000.L | tcp | **pass** | `diod -f -n -l 0.0.0.0:5640 -e /export` in a container, then our cli's `version`, `ls`, `cat`, `stat`, `write`, `mkdir`, `mv`, `rm` with `--uname root --aname /export` | `InteropTests.OurClientAgainstDiod`; needed the `Trename` fallback and the hosts entry below |
| hugelgupf/p9 `p9ufs` | v0.4.1, built with go1.26.0 | server | 9P2000.L | tcp | **pass** | `p9ufs -root <tree> 127.0.0.1:<p>` then our cli's `version`, `ls`, `cat`, `stat` over a tree with a Unicode name and a nested directory | `InteropTests.OurClientAgainstP9ufs`; the dirent-type accommodation of 2026-09-06 still applies |
| plan9port `9p` | git `b6564bd9`, 2026-08-26, built from source | client | 9P2000 | tcp | **pass** | `9p -a 'tcp!127.0.0.1!<p>' ls /`, `read /name`, `read /unicode`, `stat /dir` against our `jsonfs` | `InteropTests.Plan9portClientAgainstOurJsonfs`; the framing fix of 2026-09-06 holds |
| diod | 1.0.24-5 | server | 9P2000, 9P2000.u | tcp | **not run: diod speaks 9P2000.L only** | — | it answers any other `Tversion` with an `Rlerror` on `NOTAG`; see the finding below for what the client now makes of that |
| Linux v9fs under Docker Desktop | LinuxKit 7.0.12 kernel | client | — | tcp | **not run: that kernel has `9p` but no `9pnet_fd`** | `mount -t 9p …` → `Invalid argument` | which is why the v9fs rows use a VM rather than a container |

## What the runs of 2026-09-10 found

Three things, none of them visible to a suite in which this client talks only to this server.

### diod has no `Trenameat`

diod implements `Trename` and not `Trenameat`, and answers the latter `EOPNOTSUPP` (errno 95).
This client used `Trenameat` for every 9P2000.L rename, so `mv` against the most deployed `.L`
server failed. Linux v9fs falls back to `Trename` on exactly that answer, and the client now does
the same: the file gets a fid of its own, the destination directory's fid is the one already held,
and the fallback is attempted once. `ClientInteropRegressionTests.RenameFallsBackToTrenameWhenTheServerLacksTrenameat`
pins it on the wire; `InteropTests.OurClientAgainstDiod` exercises it against diod itself.

### diod answers an unknown dialect with an error, not `Rversion "unknown"`

version(5) answers a `Tversion` for a dialect the server does not speak with
`Rversion "unknown"`. diod answers it with an `Rlerror` carrying `NOTAG`. The client routed that
frame to its pending-tag table, found nothing, and terminated the session as a protocol violation
("the server answered unknown tag 65535"). An error on `NOTAG` while a `Tversion` is outstanding is
now the answer to that `Tversion`: connecting fails with a `NinePVersionException` that quotes the
error, and the session is not blamed for a tag it never issued.
`ClientInteropRegressionTests.AnErrorAnsweringTheVersionRequestIsAVersionError` pins both the
`.L` and the 9P2000 shape of the error.

### Linux maps 9P2000 enames through a fixed, case-sensitive table

Over plain 9P2000 an `Rerror` carries only the ename, and v9fs turns it into an errno with the
table in `net/9p/error.c`, an exact-match hash of Plan 9 wordings and glibc `strerror` texts. An
ename that is in neither becomes `ESERVERFAULT` (526). Of the 27 enames in `ErrorTable`, six are
in that table (`file not found`, `i/o error`, `permission denied` for `EACCES`, `file already
exists`, `not a directory`, `file too big`); `permission denied` for `EPERM` maps to `EACCES`
there; and the other twenty are unknown to it. The measured case: a write to the read-only
`jsonfs` over `version=9p2000` reports `Unknown error 526` where `.u` and `.L`, which carry the
errno, report `Permission denied`. Several of ours differ from an accepted wording by a character
(`read-only file system` against Linux's `read only file system`; `is a directory` against
`Is a directory`).

Changed, by owner decision the same day: `ErrorTable` now sends, for every errno Linux
can name, a string Linux maps to that errno (a Plan 9 wording where Linux lists one, `strerror`
text otherwise), `Errno` carries every one of those errnos, and every string in Linux's table plus
every wording this table used to send is understood on receipt. The table is
`docs/9p/fixtures/linux-9p-errors.json`, generated from the kernel source at a pinned commit, and
`ErrorTableTests` holds the implementation to it row by row; the rule is stated in the workspace
architecture §3. The reference-defined enames that never reach a Linux mount (`authentication not
required`, `version not negotiated`) keep their wording.

### diod resolves the client's address, and drops it when it cannot

diod calls `getnameinfo` on every accepted connection and closes it when the lookup fails
(`getnameinfo: Temporary failure in name resolution`). Inside a container the address a published
port carries (`192.168.65.1` under Docker Desktop, `172.17.0.1` on Linux) has no reverse entry, so
the test adds one with `--add-host`. This is diod's behaviour, not a 9P matter, but anyone running
diod in a container will meet it.

## Reproducing these runs

```text
brew install lima go docker            # or the equivalents; git and a C toolchain for plan9port
eval "$(tests/interop/setup.sh)"       # fetches the peers at the pinned versions, prints the variables
dotnet test --project tests/NineP.Client.Tests -f net10.0 -- --filter-class NineP.Client.Tests.Compat.InteropTests
```

`setup.sh` installs `p9ufs` v0.4.1 with `go install`, builds plan9port at `b6564bd9` from source,
builds the diod container from `tests/interop/diod.Dockerfile` (Debian bookworm's 1.0.24-5), and
creates a lima Debian 12 VM booted on the full `linux-image-arm64`/`-amd64` kernel, because
Debian's cloud kernel ships without the `9p` module. Each test skips, naming its variable and how
to get the peer, when the variable is unset; in CI all of them skip. A different machine records
its own row: the peer versions are pinned, the results are what the assertions compare, and the
table above says which machine produced these.

## What the runs of 2026-09-06 found

Two defects, both of them invisible to a test suite in which our client talks only to our server.

### A 9P2000 directory read carried each stat record's length twice

`9p ls -l /` answered `dirreadall /: malformed directory contents`. The bytes on the wire showed
every entry beginning `4a00 4800 …` — `size[2] = 74` followed by a stat record whose own
`size[2] = 72`. That is the `stat[n]` framing of `Rstat` and `Twstat`, where stat(5)'s BUGS section
does put the length on the wire twice. A **directory read** is not that: read(5) returns an integral
number of bare stat records, each carrying its `size[2]` once (reference §4.2, "total byte count of
the following data (excludes itself)").

`StatCodec` now has both framings — `Read`/`Write` for `stat[n]`, `ReadRecord`/`WriteRecord` for a
bare record — and `DirectoryPacker` and the client's `ReadStatRecordsAsync` use the bare one. The
budget arithmetic in the packer changed from `+ 4` to `+ 2` with it.

Two implementations that both get this wrong read each other perfectly well, which is exactly why
only an external peer could find it, and exactly why exit criterion 4 exists.

### `p9ufs` writes the qid type where the POSIX `d_type` belongs

Every directory `p9ufs` listed came back as a regular file. Its `Rreaddir` entries carry
`0x80` in the `type[1]` field:

```text
80 00000000 8f05ef0200090800  0100000000000000  80  0800 "emptyobj"
qid (type 0x80 = QTDIR)       offset = 1        ^^  name
```

Reference §4.3 makes that byte the POSIX `d_type`, in which `DT_DIR` is **4** and `0x80` is not a
value at all. `DirEntryCodec` now believes the byte when it names a kind and falls back to the qid
when it does not — `DT_UNKNOWN` included — because reference §4.1 makes the qid authoritative for
the two kinds a qid can express. A conformant server is unaffected: its `DT_REG` and `DT_DIR` are
taken as sent.

### The `Tfsync` caveat, resolved by measurement

`docs/9p/fixtures/conformance.md` warns that hugelgupf/p9 decodes `Tfsync` as `fid[4]` only
(11 bytes) while this implementation always sends diod's 15-byte `fid[4] datasync[4]`, and that the
four trailing bytes might succeed, error, or desynchronise the connection. Measured:

```text
opened name iounit=1048552
Tfsync: Rfsync received
after fsync, read 11 bytes: conformance
```

`p9ufs` **accepts** the 15-byte frame, answers `Rfsync`, and the following `Tread` on the same
connection returns the right bytes — so the trailing `datasync[4]` is consumed rather than left in
the stream. Nothing was changed in the codec, and nothing needed to be: both `Tfsync` forms remain
golden vectors, the encoder still sends 15 bytes, and the decoder still accepts 11.

## What was not exercised

- The `.L` extras `p9ufs` does not implement (`Tlock`, `Tgetlock`, `Txattrwalk`, `Txattrcreate`)
  were not driven against it; our own conformance and unit suites cover them against our server.
- `wss://` and `tls://` were not used against either peer: neither speaks anything but plain TCP.
- Authentication was not exercised across peers. `p9ufs` requires none, and plan9port's `9p`
  attempted `Tauth`, was answered `Rerror "authentication not required"`, and attached with
  `NOFID` — which is the refusal shape reference §5.2 prescribes and is itself a small interop
  result.

## Additional conformance coverage

The self scenario now reports **145 outcomes**: the existing A–D checks plus every CLI edge
case E1–E19 across three dialects over TCP and memory. `PartE.cs` reports each ID separately;
`ConformanceTests` checks the count, the six combinations for each E ID, and uniqueness.
The additional F1–F30 cases run as named client/server/doc tests, covering malformed names,
limits, mutable listings, transfer failure and JSON persistence. Full F10/F11/F12 workloads
are local-only opt-ins with explicit skips in CI; their smaller versions run in CI.
See [the conformance requirements](9p/fixtures/conformance.md) and [measured scale costs](benchmarks.md).


## Audit 002 boundary checks — 2026-09-10

The final `Compat.InteropTests` run passed **6/6 cases**, with no skips, using the existing local peers: p9ufs v0.4.1 (go1.26.0), plan9port b6564bd, diod 1.0.24-5, and Linux 6.1.0-53-arm64 in the `ninep` Lima VM. This run used the current Debug build on the host described above.

The additions read an empty file through p9ufs, plan9port and each of the three Linux dialects. The diod case writes nonempty content, calls the CLI's empty write, then checks both read output and the independently reported size are zero. This tests truncating-open behavior; it does not claim the CLI sends a zero-count Twrite. The direct wire count-zero cases are in ContentBoundaryTests and JsonFsBoundaryTests.

Reproduce with the peer environment variables described above and:

```sh
dotnet test --project tests/NineP.Client.Tests -f net10.0 --no-build -- --filter-class NineP.Client.Tests.Compat.InteropTests
```

An additional exploratory `: > name` probe against `jsonfs --writable` through Linux did **not** pass: this kernel reported Permission denied for 9P2000.u/L and Operation not supported for 9P2000. No successful Linux truncation claim is made, and no extra production fix was included in the approved test-audit change. These three probes are outside the six passing cases. The existing read-only refusal assertions remain intact; an EXIT trap now unmounts the test mount even when a command fails.
