using NineP.Protocol;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using Xunit;

namespace NineP.Client.Tests.Chaos;

[Trait("Category", "Chaos")]
public sealed class FaultyTransportTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    [Fact]
    public async Task DelaysOwnTheirBytesAndUseTheInjectedClock()
    {
        (INinePConnection raw, INinePConnection peer) = MemoryTransport.CreatePair();
        ManualClock clock = new();
        FaultScript script = new() { Clock = clock, Writes = [new(FrameFaultKind.Delay, Delay: TimeSpan.FromHours(1))] };
        await using INinePConnection connection = FaultyTransport.Wrap(raw, script);
        await using (peer)
        {
            byte[] bytes = [7, 0, 0, 0, 121, 1, 0];
            Task write = connection.WriteAsync(bytes, Ct).AsTask();
            Assert.False(write.IsCompleted);
            bytes[5] = 99;
            Assert.Equal(TimeSpan.FromHours(1), clock.DueTime);
            clock.Fire();
            await write;
            byte[] read = new byte[7];
            Assert.Equal(7, await peer.ReadAsync(read, Ct));
            Assert.Equal(1, read[5]);
            Assert.Equal(["Delay"], script.Trace);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposalCancelsStalledReadsAndWrites(bool read)
    {
        (INinePConnection raw, INinePConnection peer) = MemoryTransport.CreatePair();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FaultScript script = new() { StallAfterBytes = 0, Writes = [new(FrameFaultKind.Delay, Gate: gate.Task)] };
        await using INinePConnection connection = FaultyTransport.Wrap(raw, script);
        await using (peer)
        {
            Task pending = read ? connection.ReadAsync(new byte[7], Ct).AsTask()
                : connection.WriteAsync(new byte[] { 7, 0, 0, 0, 121, 1, 0 }, Ct).AsTask();
            Assert.False(pending.IsCompleted);
            await connection.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            Assert.Contains(read ? "Stall" : "Delay", script.Trace);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public TimeSpan DueTime { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            return new Timer();
        }
        public void Fire() => _callback!(_state);
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
