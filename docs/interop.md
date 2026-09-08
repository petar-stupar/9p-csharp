# Interop

What this implementation has been run against, in both directions, with the exact command and the
observed result. A row that was not run says so and says why: **interop that was not run is not
interop that passed.**

**Merged languages before this one: none yet (ticket 001 is the first).** The cross-language matrix
of ARCHITECTURE.md §10.4 therefore has no rows other than the external reference peers below, and
this repository is the peer the next language will be measured against.

## The machine these runs were made on

| | |
| --- | --- |
| Host | Apple M4 Pro, macOS 26.6.2 (25G83), Darwin 25.6.0, arm64 |
| This implementation | `9p-csharp` at `0.1.0`, .NET SDK 10.0.103, `Debug` build |
| Date | 2026-09-06 |

## External reference peers

| peer | version | role | dialect | transport | result | command | notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| hugelgupf/p9 `p9ufs` | v0.4.1, built with go1.26.0 | server | 9P2000.L | tcp | **pass** | `p9ufs -root <tree> 127.0.0.1:5798` then our cli's `ls` / `cat` recursion over the tree | Part A step 3 composed byte-identical to `sample.expected.txt`; needed the dirent-type accommodation below |
| hugelgupf/p9 `p9ufs` | v0.4.1 | server | 9P2000.L | tcp | **pass** | the `Tfsync` probe described below | our 15-byte `Tfsync` is accepted, answered `Rfsync`, and the connection stays in sync |
| plan9port `9p` | git `b6564bd9`, 2026-08-26, built from source | client | 9P2000 | tcp | **pass** | `9p -a 'tcp!127.0.0.1!5810' ls -l /` and `9p -a … read /name` against our `jsonfs` | found the directory stat-record framing bug below |
| plan9port `9p` | git `b6564bd9` | client | 9P2000 | tcp | **not run: `brew install plan9port` no longer resolves** | `brew install plan9port` → `No available formula with the name "plan9port"` | the formula has been removed from Homebrew core; the row above is the same peer built from source with `git clone https://github.com/9fans/plan9port && ./INSTALL -b`, which is what was actually measured |
| diod | — | server | 9P2000.L | tcp | **not run: Linux only** | — | ARCHITECTURE.md's Decision Log of 2026-09-05 records diod and v9fs as Linux-only, reachable from this machine only through Docker |
| Linux v9fs | — | client (kernel) | 9P2000.L | tcp / virtio | **not run: Linux only** | — | as above; a v9fs mount needs a Linux kernel |

## The two runs, in full

### Our client against `p9ufs`

The tree is `sample.json` materialised on disk by the workspace's own generator, which is what the
fixture's "External peers" section prescribes:

```text
node docs/9p/fixtures/gen-conformance-tree.mjs /tmp/p9tree      # materialised 20 files
GOBIN=$PWD/gobin go install github.com/hugelgupf/p9/cmd/p9ufs@latest
./gobin/p9ufs -root /tmp/p9tree 127.0.0.1:5798
dotnet run --project examples/NineP.Cli -- --addr tcp://127.0.0.1:5798 --dialect 9P2000.L ls /
dotnet run --project examples/NineP.Cli -- --addr tcp://127.0.0.1:5798 --dialect 9P2000.L cat /name
```

Composing Part A step 3's listing — the same recursion the conformance driver performs, `ls` and
`cat` in document order with sizes and SHA-256 digests — produced output **identical to
`docs/9p/fixtures/sample.expected.txt`**, `emptyarr/` and `emptyobj/` included: the generator does
create them, so the fixture's escape clause was not needed.

### plan9port's `9p` against our `jsonfs`

```text
git clone --depth 1 https://github.com/9fans/plan9port.git && (cd plan9port && ./INSTALL -b)
dotnet run --project examples/NineP.JsonFs -- --listen tcp://127.0.0.1:5810 \
    --file docs/9p/fixtures/sample.json --dialects 9P2000
PLAN9=<clone> $PLAN9/bin/9p -a 'tcp!127.0.0.1!5810' ls -l /
PLAN9=<clone> $PLAN9/bin/9p -a 'tcp!127.0.0.1!5810' read /name
PLAN9=<clone> $PLAN9/bin/9p -a 'tcp!127.0.0.1!5810' stat /dir
```

```text
--rw-r--r-- M 0 pstupar pstupar 10 Sep  6 10:20 'big number'
d-rwxr-xr-x M 0 pstupar pstupar  0 Sep  6 10:20 dir
--rw-r--r-- M 0 pstupar pstupar  0 Sep  6 10:20 empty
d-rwxr-xr-x M 0 pstupar pstupar  0 Sep  6 10:20 emptyarr
d-rwxr-xr-x M 0 pstupar pstupar  0 Sep  6 10:20 emptyobj
--rw-r--r-- M 0 pstupar pstupar  4 Sep  6 10:20 enabled
--rw-r--r-- M 0 pstupar pstupar 10 Sep  6 10:20 greeting
d-rwxr-xr-x M 0 pstupar pstupar  0 Sep  6 10:20 list
--rw-r--r-- M 0 pstupar pstupar 11 Sep  6 10:20 name
--rw-r--r-- M 0 pstupar pstupar  2 Sep  6 10:20 negative
--rw-r--r-- M 0 pstupar pstupar  0 Sep  6 10:20 nothing
--rw-r--r-- M 0 pstupar pstupar  3 Sep  6 10:20 ratio
--rw-r--r-- M 0 pstupar pstupar 22 Sep  6 10:20 unicode
--rw-r--r-- M 0 pstupar pstupar  1 Sep  6 10:20 version
```

`read /name` printed `conformance`, and `stat /dir` reported `q (…) d` — a directory — with mode
`020000000755`. Names, kinds, sizes and bytes all agree with `sample.json`.

## What these runs found

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
