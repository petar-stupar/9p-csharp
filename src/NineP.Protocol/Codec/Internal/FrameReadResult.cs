using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using NineP.Protocol.Transports;

namespace NineP.Protocol.Codec.Internal;

/// <summary>What one attempt to read a frame produced.</summary>
/// <param name="Lease">The frame, owned by the caller until it disposes the lease.</param>
/// <param name="IsEndOfStream">True when the peer closed cleanly between frames.</param>
/// <param name="Failure">The framing failure, when there is one.</param>
/// <param name="Close">Why the connection must now be closed, when it must.</param>
internal readonly record struct FrameReadResult(
    FrameLease? Lease, bool IsEndOfStream, ProtocolErrorKind? Failure, CloseReason? Close)
{
    /// <summary>True when a complete frame is available.</summary>
    public bool HasFrame => Lease is not null;

    /// <summary>A complete frame.</summary>
    /// <param name="lease">The lease that owns it.</param>
    /// <returns>The result.</returns>
    public static FrameReadResult Frame(FrameLease lease) => new(lease, false, null, null);

    /// <summary>The peer closed cleanly, between frames.</summary>
    /// <returns>The result.</returns>
    public static FrameReadResult Closed() => new(null, true, null, CloseReason.PeerClosed);

    /// <summary>The peer closed in the middle of a frame.</summary>
    /// <returns>The result.</returns>
    public static FrameReadResult Truncated() =>
        new(null, true, ProtocolErrorKind.Size, CloseReason.ProtocolViolation);

    /// <summary>The frame violated the size rule and the connection cannot be resynced.</summary>
    /// <param name="close">Which close reason the violation carries.</param>
    /// <returns>The result.</returns>
    public static FrameReadResult SizeViolation(CloseReason close) =>
        new(null, false, ProtocolErrorKind.Size, close);

    /// <summary>The peer began a frame and did not finish it within the read-header timeout.</summary>
    /// <returns>The result.</returns>
    public static FrameReadResult HeaderTimeout() => new(null, false, null, CloseReason.Timeout);
}
