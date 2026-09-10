using System.Collections.Concurrent;
using NineP.Protocol;

namespace NineP.Server.Internal;

/// <summary>
/// The live counters behind <see cref="ServerCounters"/> (architecture §4). They are mutable and
/// shared by every connection; <see cref="Snapshot"/> is what a scrape sees, so a reader never
/// observes one counter from before an update and another from after it.
/// </summary>
internal sealed class ServerMetrics
{
    private readonly ConcurrentDictionary<MessageType, long> _byType = new();
    private readonly ConcurrentDictionary<ProtocolErrorKind, long> _byKind = new();
    private readonly ConcurrentDictionary<int, long> _byErrno = new();
    private long _bytesRead;
    private long _bytesWritten;
    private long _accepted;
    private long _open;
    private long _refused;
    private long _metered;
    private long _authThrottled;

    /// <summary>Records one received request.</summary>
    /// <param name="type">The T-message type.</param>
    public void CountRequest(MessageType type) =>
        _byType.AddOrUpdate(type, 1, static (_, count) => count + 1);

    /// <summary>Records one error answered to a peer.</summary>
    /// <param name="error">The error value.</param>
    public void CountError(NinePError error) =>
        _byErrno.AddOrUpdate(error.Errno, 1, static (_, count) => count + 1);

    /// <summary>Records one malformed or illegal message.</summary>
    /// <param name="kind">What was wrong with it.</param>
    public void CountProtocolError(ProtocolErrorKind kind) =>
        _byKind.AddOrUpdate(kind, 1, static (_, count) => count + 1);

    /// <summary>Adds to the bytes-read counter.</summary>
    /// <param name="count">How many bytes were read.</param>
    public void AddBytesRead(int count) => Interlocked.Add(ref _bytesRead, count);

    /// <summary>Adds to the bytes-written counter.</summary>
    /// <param name="count">How many bytes were written.</param>
    public void AddBytesWritten(int count) => Interlocked.Add(ref _bytesWritten, count);

    /// <summary>Records an accepted connection.</summary>
    public void ConnectionAccepted()
    {
        Interlocked.Increment(ref _accepted);
        Interlocked.Increment(ref _open);
    }

    /// <summary>Records a closed connection.</summary>
    public void ConnectionClosed() => Interlocked.Decrement(ref _open);

    /// <summary>Records a connection refused by a limit.</summary>
    public void ConnectionRefused() => Interlocked.Increment(ref _refused);

    /// <summary>Records one request refused for the rate budget (reference §8 rule 40).</summary>
    public void RequestMetered() => Interlocked.Increment(ref _metered);

    /// <summary>Records one Tauth refused for the per-address budget (reference §8 rule 40).</summary>
    public void AuthThrottled() => Interlocked.Increment(ref _authThrottled);

    /// <summary>The counters as one consistent record.</summary>
    /// <returns>The snapshot a caller reads.</returns>
    public ServerCounters Snapshot() => new()
    {
        MessagesByType = new Dictionary<MessageType, long>(_byType),
        ProtocolErrorsByKind = new Dictionary<ProtocolErrorKind, long>(_byKind),
        ErrorsByErrno = new Dictionary<int, long>(_byErrno),
        BytesRead = Interlocked.Read(ref _bytesRead),
        BytesWritten = Interlocked.Read(ref _bytesWritten),
        ConnectionsAccepted = Interlocked.Read(ref _accepted),
        ConnectionsOpen = Interlocked.Read(ref _open),
        ConnectionsRefused = Interlocked.Read(ref _refused),
        RequestsMetered = Interlocked.Read(ref _metered),
        AuthAttemptsThrottled = Interlocked.Read(ref _authThrottled),
    };
}
