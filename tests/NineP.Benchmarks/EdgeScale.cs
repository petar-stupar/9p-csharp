using System.Diagnostics;
using System.Globalization;
using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Transports;
using NineP.Server;
using NineP.TestSupport;

namespace NineP.Benchmarks;

/// <summary>Bounded scale acceptance runs, separate from throughput measurements.</summary>
internal static class EdgeScale
{
    private const long MiB = 1024 * 1024;
    private const int Window = 4;
    private const long EntryAllowance = 1024;

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args[0] == "edge-serve")
        {
            return await ServeAsync(args);
        }
        string kind = args[1];
        bool full = args.Length > 2 && args[2] == "full";
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(full ? 10 : 2));
        if (kind == "create")
        {
            await CreateAsync(full ? 1_000_000 : 10_000, deadline.Token);
        }
        else
        {
            foreach (Dialect dialect in kind == "file" ? new[] { Dialect.P9_2000_L, Dialect.P9_2000 } : new[] { Dialect.P9_2000_L })
            {
                uint msize = dialect == Dialect.P9_2000_L ? 1048576u : 65536u;
                ulong length = full ? (1ul << 32) + 1048576 : 8ul * 1048576;
                int entries = full ? 1_000_000 : 10_000;
                await StreamAsync(kind, dialect, msize, length, entries, deadline.Token);
            }
        }
        return 0;
    }

    private static async Task<int> ServeAsync(string[] args)
    {
        uint msize = uint.Parse(args[2], CultureInfo.InvariantCulture);
        ulong length = ulong.Parse(args[3], CultureInfo.InvariantCulture);
        int entries = int.Parse(args[4], CultureInfo.InvariantCulture);
        GeneratedFilesystem fs = new(length, entries);
        await using NinePServer server = new(new ServerOptions
        {
            Listen = [NinePAddress.Parse("tcp://127.0.0.1:0")],
            Limits = Limits.Default with { MaxMsize = msize },
        });
        using CancellationTokenSource stopping = new();
        Task serving = server.ServeAsync(fs, stopping.Token);
        await server.Listening;
        await Console.Out.WriteLineAsync(server.Endpoints[0].ToString());
        await Console.Out.FlushAsync();
        while (await Console.In.ReadLineAsync() is string line)
        {
            if (line == "measure")
            {
                long retained = GC.GetTotalMemory(forceFullCollection: true);
                await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{RUsage.PeakBytes()} {retained} {fs.Root.LargestPage}"));
                await Console.Out.FlushAsync();
            }
        }
        await stopping.CancelAsync();
        try { await serving; }
        catch (OperationCanceledException) { }
        return 0;
    }

    private static async Task StreamAsync(string kind, Dialect dialect, uint msize, ulong length, int entries, CancellationToken ct)
    {
        ProcessStartInfo info = StartInfo("edge-serve", kind, msize.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture), entries.ToString(CultureInfo.InvariantCulture));
        using Process server = Process.Start(info) ?? throw new InvalidOperationException("scale server did not start");
        Task<string> errors = server.StandardError.ReadToEndAsync(ct);
        using CancellationTokenSource monitorStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // The finally awaits the monitor before disposing either the token source or process.
#pragma warning disable CA2025
        Task monitor = MonitorAsync(server, monitorStop.Token);
#pragma warning restore CA2025
        try
        {
            string address = await server.StandardOutput.ReadLineAsync(ct) ?? throw new InvalidOperationException("scale server did not bind: " + await errors);
            await using NinePSession s = await NinePClient.ConnectAsync(NinePAddress.Parse(address), new ClientOptions { Dialects = [dialect], Msize = msize, Uname = "glenda" }, ct);
            await s.AttachAsync(ct);
            // Warm the same code paths and buffer sizes before establishing process baselines.
            await WorkAsync(s, kind, Math.Min(length, 256ul * 1048576), Math.Min(entries, 10000), stopEarly: true, ct);
            long clientBaseline = RUsage.PeakBytes();
            (long serverBaseline, long retainedBaseline, _) = await MeasureAsync(server, ct);
            Stopwatch watch = Stopwatch.StartNew();
            await WorkAsync(s, kind, length, entries, stopEarly: false, ct);
            (long serverPeak, long retained, int page) = await MeasureAsync(server, ct);
            long clientGrowth = Math.Max(0, RUsage.PeakBytes() - clientBaseline);
            long serverGrowth = Math.Max(0, serverPeak - serverBaseline);
            long retainedGrowth = Math.Max(0, retained - retainedBaseline);
            Require(clientGrowth < 64 * MiB, $"client RSS grew {clientGrowth} bytes");
            Require(retainedGrowth < 8L * msize, $"retained server memory grew {retainedGrowth} bytes");
            Require(page <= 64, $"handler generated {page} entries in a page");
            await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"PASS {kind} dialect={dialect} bytes={length} entries={entries} seconds={watch.Elapsed.TotalSeconds:F3} client_rss_growth={clientGrowth} server_rss_growth={serverGrowth} server_retained_growth={retainedGrowth}"));
            await s.DisposeAsync();
            server.StandardInput.Close();
            await server.WaitForExitAsync(ct);
            Require(server.ExitCode == 0, await errors);
        }
        finally
        {
            await monitorStop.CancelAsync();
            try
            {
                try { await monitor; }
                catch (OperationCanceledException) { }
            }
            finally
            {
                if (!server.HasExited)
                {
                    server.Kill(entireProcessTree: true);
                }
                await server.WaitForExitAsync(CancellationToken.None);
                try { await errors; }
                catch (OperationCanceledException) { }
            }
        }
    }

    private static async Task WorkAsync(NinePSession s, string kind, ulong length, int entries, bool stopEarly, CancellationToken ct)
    {
        if (kind == "directory")
        {
            await using NinePFid directory = await s.OpenFileAsync("/", OpenMode.Read, OpenFlags.None, ct);
            int count = 0;
            await foreach (DirEntry entry in directory.ReadDirAsync(ct))
            {
                Require(entry.Name == "e" + count.ToString("D7", CultureInfo.InvariantCulture), "directory skipped, duplicated or reordered an entry");
                Require(entry.Cursor == (ulong)count + 1, "wrong directory cookie");
                count++;
                Require(count <= entries, "directory did not terminate");
                if (stopEarly && count == entries)
                {
                    break;
                }
            }
            Require(count == entries, "directory ended early");
            return;
        }
        await using NinePFid file = await s.OpenFileAsync("data", OpenMode.Read, OpenFlags.None, ct);
        byte[][] buffers = Enumerable.Range(0, Window).Select(_ => new byte[file.Iounit]).ToArray();
        ulong offset = 0;
        while (offset < length)
        {
            List<Task> pending = [];
            for (int i = 0; i < Window && offset < length; i++)
            {
                int count = (int)Math.Min((ulong)buffers[i].Length, length - offset);
                pending.Add(ReadAsync(offset, buffers[i].AsMemory(0, count)));
                offset += (ulong)count;
            }
            await Task.WhenAll(pending);
        }
        if (!stopEarly)
        {
            Require(await file.ReadAsync(length, buffers[0], ct) == 0, "missing EOF");
        }

        async Task ReadAsync(ulong at, Memory<byte> destination)
        {
            int count = await file.ReadAsync(at, destination, ct);
            Require(count == destination.Length, "short synthetic read");
            Require(OffsetPattern.Matches(at, destination.Span), $"wrong content at {at}");
        }
    }

    private static async Task CreateAsync(int count, CancellationToken ct)
    {
        MemoryTransport transport = new();
        NinePAddress address = new(NinePScheme.Memory, "scale", 0, string.Empty);
        MemoryFilesystem tree = new();
        await using NinePServer server = new(new ServerOptions
        {
            Listen = [address],
            Transports = [transport],
            Limits = Limits.Default with { MaxFidsPerConnection = Window + 1 },
        });
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task serving = server.ServeAsync(tree, stopping.Token);
        await server.Listening;
        try
        {
            await using NinePSession s = await NinePClient.ConnectAsync(transport, address, new ClientOptions { Uname = "glenda", Msize = 4096 }, ct);
            await s.AttachAsync(ct);
            long baseline = RUsage.PeakBytes();
            Stopwatch watch = Stopwatch.StartNew();
            for (int at = 0; at < count; at += Window)
            {
                List<Task> pending = [];
                for (int i = at; i < Math.Min(count, at + Window); i++)
                {
                    pending.Add(CreateOne(i));
                }

                await Task.WhenAll(pending);
                Require(s.LiveFids == 1, "create leaked a fid");
                if ((at & 1023) == 0)
                {
                    CheckMemory(null);
                }
            }
            int listed = 0;
            await using (NinePFid directory = await s.OpenFileAsync("/", OpenMode.Read, OpenFlags.None, ct))
            {
                await foreach (DirEntry ignored in directory.ReadDirAsync(ct))
                {
                    _ = ignored;
                    listed++;
                    Require(listed <= count, "created directory did not terminate");
                }
            }
            Require(listed == count, "created directory lost entries");
            long growth = Math.Max(0, RUsage.PeakBytes() - baseline);
            Require(growth <= (count * EntryAllowance) + (32 * MiB), "create memory exceeded predeclared per-entry allowance");
            await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"PASS create entries={count} seconds={watch.Elapsed.TotalSeconds:F3} rss_growth={growth} bytes_per_entry={(double)growth / count:F1} allowance={EntryAllowance}"));

            async Task CreateOne(int index)
            {
                for (int retry = 0; retry < 100; retry++)
                {
                    try
                    {
                        await using NinePFid fid = await s.CreateFileAsync("e" + index.ToString("D7", CultureInfo.InvariantCulture), 0x1A4, ct);
                        return;
                    }
                    catch (NinePException failure) when (failure.Error.Errno == Errno.EAGAIN)
                    {
                        await Task.Yield();
                    }
                }
                throw new InvalidOperationException("EAGAIN retries exhausted");
            }
        }
        finally
        {
            await stopping.CancelAsync();
            try { await serving; }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<(long Peak, long Retained, int Page)> MeasureAsync(Process server, CancellationToken ct)
    {
        await server.StandardInput.WriteLineAsync("measure");
        await server.StandardInput.FlushAsync(ct);
        string line = await server.StandardOutput.ReadLineAsync(ct) ?? throw new InvalidOperationException("missing server memory measurement");
        string[] fields = line.Split(' ');
        return (long.Parse(fields[0], CultureInfo.InvariantCulture), long.Parse(fields[1], CultureInfo.InvariantCulture), int.Parse(fields[2], CultureInfo.InvariantCulture));
    }

    private static async Task MonitorAsync(Process server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            CheckMemory(server);
            await Task.Delay(100, ct);
        }
    }

    private static void CheckMemory(Process? server)
    {
        using Process self = Process.GetCurrentProcess();
        self.Refresh();
        long bytes = self.WorkingSet64;
        if (server is not null && !server.HasExited)
        {
            server.Refresh();
            bytes += server.WorkingSet64;
        }
        long maximum = long.TryParse(Environment.GetEnvironmentVariable("NINEP_SCALE_MEMORY_MIB"), out long configured) ? configured * MiB : 512 * MiB;
        if (bytes > maximum)
        {
            if (server is not null && !server.HasExited)
            {
                server.Kill(entireProcessTree: true);
            }

            throw new InvalidOperationException($"scale process memory {bytes} exceeded fixed budget {maximum}");
        }
    }

    internal static ProcessStartInfo StartInfo(params string[] args)
    {
        ProcessStartInfo info = new("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(typeof(EdgeScale).Assembly.Location);
        foreach (string argument in args)
        {
            info.ArgumentList.Add(argument);
        }
        // Fail inside this child rather than allowing a regression to consume the runner's RAM.
        info.Environment["DOTNET_GCHeapHardLimit"] = Environment.GetEnvironmentVariable("NINEP_SCALE_HEAP_LIMIT") ?? "0x08000000";
        return info;
    }

    private static void Require(bool condition, string error)
    {
        if (!condition)
        {
            throw new InvalidOperationException(error);
        }
    }
}
