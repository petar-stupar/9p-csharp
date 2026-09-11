using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.Server;
using NineP.TestSupport;
using Xunit;

namespace NineP.Client.Tests.Chaos;

[Trait("Category", "Chaos")]
public sealed class ClientTransportFaultTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    [Fact]
    public async Task ADroppedReplyTimesOutFlushesAndLeavesTheSessionUsable()
    {
        FaultScript script = new() { Writes = [new(FrameFaultKind.Drop, MessageType.Rclunk)] };
        await using Peer p = await Peer.StartAsync(serverScript: script, timeout: TimeSpan.FromSeconds(1));
        Task<Rclunk> call = p.Session.Messages.ClunkAsync(new Tclunk(0, 1), Ct).AsTask();
        Tclunk request = await p.Server.ReadAsync<Tclunk>(Ct);
        await p.Server.WriteAsync(new Rclunk(request.Tag), Ct);
        Tflush flush = await p.Server.ReadAsync<Tflush>(Ct);
        Assert.Equal(request.Tag, flush.OldTag);
        await p.Server.WriteAsync(new Rflush(flush.Tag), Ct);
        await Assert.ThrowsAsync<TimeoutException>(async () => await call.WaitAsync(Ct));
        await p.SentinelAsync();
        Assert.Contains("Drop", script.Trace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedReplyRespectsTheFlushBoundary(bool afterFlush)
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FaultScript script = new() { Writes = [new(FrameFaultKind.Delay, MessageType.Rclunk, Gate: release.Task)] };
        await using Peer p = await Peer.StartAsync(serverScript: script);
        using CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<Rclunk> call = p.Session.Messages.ClunkAsync(new Tclunk(0, 1), cancelled.Token).AsTask();
        Tclunk request = await p.Server.ReadAsync<Tclunk>(Ct);
        // Allocate the witness before releasing oldtag, so it cannot reuse that number.
        Task<Rclunk> witness = p.Session.Messages.ClunkAsync(new Tclunk(0, 2), Ct).AsTask();
        Tclunk pending = await p.Server.ReadAsync<Tclunk>(Ct);
        Task delayed = p.Server.WriteAsync(new Rclunk(request.Tag), Ct);
        await cancelled.CancelAsync();
        Tflush flush = await p.Server.ReadAsync<Tflush>(Ct);
        Assert.Equal(request.Tag, flush.OldTag);
        if (afterFlush)
        {
            await p.Server.WriteAsync(new Rflush(flush.Tag), Ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call.WaitAsync(Ct));
            release.SetResult();
            await delayed.WaitAsync(Ct);
            NinePProtocolException error = await Assert.ThrowsAsync<NinePProtocolException>(async () => await witness.WaitAsync(Ct));
            Assert.Equal(ProtocolErrorKind.Type, error.Kind);
        }
        else
        {
            release.SetResult();
            await delayed.WaitAsync(Ct);
            await p.Server.WriteAsync(new Rflush(flush.Tag), Ct);
            Assert.Equal(request.Tag, (await call.WaitAsync(Ct)).Tag);
            await p.Server.WriteAsync(new Rclunk(pending.Tag), Ct);
            await witness.WaitAsync(Ct);
            await p.SentinelAsync();
        }
        Assert.Contains("Delay", script.Trace);
    }

    [Fact]
    public async Task SingleByteReadsReassembleTheReply()
    {
        FaultScript script = new() { ReadChunkSize = 1 };
        await using Peer p = await Peer.StartAsync(clientScript: script);
        await p.SentinelAsync();
        Assert.Contains("Split", script.Trace);
        Assert.True(script.BytesRead > 7);
    }

    [Fact]
    public async Task ADuplicateAfterRetirementFailsEveryPendingCaller()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FaultScript script = new() { Writes = [new(FrameFaultKind.Duplicate, MessageType.Rclunk, Gate: release.Task)] };
        await using Peer p = await Peer.StartAsync(serverScript: script);
        Task<Rclunk> call = p.Session.Messages.ClunkAsync(new Tclunk(0, 1), Ct).AsTask();
        Tclunk request = await p.Server.ReadAsync<Tclunk>(Ct);
        Task<Rclunk>[] pending = [p.Session.Messages.ClunkAsync(new Tclunk(0, 2), Ct).AsTask(), p.Session.Messages.ClunkAsync(new Tclunk(0, 3), Ct).AsTask()];
        await p.Server.ReadAsync<Tclunk>(Ct);
        await p.Server.ReadAsync<Tclunk>(Ct);
        Task duplicate = p.Server.WriteAsync(new Rclunk(request.Tag), Ct);
        Assert.Equal(request.Tag, (await call.WaitAsync(Ct)).Tag);
        release.SetResult();
        await duplicate.WaitAsync(Ct);
        foreach (Task<Rclunk> waiting in pending)
        {
            NinePProtocolException error = await Assert.ThrowsAsync<NinePProtocolException>(async () => await waiting.WaitAsync(Ct));
            Assert.Equal(ProtocolErrorKind.Type, error.Kind);
        }
        Assert.Contains("Duplicate", script.Trace);
    }

    [Fact]
    public async Task AMidFrameCloseFailsThePendingCalls()
    {
        // Rversion is 21 bytes; allow only six bytes of the subsequent seven-byte reply.
        FaultScript script = new() { CloseAfterBytes = 27 };
        await using Peer p = await Peer.StartAsync(clientScript: script);
        Task<Rclunk> first = p.Session.Messages.ClunkAsync(new Tclunk(0, 1), Ct).AsTask();
        Task<Rclunk> second = p.Session.Messages.ClunkAsync(new Tclunk(0, 2), Ct).AsTask();
        Tclunk request = await p.Server.ReadAsync<Tclunk>(Ct);
        await p.Server.ReadAsync<Tclunk>(Ct);
        await p.Server.WriteAsync(new Rclunk(request.Tag), Ct);
        await Assert.ThrowsAnyAsync<NinePException>(async () => await first.WaitAsync(Ct));
        await Assert.ThrowsAnyAsync<NinePException>(async () => await second.WaitAsync(Ct));
        Assert.Contains("CloseAfterBytes", script.Trace);
        Assert.Equal(27, script.BytesRead);
    }

    [Fact]
    public async Task AThrottledQuarterMegabyteTransferCompletesWithTheWindow()
    {
        // 1 KiB chunks split every reply; 256 KiB keeps the delay count bounded on the Windows
        // runner, whose timer resolution makes every 1 ms delay cost about 16 ms.
        FaultScript clientScript = new() { ReadChunkSize = 1024, ReadDelay = TimeSpan.FromMilliseconds(1) };
        FaultyTransport transport = new(new MemoryTransport(), () => clientScript);
        NinePAddress address = new(NinePScheme.Memory, "fault-" + Guid.NewGuid().ToString("N"), 0, "");
        MemoryFilesystem tree = new();
        await using NinePServer server = new(new ServerOptions { Listen = [address], Transports = [transport] });
        Task serving = server.ServeAsync(tree, Ct);
        await server.Listening.WaitAsync(Ct);
        await using (NinePSession s = await NinePClient.ConnectAsync(transport, address, new ClientOptions { Uname = "glenda", InFlightWindow = 4, Msize = 8192 }, Ct))
        {
            await s.AttachAsync(Ct);
            byte[] content = new byte[256 * 1024];
            for (int i = 0; i < content.Length; i++)
            {
                content[i] = (byte)(i * 31);
            }
            await using (NinePFid file = await s.CreateFileAsync("data", Perms.P0666, Ct))
            {
                await file.WriteAllAsync(content, Ct);
            }
            Assert.Equal(content, await s.ReadFileAsync("data", Ct));
            Assert.True(server.Counters.MessagesByType[MessageType.Tread] > 1);
        }
        await server.DisposeAsync();
        await serving.WaitAsync(Ct);
        Assert.Contains("Throttle", clientScript.Trace);
        Assert.Contains("Split", clientScript.Trace);
    }

    private sealed class Peer(NinePSession session, FakeNinePServer server) : IAsyncDisposable
    {
        public NinePSession Session { get; } = session;
        public FakeNinePServer Server { get; } = server;
        public static async Task<Peer> StartAsync(FaultScript? clientScript = null, FaultScript? serverScript = null, TimeSpan? timeout = null)
        {
            var (client, server) = MemoryTransport.CreatePair();
            FakeNinePServer peer = FakeNinePServer.Wrap(FaultyTransport.Wrap(server, serverScript ?? new FaultScript()));
            Task<NinePSession> connecting = NinePClient.ConnectAsync(FaultyTransport.Wrap(client, clientScript ?? new FaultScript()),
                new ClientOptions { RequestTimeout = timeout ?? TimeSpan.FromSeconds(10), Dialects = [Dialect.P9_2000_L] }, Ct).AsTask();
            await peer.NegotiateAsync(Constants.Version9P2000L, cancellationToken: Ct);
            return new Peer(await connecting, peer);
        }
        public async Task SentinelAsync()
        {
            Task<Rclunk> call = Session.Messages.ClunkAsync(new Tclunk(0, 777), Ct).AsTask();
            Tclunk request = await Server.ReadAsync<Tclunk>(Ct);
            await Server.WriteAsync(new Rclunk(request.Tag), Ct);
            Assert.Equal(request.Tag, (await call.WaitAsync(Ct)).Tag);
        }
        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Session.DisposeAsync();
        }
    }
}
