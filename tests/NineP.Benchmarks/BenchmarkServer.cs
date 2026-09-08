using System.Globalization;
using NineP.Protocol;
using NineP.Protocol.Transports;
using NineP.Server;

namespace NineP.Benchmarks;

/// <summary>
/// The <c>serve</c> mode: one shipped <see cref="NinePServer"/> over the synthetic tree, on a
/// loopback TCP port the kernel chooses. It runs in its own process so that benchmark (c) —
/// peak RSS of <b>the server</b> under benchmark (a) — measures the server and not the driver.
/// </summary>
internal static class BenchmarkServer
{
    /// <summary>
    /// Serves until standard input closes, then prints its own peak resident set size.
    /// </summary>
    /// <param name="msize">The largest msize this server will negotiate.</param>
    /// <param name="length">The length of <c>/stream</c>.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(uint msize, ulong length)
    {
        NinePAddress requested = NinePAddress.Parse("tcp://127.0.0.1:0");
        ServerOptions options = new()
        {
            Listen = [requested],
            Limits = Limits.Default with { MaxMsize = msize },
        };

        await using NinePServer server = new(options);
        SyntheticFilesystem tree = new(length);

        using CancellationTokenSource stopping = new();
        Task serving = server.ServeAsync(tree, stopping.Token);
        await server.Listening.ConfigureAwait(false);

        await Console.Out.WriteLineAsync(string.Format(
            CultureInfo.InvariantCulture, "listening {0}", server.Endpoints[0]));
        await Console.Out.FlushAsync().ConfigureAwait(false);

        // The driver closes this process's standard input when it has finished measuring.
        await Console.In.ReadToEndAsync().ConfigureAwait(false);

        await stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await serving.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The accept loop was stopped on purpose.
        }

        await Console.Out.WriteLineAsync(string.Format(
            CultureInfo.InvariantCulture, "maxrss {0}", RUsage.PeakBytes()));

        await Console.Out.WriteLineAsync(string.Format(
            CultureInfo.InvariantCulture, "written {0}", tree.BytesWritten));
        return 0;
    }
}
