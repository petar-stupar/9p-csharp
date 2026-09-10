# Benchmarks

The four measurements of [ARCHITECTURE.md §9](9p/ARCHITECTURE.md), taken on this machine with the
commands below. **Every number here was measured, not estimated**; each section carries the command
that produced it, so any of them can be re-run and disagreed with.

`BenchmarkDocTests` reads this document and fails the build if a section loses its command, its
number or, for peak RSS, its cross-check.

## Machine and runtime

| | |
| --- | --- |
| CPU | Apple M4 Pro, 1 socket, 12 physical and 12 logical cores (`sysctl -n machdep.cpu.brand_string`) |
| Memory | 24 GiB (`sysctl -n hw.memsize` → 25769803776) |
| OS | macOS 26.6.2, build 25G83, Darwin 25.6.0, arm64 |
| SDK | .NET SDK 10.0.103; runtime .NET 10.0.3 (10.0.326.7603), Arm64 RyuJIT armv8.0-a |
| TFM | `net10.0` (the benchmark project is single-target; the packages themselves also build for `net8.0`) |
| Configuration | `Release` |
| GC | workstation, concurrent — the SDK defaults; no project file or `runtimeconfig.json` in this repository sets `ServerGarbageCollection` or `ConcurrentGarbageCollection` |
| Transport | loopback TCP (`tcp://127.0.0.1:0`), server and client in **separate processes** |
| Date | 2026-09-06, at commit `0.1.0`; sections (a) and (c) re-measured 2026-09-07 after the `Rread` path stopped allocating its budget; all four sections re-measured 2026-09-08 after IR-9 (the projection-honesty fixes), with no row moving beyond run-to-run spread |

The server under (a), (b) and (c) is the shipped `NinePServer` over a synthetic tree of one file,
`/stream`, whose reads produce zeros and whose writes are counted and discarded
(`tests/NineP.Benchmarks/SyntheticFilesystem.cs`). That is deliberate: benchmark (a) is about the
framing, the transport and the client's in-flight window, and a handler that kept a gibibyte in a
`byte[]` would measure the array — and would put that gibibyte into the peak RSS of benchmark (c).

## (a) sequential read and write of 1 GiB over loopback TCP

Full gibibyte in each direction, once per dialect, with the client's default in-flight window of 4
and chunks of `iounit` (`msize − IOHDRSZ`).

```text
dotnet run -c Release --project tests/NineP.Benchmarks -- throughput --dialect 9P2000.L --msize 1048576 --bytes 1073741824
dotnet run -c Release --project tests/NineP.Benchmarks -- throughput --dialect 9P2000   --msize 65536   --bytes 1073741824
```

| Dialect | msize | Direction | Elapsed | Throughput |
| --- | --- | --- | --- | --- |
| 9P2000.L | 1 MiB | read | 0.677 s | **1513.6 MiB/s** |
| 9P2000.L | 1 MiB | write | 0.600 s | **1707.3 MiB/s** |
| 9P2000 | 64 KiB | read | 0.849 s | **1205.6 MiB/s** |
| 9P2000 | 64 KiB | write | 0.927 s | **1104.5 MiB/s** |

The gap between the two msizes is the round-trip count: a gibibyte is 1024 chunks at 1 MiB and
16 384 at 64 KiB, and the window of 4 hides only so much of that.

**What this row cannot see.** Every read here fills its buffer, so the size of that buffer never
mattered to it: until 2026-09-07 the server allocated a fresh `byte[]` of `min(count, msize −
IOHDRSZ)` for **every** `Tread`, whatever the file's size, and a client that asks for a whole
`iounit` on a ten-byte file — the shipped client does, and so does v9fs — paid a 1 MiB allocation
per read. The payload is now a pooled rental clamped to what the file has left to give. Measured in
process by `ReadPathTests.ReadingASmallFileAtALargeMsizeDoesNotAllocateTheMsize`, reading a
ten-byte file at an msize of 1 MiB: **5 813 bytes allocated per read, against 1 054 899 before** —
client and server together, so the server's own share is smaller still. The four rows above moved
by less than the run-to-run spread.

## (b) 100 000 walk + stat + clunk round trips

One `Twalk`, one `Tgetattr` and one `Tclunk` per iteration, strictly one at a time — the window is
1, so this measures latency and not throughput. A thousand untimed iterations precede the measured
run so that the JIT is not in the number.

```text
dotnet run -c Release --project tests/NineP.Benchmarks -- roundtrips --dialect 9P2000.L --msize 1048576 --count 100000
```

| Iterations | Elapsed | Rate | Per iteration | Per 9P round trip |
| --- | --- | --- | --- | --- |
| 100 000 | 17.072 s | **5858 ops/s** | **170.72 µs** | ≈ 57 µs |

Each iteration is three request/reply exchanges over loopback TCP, so the per-round-trip figure is
the third column divided by three.

## (c) peak RSS of the server under (a)

Read with `getrusage(RUSAGE_SELF).ru_maxrss` from inside the **server** process
(`tests/NineP.Benchmarks/RUsage.cs`), which is why the server runs as its own process: the driver's
own gibibyte of buffers is not part of what a server costs. `ru_maxrss` is bytes on macOS and
kibibytes on Linux, and `RUsage.PeakBytes` normalises the two.

`Process.PeakWorkingSet64` is used on Windows only, where it is the kernel's own peak working set
and the nearest thing to `ru_maxrss`. It is **not** used on macOS: it returns 0 there, and a
benchmark that publishes zero bytes of peak memory is worse than one that publishes nothing.
`BenchmarkRssTests.MaxRssIsNonZeroAndAtLeastWorkingSet` is what stops that regressing, on every
platform CI runs.

```text
dotnet run -c Release --project tests/NineP.Benchmarks -- throughput --dialect 9P2000.L --msize 1048576 --bytes 1073741824
dotnet run -c Release --project tests/NineP.Benchmarks -- throughput --dialect 9P2000   --msize 65536   --bytes 1073741824
```

| Dialect | msize | Peak RSS (`getrusage`) |
| --- | --- | --- |
| 9P2000.L | 1 MiB | 76 201 984 bytes (**72.7 MiB**) |
| 9P2000 | 64 KiB | 72 073 216 bytes (**68.7 MiB**) |

**Cross-check.** The same server binary, started under `/usr/bin/time -l` and stopped without
serving anything, reported its own peak from `getrusage` and the kernel's from `time` within
0.2 % of each other:

```text
/usr/bin/time -l dotnet exec tests/NineP.Benchmarks/bin/Release/net10.0/NineP.Benchmarks.dll \
    serve --msize 1048576 --length 1073741824 < /dev/null
```

| Source | Value |
| --- | --- |
| `getrusage(RUSAGE_SELF).ru_maxrss` (printed by the server itself) | 50 692 096 bytes |
| `maximum resident set size` from `/usr/bin/time -l` | 50 790 400 bytes |

The remainder — about 30 MiB between an idle server and one that has moved a gibibyte — is the
pooled frame buffers at an msize of 1 MiB plus the .NET heap the transfer touched; it does not
scale with the size of the transfer, which is the property that matters.

## (d) codec decode and encode, ns/op

`BenchmarkDotNet` 0.15.8, default job. `Twalk` and `Rgetattr` bracket the codec: `Twalk` is a
counted list of strings and allocates, `Rgetattr` is 160 fixed bytes and allocates nothing.

```text
dotnet run -c Release --project tests/NineP.Benchmarks -- codec
```

| Method | Mean | Error | StdDev | Allocated |
| --- | --- | --- | --- | --- |
| `Twalk` decode (4 elements) | **57.82 ns** | 0.201 ns | 0.179 ns | 200 B |
| `Twalk` encode (4 elements) | **37.26 ns** | 0.061 ns | 0.051 ns | 64 B |
| `Rgetattr` decode | **53.44 ns** | 0.067 ns | 0.062 ns | 0 B |
| `Rgetattr` encode | **40.88 ns** | 0.842 ns | 1.602 ns | 0 B |

`Twalk`'s 200 bytes are the `string[]` and its four strings; the fixed-size message allocates
nothing in either direction, which is the zero-allocation claim of ARCHITECTURE.md §9 stated as a
number.

## What these numbers are not

**No target is promised.** These are one machine's figures on one day, and a laptop under load will
produce different ones. What they are for is comparison: a regression of more than **20 %** against
the previous pull request's figures is a review finding, and the commands above are how the
comparison is made.

## Transfer validation

The throughput workload requires a full response for every requested range. Unexpected short or
zero reads/writes fail with direction, offset, requested and completed counts; they never become a
successful row using the original requested byte count. After timing, the driver also checks the
server's independent total of received write bytes before printing either throughput result. Tests
cover zero, short and mixed-progress batches and successful complete transfers. Historical timings
above are historical measurements; correctness fixes require a fresh run before replacing them.

## Additional edge workloads (F10–F12)

Measured on the machine above on 2026-09-08, Release/net10.0. These acceptance workloads
create no large fixture file and never materialize the generated file or lazy directory.
The file oracle encodes the full block offset, so crossing 2³² cannot silently wrap.
Both processes run with two runtime processors to bound per-thread pools, and a 128 MiB
managed heap cap each. A watchdog enforces 512 MiB combined resident memory. A fixed
256 MiB streaming warmup precedes the file measurements; the directory warms 10000 entries.
The checks require client RSS growth below 64 MiB and retained server managed growth below
8 × msize after full GC. Server peak RSS growth is reported separately, including runtime pools.

```sh
DOTNET_PROCESSOR_COUNT=2 DOTNET_GCHeapHardLimit=0x08000000 dotnet tests/NineP.Benchmarks/bin/Release/net10.0/NineP.Benchmarks.dll edge-scale file full
DOTNET_PROCESSOR_COUNT=2 DOTNET_GCHeapHardLimit=0x08000000 dotnet tests/NineP.Benchmarks/bin/Release/net10.0/NineP.Benchmarks.dll edge-scale directory full
```

| Workload | Logical size | Elapsed | Client RSS growth | Server RSS growth | Retained server managed growth |
| --- | --- | --- | --- | --- | --- |
| F10, .L, msize 1 MiB | 4296015872 bytes | 4.260 s | 15597568 bytes | 16859136 bytes | 297192 bytes |
| F10, 9P2000, msize 64 KiB | 4296015872 bytes | 4.409 s | 0 bytes¹ | 10125312 bytes | 139144 bytes |
| F11, .L | 1000000 entries | 0.308 s | 35438592 bytes | 15368192 bytes | 2077200 bytes |

¹ This is the increase in the process high-water mark. The preceding .L run already established
that peak; it does not mean that the client used zero memory.

Full F12 retains real mutable nodes. Its measured RSS growth was **587382784 bytes** for
1000000 creates (587.4 bytes/entry), completing in **13.189 s**. That exceeds the 512 MiB CI
budget, so the full case is explicitly skipped there. CI still runs 10000 real creates, checks
all entries, enforces at most four temporary fids plus the attach fid, and injects ENOSPC in
a separate small regression test. The acceptance allowance is fixed before execution at
1024 bytes/entry plus 32 MiB startup overhead.

```sh
DOTNET_PROCESSOR_COUNT=2 NINEP_SCALE_MEMORY_MIB=1536 DOTNET_GCHeapHardLimit=0x40000000 dotnet tests/NineP.Benchmarks/bin/Release/net10.0/NineP.Benchmarks.dll edge-scale create full
```

The xunit wrappers (`ScaleTests`) set the processor and heap limits, own child-process
cleanup, and report full runs as skipped unless opted in. `NINEP_FULL_SCALE=1` enables F10/F11;
`NINEP_FULL_CREATE_SCALE=1 NINEP_SCALE_MEMORY_MIB=1536 NINEP_SCALE_HEAP_LIMIT=0x40000000`
enables local F12. These are resource ceilings and timeout checks, with no throughput target.
All three full-scale tests are local-only. GitHub Actions runs only the 8 MiB file,
10000-entry directory and 10000-create versions. The full tests explicitly skip whenever
`CI` or `GITHUB_ACTIONS` is true or 1, even if their opt-in variables are also set.
