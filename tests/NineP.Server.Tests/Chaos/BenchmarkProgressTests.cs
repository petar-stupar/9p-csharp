using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
#if NET10_0_OR_GREATER
using System.Reflection;
using NineP.Benchmarks;
using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Chaos;

/// <summary>The measured workload may only succeed when every requested byte was transferred.</summary>
[Trait("Category", "Chaos")]
public sealed class BenchmarkProgressTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 99)]
    [InlineData(true, 0)]
    [InlineData(true, 99)]
    public async Task ThroughputMustNotReportSuccessWhenTransferIsIncomplete(bool writing, int completed)
    {
        await VerifyTransferAsync(writing, [completed], succeeds: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedProgressBatchFails(bool writing) =>
        await VerifyTransferAsync(writing, [100, 50], succeeds: false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactTransferSucceeds(bool writing) =>
        await VerifyTransferAsync(writing, [100, 100], succeeds: true);

    [Fact]
    public async Task ThroughputMustNotReportSuccessWhenZeroRequestedBytesWereTransferred() =>
        await VerifyTransferAsync(writing: false, [0], succeeds: false);

    [Theory]
    [InlineData(2ul)]
    [InlineData(3ul)]
    [InlineData(4ul)]
    public async Task IndependentServerWriteTotalMustMatchBeforeBenchmarkCanReport(ulong expectedWritten)
    {
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(15));
        CancellationToken ct = budget.Token;
        Type childType = typeof(CodecBenchmarks).Assembly.GetType("NineP.Benchmarks.BenchmarkChild")!;
        MethodInfo start = childType.GetMethod("StartAsync", BindingFlags.Public | BindingFlags.Static)!;
        Task starting = (Task)start.Invoke(null, [8192u, 8ul])!;
        await starting.WaitAsync(ct);
        object child = starting.GetType().GetProperty("Result")!.GetValue(starting)!;
        await using IAsyncDisposable lifetime = Assert.IsAssignableFrom<IAsyncDisposable>(child);
        NinePAddress address = Assert.IsType<NinePAddress>(childType.GetProperty("Address")!.GetValue(child));

        await using (NinePSession session = await NinePClient.ConnectAsync(address,
            new ClientOptions { Dialects = [Dialect.P9_2000_L], Msize = 8192, Uname = "bench" }, ct))
        {
            await session.AttachAsync(ct);
            await using NinePFid file = await session.OpenFileAsync("stream", OpenMode.Write, cancellationToken: ct);
            // Independent accounting counts bytes delivered, including overlapping writes. The
            // total is three, while the file's declared length is eight and written extent is two.
            Assert.Equal(2, await file.WriteAsync(0, new byte[] { 1, 2 }, ct));
            Assert.Equal(1, await file.WriteAsync(1, new byte[] { 3 }, ct));
        }

        // ThroughputAsync awaits this gate before printing either throughput row; a mismatched
        // server total must fail before a successful measurement is returned for reporting.
        MethodInfo stop = childType.GetMethod("StopAsync", BindingFlags.Public | BindingFlags.Instance)!;
        Task<long> stopped = (Task<long>)stop.Invoke(child, [expectedWritten])!;
        if (expectedWritten == 3)
        {
            Assert.True(await stopped.WaitAsync(ct) > 0);
        }
        else
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await stopped.WaitAsync(ct));
            Assert.Contains("did not receive exactly the requested write bytes", failure.Message, StringComparison.Ordinal);
        }
    }

    private static async Task VerifyTransferAsync(bool writing, int[] completed, bool succeeds)
    {
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(10));
        CancellationToken ct = budget.Token;
        (INinePConnection wire, FakeNinePServer peer) = FakeNinePServer.CreatePair();
        await using FakeNinePServer server = peer;
        Task<NinePSession> connecting = NinePClient.ConnectAsync(wire,
            new ClientOptions { Dialects = [Dialect.P9_2000_L], Msize = 8192 }, ct).AsTask();
        await server.NegotiateAsync(Constants.Version9P2000L, cancellationToken: ct);
        await using NinePSession session = await connecting;
        await using NinePFid fid = new(session, 1, new Qid(QidType.QTFILE, 0, 1));
        fid.MarkOpened(fid.Qid, 100);
        Type driver = typeof(CodecBenchmarks).Assembly.GetType("NineP.Benchmarks.BenchmarkDriver")!;
        MethodInfo transfer = driver.GetMethod("TransferAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        Task<double> measured = (Task<double>)transfer.Invoke(null, [fid, (ulong)(100 * completed.Length), completed.Length, writing])!;
        for (int i = 0; i < completed.Length; i++)
        {
            if (writing)
            {
                Twrite request = await server.ReadAsync<Twrite>(ct);
                Assert.Equal(100, request.Data.Length);
                await server.WriteAsync(new Rwrite(request.Tag, (uint)completed[i]), ct);
            }
            else
            {
                Tread request = await server.ReadAsync<Tread>(ct);
                Assert.Equal(100u, request.Count);
                await server.WriteAsync(new Rread(request.Tag, new byte[completed[i]]), ct);
            }
        }

        if (succeeds)
        {
            Assert.True(await measured.WaitAsync(ct) >= 0);
        }
        else
        {
            IOException error = await Assert.ThrowsAsync<IOException>(async () => await measured.WaitAsync(ct));
            Assert.Contains("incomplete", error.Message, StringComparison.Ordinal);
        }

        await server.DisposeAsync();
    }
}
#endif
