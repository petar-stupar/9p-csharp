using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Negotiation;
using NineP.Protocol.Transports;

namespace NineP.Client.Internal;

/// <summary>One request that has been written and is waiting for its reply.</summary>
internal sealed class PendingRequest(MessageType expected)
{
    /// <summary>The R-type this request may be answered with, besides the dialect's error type.</summary>
    public MessageType Expected => expected;

    /// <summary>Completed with the reply's frame, or faulted when the session terminates.</summary>
    public TaskCompletionSource<byte[]> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// When this entry is a <c>Tflush</c> the client sent to cancel a request of its own, the
    /// request it cancels. An <c>Rflush</c> is the server's word that it is finished with
    /// <b>both</b> tags (reference §5.3), so both are reclaimed when one arrives — however late.
    /// Null for a <c>Tflush</c> the caller asked for directly, whose <c>oldtag</c> belongs to
    /// whoever is still waiting on it.
    /// </summary>
    public (ushort Tag, PendingRequest Request)? Flushed { get; init; }
}
