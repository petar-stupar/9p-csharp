using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.Server.Internal;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Chaos;

[Trait("Category", "Chaos")]
public sealed class ServerTransportFaultTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APartialHeaderCloseOrStallAffectsOnlyItsConnection(bool stall)
    {
        FaultScript script = stall ? new() { StallAfterBytes = 2 } : new() { CloseAfterBytes = 6 };
        int accepted = 0;
        FaultyTransport transport = new(new MemoryTransport(), server: () => Interlocked.Increment(ref accepted) == 1 ? script : new FaultScript());
        NinePAddress address = new(NinePScheme.Memory, "fault-" + Guid.NewGuid().ToString("N"), 0, "");
        await using NinePServer server = new(new ServerOptions
        {
            Listen = [address],
            Transports = [transport],
            Limits = Limits.Default with { ReadHeaderTimeout = TimeSpan.FromMilliseconds(300) }
        });
        Task serving = server.ServeAsync(new MemoryFilesystem(), Ct);
        await server.Listening.WaitAsync(Ct);
        await using (FakeNinePServer hostile = FakeNinePServer.Wrap(await transport.ConnectAsync(address, Ct)))
        {
            await hostile.WriteAsync(new Tversion(Constants.NOTAG, 8192, Constants.Version9P2000L), Ct);
            Assert.Empty(await hostile.ReadFrameAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct));
        }
        await using (NinePSession healthy = await NinePClient.ConnectAsync(transport, address, new ClientOptions { Uname = "glenda" }, Ct))
        {
            await healthy.AttachAsync(Ct);
            Assert.Equal(FileKind.Directory, (await healthy.GetAttrAsync("/", Ct)).Kind);
        }
        await server.DisposeAsync();
        await serving.WaitAsync(Ct);
        Assert.Contains(stall ? "Stall" : "CloseAfterBytes", script.Trace);
        Assert.Equal(stall ? 2 : 6, script.BytesRead);
    }

    [Fact]
    public async Task ADuplicateRequestIsRejectedWhileTheOriginalRemainsPending()
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        MemoryFile file = (MemoryFile)h.Tree.Root.Children["hello.txt"];
        file.ReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FaultScript script = new() { Writes = [new(FrameFaultKind.Duplicate, MessageType.Tread)] };
        await using FakeNinePServer wire = FakeNinePServer.Wrap(FaultyTransport.Wrap(await h.DialAsync(), script));
        await wire.WriteAsync(new Tversion(Constants.NOTAG, 8192, Constants.Version9P2000L), Ct);
        await wire.ReadAsync<Rversion>(Ct);
        wire.Dialect = Dialect.P9_2000_L;
        await wire.WriteAsync(new Tattach(1, 1, Constants.NOFID, "glenda", "", Constants.NONUNAME), Ct);
        await wire.ReadAsync<Rattach>(Ct);
        await wire.WriteAsync(new Twalk(2, 1, 2, ["hello.txt"]), Ct);
        await wire.ReadAsync<Rwalk>(Ct);
        await wire.WriteAsync(new Tlopen(3, 2, 0), Ct);
        await wire.ReadAsync<Rlopen>(Ct);
        try
        {
            await wire.WriteAsync(new Tread(4, 2, 0, 64), Ct);
            Rlerror refusal = await wire.ReadAsync<Rlerror>(Ct);
            Assert.Equal((ushort)4, refusal.Tag);
            Assert.Equal(NinePError.FromEname("duplicate tag").Errno, refusal.Ecode);
        }
        finally
        {
            file.ReadGate.SetResult();
        }
        Assert.NotEmpty((await wire.ReadAsync<Rread>(Ct)).Data.ToArray());
        Assert.Contains("Duplicate", script.Trace);
    }

    [Fact]
    public async Task ADelayedWriterNeverExceedsTheReplyChannelBound()
    {
        const int capacity = 4;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FaultScript script = new() { Writes = [new(FrameFaultKind.Delay, MessageType.Rclunk, Gate: release.Task)] };
        var (client, server) = MemoryTransport.CreatePair();
        await using FakeNinePServer reader = FakeNinePServer.Wrap(client);
        ServerOptions options = new() { Listen = [new NinePAddress(NinePScheme.Memory, "queue", 0, "")], Limits = Limits.Default with { MaxInFlightPerConnection = capacity, FlushReservePerConnection = 1 } };
        await using ServerSession session = new(FaultyTransport.Wrap(server, script), options, new MemoryFilesystem(), new ServerMetrics(), new OpenState());
        Task running = session.RunAsync(Ct);
        await session.EnqueueAsync(new Rclunk(0), Dialect.P9_2000);
        while (!script.Trace.Contains("Delay", StringComparer.Ordinal))
        {
            Ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        for (ushort tag = 1; tag <= capacity; tag++)
        {
            await session.EnqueueAsync(new Rclunk(tag), Dialect.P9_2000);
        }
        Assert.Equal(capacity, session.QueuedReplies);
        Task blocked = session.EnqueueAsync(new Rclunk(capacity + 1), Dialect.P9_2000).AsTask();
        Assert.False(blocked.IsCompleted);
        Assert.Equal(capacity, session.QueuedReplies);
        release.SetResult();
        await blocked.WaitAsync(Ct);
        for (ushort tag = 0; tag < capacity + 2; tag++)
        {
            Assert.Equal(tag, (await reader.ReadAsync<Rclunk>(Ct)).Tag);
        }
        Assert.Equal(0, session.QueuedReplies);
        await reader.DisposeAsync();
        await running.WaitAsync(Ct);
    }
}
