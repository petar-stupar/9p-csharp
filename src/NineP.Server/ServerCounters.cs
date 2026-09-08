using NineP.Protocol;

namespace NineP.Server;

/// <summary>
/// A snapshot of what the server has done, for metrics scraping (architecture §4). It is a record
/// rather than a set of live counters so that a scrape sees one consistent moment rather than a
/// mixture of two.
/// </summary>
public sealed record ServerCounters
{
    /// <summary>Messages received per type.</summary>
    public IReadOnlyDictionary<MessageType, long> MessagesByType { get; init; } =
        new Dictionary<MessageType, long>();

    /// <summary>Errors answered per protocol error kind.</summary>
    public IReadOnlyDictionary<ProtocolErrorKind, long> ProtocolErrorsByKind { get; init; } =
        new Dictionary<ProtocolErrorKind, long>();

    /// <summary>Errors answered per errno.</summary>
    public IReadOnlyDictionary<int, long> ErrorsByErrno { get; init; } = new Dictionary<int, long>();

    /// <summary>Bytes read from transports.</summary>
    public long BytesRead { get; init; }

    /// <summary>Bytes written to transports.</summary>
    public long BytesWritten { get; init; }

    /// <summary>Connections accepted since the server started.</summary>
    public long ConnectionsAccepted { get; init; }

    /// <summary>Connections currently open.</summary>
    public long ConnectionsOpen { get; init; }

    /// <summary>Connections closed because a limit was reached.</summary>
    public long ConnectionsRefused { get; init; }
}
