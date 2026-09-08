using System.Collections.Concurrent;
using NineP.Protocol;
using NineP.Protocol.Auth;

namespace NineP.Server.Internal;

/// <summary>
/// One in-flight request. The state moves only by <see cref="Interlocked.CompareExchange(ref int, int, int)"/>,
/// which is what makes "send the reply" and "suppress it" mutually exclusive: exactly one of the
/// handler and the flush wins, so a reply can never appear after its own <c>Rflush</c> (§6.6).
/// </summary>
internal sealed class PendingRequest(ushort tag, MessageType type)
{
    private int _state = (int)RequestState.Running;

    /// <summary>The tag the request came in under.</summary>
    public ushort Tag => tag;

    /// <summary>The T-message type, for the request log.</summary>
    public MessageType Type => type;

    /// <summary>Cancels the handler when the request is flushed.</summary>
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Where the request is now.</summary>
    public RequestState State => (RequestState)Volatile.Read(ref _state);

    /// <summary>The R-type this request was answered with, for the request log.</summary>
    public MessageType Reply { get; set; }

    /// <summary>The error the request was answered with, when it was an error.</summary>
    public NinePError? Error { get; set; }

    /// <summary>The identity the request ran as, for the request log.</summary>
    public Identity? Identity { get; set; }

    /// <summary>A sanitised description of what was asked (reference §8 rule 11).</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Claims the right to send this request's reply.</summary>
    /// <returns>False when a <c>Tflush</c> already claimed it, or it was already answered.</returns>
    public bool TryComplete() =>
        Interlocked.CompareExchange(ref _state, (int)RequestState.Completed, (int)RequestState.Running)
            == (int)RequestState.Running;

    /// <summary>Claims the right to suppress this request's reply.</summary>
    /// <returns>False when the reply had already been handed to the writer.</returns>
    public bool TryFlush() =>
        Interlocked.CompareExchange(ref _state, (int)RequestState.Flushed, (int)RequestState.Running)
            == (int)RequestState.Running;
}
