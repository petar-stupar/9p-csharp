using System.Reflection;
using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.Server.Internal;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Regression;

/// <summary>Reader-owned flush state must outlive bounded transport shutdown (review C07/C08).</summary>
[Trait("Category", "Regression")]
public sealed class SessionShutdownRegressionTests
{
    [Fact]
    public async Task ShutdownKeepsFlushGatesAndHandlersAliveUntilInlineReaderUnwinds()
    {
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(15));
        CancellationToken ct = budget.Token;
        MemoryFilesystem tree = new();
        MemoryFile file = tree.Root.Add(tree.NewFile("open", Perms.P0666));
        RecordingLogger logger = new();
        (INinePConnection clientWire, INinePConnection serverWire) = MemoryTransport.CreatePair();
        await using FakeNinePServer client = FakeNinePServer.Wrap(clientWire);
        await using BlockedReplyConnection connection = new(serverWire);
        using SemaphoreSlim listenerBudget = new(4, 4);
        ServerOptions options = new()
        {
            Listen = [],
            Logger = logger,
            Limits = Limits.Default with { MaxInFlightPerConnection = 2, FlushReservePerConnection = 1 },
        };
        await using ServerSession session = new(connection, options, tree, new ServerMetrics(), new OpenState(), listenerBudget);
        Task running = session.RunAsync(ct);
        using ManualResetEventSlim resumeFlush = new(false);
        CancellationTokenRegistration registration = default;
        CancellationTokenSource? pendingSource = null;
        try
        {
            await client.WriteAsync(new Tversion(Constants.NOTAG, 8192, Constants.Version9P2000L), ct);
            await client.ReadAsync<Rversion>(ct);
            client.Dialect = Dialect.P9_2000_L;
            await client.WriteAsync(new Tattach(1, 1, Constants.NOFID, "glenda", string.Empty, Constants.NONUNAME), ct);
            await client.ReadAsync<Rattach>(ct);
            await client.WriteAsync(new Twalk(2, 1, 2, ["open"]), ct);
            await client.ReadAsync<Rwalk>(ct);
            await client.WriteAsync(new Tlopen(3, 2, 0), ct);
            await client.ReadAsync<Rlopen>(ct);
            Assert.Equal(1, file.Opens);

            // Park the writer on a reply while leaving the reader free to process the next flush.
            connection.BlockWrites = true;
            await client.WriteAsync(new Tflush(4, 999), ct);
            await connection.WriteEntered.Task.WaitAsync(ct);

            // A synchronous cancellation callback holds the inline flush inside Tags.Flush, after
            // it acquired both gates. This direct pending-tag hook isolates the reader's lifetime:
            // no worker keeps a general slot held and accidentally masks premature gate cleanup.
            PendingRequest pending = session.Tags.Begin(50, MessageType.Tread);
            pendingSource = pending.Cts;
            TaskCompletionSource flushEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            registration = pendingSource.Token.Register(() =>
            {
                flushEntered.TrySetResult();
                resumeFlush.Wait(ct);
            });
            await client.WriteAsync(new Tflush(51, 50), ct);
            await flushEntered.Task.WaitAsync(ct);
            SemaphoreSlim replyGate = Field<SemaphoreSlim>(session, "_replyGate");
            SemaphoreSlim flushSlots = Field<SemaphoreSlim>(session, "_flushSlots");
            SemaphoreSlim general = Field<SemaphoreSlim>(session, "_general");

            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct);
            Assert.True(connection.Closed.Task.IsCompletedSuccessfully);
            Assert.False(running.IsCompleted);
            Task cleanup = Field<Task>(session, "_cleanup");
            Assert.False(cleanup.IsCompleted);
            Assert.Equal(1, file.Opens);
            Assert.False(file.WasClunked);

            // Wait(0) also proves the semaphore was not disposed; CurrentCount alone cannot.
            Assert.False(replyGate.Wait(0, ct));
            Assert.False(flushSlots.Wait(0, ct));
            Assert.True(general.Wait(0, ct));
            general.Release();

            resumeFlush.Set();
            await running.WaitAsync(TimeSpan.FromSeconds(2), ct);
            await cleanup.WaitAsync(TimeSpan.FromSeconds(2), ct);
            Assert.Equal(0, file.Opens);
            Assert.True(file.WasClunked);
            Assert.True(tree.Root.WasClunked);
            Assert.Empty(logger.Warnings);
            Assert.Throws<ObjectDisposedException>(() => replyGate.Wait(0, ct));
            Assert.Throws<ObjectDisposedException>(() => flushSlots.Wait(0, ct));
            Assert.Throws<ObjectDisposedException>(() => general.Wait(0, ct));
        }
        finally
        {
            // Release the deliberately blocked reader even when an assertion detects a regression.
            resumeFlush.Set();
            await registration.DisposeAsync();
            pendingSource?.Dispose();
            await session.DisposeAsync();
            await running.WaitAsync(ct);
            await Field<Task>(session, "_cleanup").WaitAsync(ct);
        }
    }

    private static T Field<T>(ServerSession session, string name) where T : class =>
        Assert.IsAssignableFrom<T>(typeof(ServerSession).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session));

    private sealed class BlockedReplyConnection(INinePConnection inner) : INinePConnection
    {
        public bool BlockWrites { get; set; }
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NinePAddress RemoteAddress => inner.RemoteAddress;
        public PeerIdentity? PeerIdentity => inner.PeerIdentity;
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        {
            if (BlockWrites)
            {
                WriteEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            await inner.WriteAsync(message, cancellationToken);
        }
        public ValueTask CloseAsync(CloseReason reason, CancellationToken cancellationToken = default) =>
            inner.CloseAsync(reason, cancellationToken);
        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            Closed.TrySetResult();
        }
    }
}
