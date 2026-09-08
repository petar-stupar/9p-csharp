using System.Diagnostics;
using System.Globalization;
using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Transports;

namespace NineP.Benchmarks;

/// <summary>
/// The client half of benchmarks (a), (b) and (c): it starts a <c>serve</c> child on loopback TCP,
/// drives it, and prints one line per measurement, ready to be pasted into
/// <c>docs/benchmarks.md</c> together with the command that produced it.
/// </summary>
internal static class BenchmarkDriver
{
    /// <summary>Benchmark (a) and (c): a sequential read and write, and the server's peak RSS.</summary>
    /// <param name="dialect">The dialect to negotiate.</param>
    /// <param name="msize">The msize both sides agree on.</param>
    /// <param name="bytes">How many bytes to move in each direction.</param>
    /// <param name="window">Requests kept outstanding during the transfer.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> ThroughputAsync(Dialect dialect, uint msize, ulong bytes, int window)
    {
        await using BenchmarkChild child = await BenchmarkChild.StartAsync(msize, bytes);

        await using NinePSession session = await ConnectAsync(child.Address, dialect, msize, window);
        await using NinePFid root = await session.AttachAsync();
        await using NinePFid file = await root.WalkAsync([SyntheticFile.Name]);
        await file.OpenAsync(OpenMode.ReadWrite);

        double read = await TransferAsync(file, bytes, window, writing: false);
        double written = await TransferAsync(file, bytes, window, writing: true);
        long peak = await child.StopAsync(bytes);
        await ReportAsync("read", dialect, msize, bytes, read);
        await ReportAsync("write", dialect, msize, bytes, written);
        await Console.Out.WriteLineAsync(string.Format(
            CultureInfo.InvariantCulture,
            "maxrss dialect={0} msize={1} bytes={2}: {3} bytes ({4:0.0} MiB)",
            Negotiator(dialect),
            msize,
            bytes,
            peak,
            peak / 1048576.0));

        return 0;
    }

    /// <summary>Benchmark (b): walk + stat + clunk round trips, one at a time.</summary>
    /// <param name="dialect">The dialect to negotiate.</param>
    /// <param name="msize">The msize both sides agree on.</param>
    /// <param name="count">How many round trips to make.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RoundTripsAsync(Dialect dialect, uint msize, int count)
    {
        await using BenchmarkChild child = await BenchmarkChild.StartAsync(msize, 0);

        await using NinePSession session = await ConnectAsync(child.Address, dialect, msize, 1);
        await using NinePFid root = await session.AttachAsync();

        // One untimed pass so the measurement is of the protocol and not of the first JIT.
        await WalkStatClunkAsync(root, 1000);

        long started = Stopwatch.GetTimestamp();
        await WalkStatClunkAsync(root, count);
        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;

        await Console.Out.WriteLineAsync(string.Format(
            CultureInfo.InvariantCulture,
            "roundtrips dialect={0} msize={1} count={2}: {3:0.000} s, {4:0} ops/s, {5:0.00} us/op",
            Negotiator(dialect),
            msize,
            count,
            seconds,
            count / seconds,
            seconds * 1e6 / count));

        await child.StopAsync();
        return 0;
    }

    private static async Task WalkStatClunkAsync(NinePFid root, int count)
    {
        for (int i = 0; i < count; i++)
        {
            NinePFid walked = await root.WalkAsync([SyntheticFile.Name]);
            await using (walked.ConfigureAwait(false))
            {
                await walked.GetAttrAsync();
            }
        }
    }

    /// <summary>Moves <paramref name="bytes"/> in one direction and returns the elapsed seconds.</summary>
    private static async Task<double> TransferAsync(NinePFid file, ulong bytes, int window, bool writing)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        int chunk = file.Iounit;
        byte[][] buffers = new byte[window][];
        for (int i = 0; i < window; i++)
        {
            buffers[i] = new byte[chunk];
        }

        Task<int>[] pending = new Task<int>[window];
        int[] requested = new int[window];
        long started = Stopwatch.GetTimestamp();
        ulong moved = 0;

        while (moved < bytes)
        {
            int batch = 0;
            for (int i = 0; i < window && moved + ((ulong)i * (ulong)chunk) < bytes; i++)
            {
                ulong offset = moved + ((ulong)i * (ulong)chunk);
                int count = (int)Math.Min((ulong)chunk, bytes - offset);
                requested[i] = count;
                pending[i] = writing
                    ? file.WriteAsync(offset, buffers[i].AsMemory(0, count)).AsTask()
                    : file.ReadAsync(offset, buffers[i].AsMemory(0, count)).AsTask();
                batch++;
            }

            int[] done = await Task.WhenAll(pending[..batch]).ConfigureAwait(false);
            for (int i = 0; i < done.Length; i++)
            {
                if (done[i] != requested[i])
                {
                    throw new IOException(string.Format(CultureInfo.InvariantCulture,
                        "incomplete {0} at offset {1}: requested {2}, completed {3}; total requested {4}",
                        writing ? "write" : "read", moved, requested[i], done[i], bytes));
                }

                moved += (ulong)done[i];
            }
        }

        return Stopwatch.GetElapsedTime(started).TotalSeconds;
    }

    private static async Task ReportAsync(
        string direction, Dialect dialect, uint msize, ulong bytes, double seconds)
    {
        double mib = bytes / 1048576.0;
        await Console.Out.WriteLineAsync(string.Format(
            CultureInfo.InvariantCulture,
            "{0} dialect={1} msize={2} bytes={3}: {4:0.000} s, {5:0.0} MiB/s",
            direction,
            Negotiator(dialect),
            msize,
            bytes,
            seconds,
            mib / seconds));
    }

    private static ValueTask<NinePSession> ConnectAsync(
        NinePAddress address, Dialect dialect, uint msize, int window) =>
        NinePClient.ConnectAsync(
            address,
            new ClientOptions
            {
                Dialects = [dialect],
                MinDialect = dialect,
                Msize = msize,
                Uname = "bench",
                InFlightWindow = window,
                Limits = Limits.Default with { MaxMsize = msize },
                RequestTimeout = TimeSpan.FromMinutes(10),
            });

    private static string Negotiator(Dialect dialect) =>
        NineP.Protocol.Negotiation.Negotiator.VersionString(dialect);
}

/// <summary>The <c>serve</c> child process, and the peak RSS it reports when it stops.</summary>
internal sealed class BenchmarkChild : IAsyncDisposable
{
    private readonly Process _process;

    private BenchmarkChild(Process process, NinePAddress address)
    {
        _process = process;
        Address = address;
    }

    /// <summary>The address the child bound.</summary>
    public NinePAddress Address { get; }

    /// <summary>Starts a <c>serve</c> child and waits for its address.</summary>
    /// <param name="msize">The msize it will negotiate up to.</param>
    /// <param name="length">The length of the file it serves.</param>
    /// <returns>The running child.</returns>
    public static async Task<BenchmarkChild> StartAsync(uint msize, ulong length)
    {
        // The child is this very program in serve mode. It is launched through the muxer and this
        // assembly's own path, which works whether this process was started by `dotnet run`, by
        // `dotnet exec`, or through the apphost.
        ProcessStartInfo info = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        info.ArgumentList.Add("exec");
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "NineP.Benchmarks.dll"));
        info.ArgumentList.Add("serve");
        info.ArgumentList.Add("--msize");
        info.ArgumentList.Add(msize.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--length");
        info.ArgumentList.Add(length.ToString(CultureInfo.InvariantCulture));

        Process process = Process.Start(info)
            ?? throw new InvalidOperationException("the benchmark server did not start");

        string? line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
        if (line is null || !line.StartsWith("listening ", StringComparison.Ordinal))
        {
            string complaint = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                "the benchmark server announced no address: " + complaint);
        }

        return new BenchmarkChild(process, NinePAddress.Parse(line.Split(' ')[1]));
    }

    /// <summary>Stops the child and reads the peak RSS it printed.</summary>
    /// <returns>The child's peak resident set size in bytes.</returns>
    public async Task<long> StopAsync(ulong expectedWritten = 0)
    {
        _process.StandardInput.Close();

        string? line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
        await _process.WaitForExitAsync().ConfigureAwait(false);

        if (line is null || !line.StartsWith("maxrss ", StringComparison.Ordinal) || _process.ExitCode != 0)
        {
            throw new InvalidOperationException("the benchmark server reported no peak RSS or failed");
        }

        string? accounting = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
        if (accounting is null || !accounting.StartsWith("written ", StringComparison.Ordinal)
            || ulong.Parse(accounting.Split(' ')[1], CultureInfo.InvariantCulture) != expectedWritten)
        {
            throw new InvalidOperationException("the benchmark server did not receive exactly the requested write bytes");
        }

        return long.Parse(line.Split(' ')[1], CultureInfo.InvariantCulture);
    }

    /// <summary>Kills the child if it is still running.</summary>
    /// <returns>A task that completes when it has gone.</returns>
    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }

        _process.Dispose();
    }
}
