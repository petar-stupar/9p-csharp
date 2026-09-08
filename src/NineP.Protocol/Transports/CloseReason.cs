namespace NineP.Protocol.Transports;

/// <summary>
/// Why a connection closed; carried to the peer where the transport can express it (workspace
/// architecture §3). A framing violation cannot be resynced, so it is always a close and never a
/// skipped message.
/// </summary>
public enum CloseReason
{
    /// <summary>An orderly close initiated by this side.</summary>
    Normal,

    /// <summary>The peer closed.</summary>
    PeerClosed,

    /// <summary>A frame violated size, framing or dialect rules; the connection cannot be resynced.</summary>
    ProtocolViolation,

    /// <summary>A frame exceeded the negotiated msize or the pre-negotiation cap.</summary>
    MessageTooLarge,

    /// <summary>A timeout elapsed: read-header, idle, or authentication.</summary>
    Timeout,

    /// <summary>A limit was exceeded: connections, fids, or in-flight requests.</summary>
    ResourceLimit,

    /// <summary>The server is shutting down.</summary>
    Shutdown,

    /// <summary>An unexpected I/O or transport failure.</summary>
    TransportError,
}
