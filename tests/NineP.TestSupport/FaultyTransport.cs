using System.Collections.Concurrent;
using NineP.Protocol;
using NineP.Protocol.Transports;

namespace NineP.TestSupport;

/// <summary>Explicit adversarial frame operations, separate from ordered stream impairments.</summary>
public enum FrameFaultKind { Drop, Delay, Duplicate }

/// <summary>A one-shot write fault, selected by complete-frame number and/or message type.</summary>
public sealed record FrameFault(FrameFaultKind Kind, MessageType? Type = null, int Frame = 0,
    TimeSpan Delay = default, Task? Gate = null);

/// <summary>One connection's deterministic script and observable activation trace.</summary>
public sealed class FaultScript
{
    private readonly ConcurrentQueue<string> _trace = new();
    private readonly HashSet<int> _used = [];
    private int _writes;
    private long _read;

    public IReadOnlyList<FrameFault> Writes { get; init; } = [];
    public int ReadChunkSize { get; init; } = int.MaxValue;
    public long CloseAfterBytes { get; init; } = long.MaxValue;
    public long StallAfterBytes { get; init; } = long.MaxValue;
    public TimeSpan ReadDelay { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public IReadOnlyList<string> Trace => _trace.ToArray();
    public long BytesRead => Interlocked.Read(ref _read);
    public int FramesWritten => Volatile.Read(ref _writes);
    internal void Record(string action) => _trace.Enqueue(action);
    internal void Read(int bytes) => Interlocked.Add(ref _read, bytes);

    internal FrameFault? Next(ReadOnlyMemory<byte> frame)
    {
        int number = Interlocked.Increment(ref _writes);
        lock (_used)
        {
            for (int i = 0; i < Writes.Count; i++)
            {
                FrameFault fault = Writes[i];
                if (!_used.Contains(i) && (fault.Frame == 0 || fault.Frame == number)
                    && (fault.Type is null || (MessageType)frame.Span[4] == fault.Type))
                {
                    _used.Add(i);
                    Record(fault.Kind.ToString());
                    return fault;
                }
            }
        }
        return null;
    }
}

/// <summary>
/// Decorates the actual transport seam. Reads may fragment, throttle, stall or close; normal
/// writes remain complete ordered frames. Drop/duplicate are explicitly adversarial peer faults.
/// </summary>
public sealed class FaultyTransport(ITransport inner, Func<FaultScript>? client = null,
    Func<FaultScript>? server = null) : ITransport
{
    public IReadOnlyCollection<NinePScheme> Schemes => inner.Schemes;

    public async ValueTask<INinePConnection> ConnectAsync(NinePAddress address, CancellationToken cancellationToken = default) =>
        Wrap(await inner.ConnectAsync(address, cancellationToken), client?.Invoke() ?? new FaultScript());

    public async ValueTask<INinePListener> ListenAsync(NinePAddress address, CancellationToken cancellationToken = default) =>
        new Listener(await inner.ListenAsync(address, cancellationToken), server);

    public static INinePConnection Wrap(INinePConnection connection, FaultScript script) => new Connection(connection, script);

    private sealed class Listener(INinePListener inner, Func<FaultScript>? script) : INinePListener
    {
        public NinePAddress LocalAddress => inner.LocalAddress;
        public async ValueTask<INinePConnection?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            INinePConnection? connection = await inner.AcceptAsync(cancellationToken);
            return connection is null ? null : Wrap(connection, script?.Invoke() ?? new FaultScript());
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class Connection(INinePConnection inner, FaultScript script) : INinePConnection
    {
        private readonly CancellationTokenSource _lifetime = new();
        private int _disposed;
        public NinePAddress RemoteAddress => inner.RemoteAddress;
        public PeerIdentity? PeerIdentity => inner.PeerIdentity;

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            CancellationToken ct = linked.Token;
            if (script.BytesRead >= script.CloseAfterBytes)
            {
                script.Record("CloseAfterBytes");
                await inner.CloseAsync(CloseReason.Normal, ct);
                return 0;
            }
            if (script.BytesRead >= script.StallAfterBytes)
            {
                script.Record("Stall");
                await Task.Delay(Timeout.InfiniteTimeSpan, script.Clock, ct);
            }
            int count = (int)Math.Min(buffer.Length, Math.Min(script.ReadChunkSize,
                Math.Min(script.CloseAfterBytes - script.BytesRead, script.StallAfterBytes - script.BytesRead)));
            if (count < buffer.Length)
            {
                script.Record("Split");
            }
            if (script.ReadDelay > TimeSpan.Zero)
            {
                script.Record("Throttle");
                await Task.Delay(script.ReadDelay, script.Clock, ct);
            }
            int read = await inner.ReadAsync(buffer[..count], ct);
            script.Read(read);
            return read;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            CancellationToken ct = linked.Token;
            FrameFault? fault = script.Next(message);
            if (fault is null)
            {
                await inner.WriteAsync(message, ct);
                return;
            }
            // The caller may return its lease as soon as its write completes. Any delayed data
            // belongs to this operation, and cancellation/disposal releases it with the task.
            byte[] owned = message.ToArray();
            if (fault.Kind == FrameFaultKind.Drop)
            {
                return;
            }
            if (fault.Kind == FrameFaultKind.Duplicate)
            {
                await inner.WriteAsync(owned, ct);
            }
            if (fault.Gate is { } gate)
            {
                await gate.WaitAsync(ct);
            }
            if (fault.Delay > TimeSpan.Zero)
            {
                await Task.Delay(fault.Delay, script.Clock, ct);
            }
            await inner.WriteAsync(owned, ct);
        }

        public async ValueTask CloseAsync(CloseReason reason, CancellationToken cancellationToken = default)
        {
            await _lifetime.CancelAsync();
            await inner.CloseAsync(reason, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            await _lifetime.CancelAsync();
            await inner.DisposeAsync();
            _lifetime.Dispose();
        }
    }
}
