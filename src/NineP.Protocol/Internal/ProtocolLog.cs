using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NineP.Protocol.Internal;

/// <summary>
/// Every record <c>NineP.Protocol</c> emits, as source-generated <see cref="LoggerMessage"/>
/// methods. The templates are compile-time constants and the arguments are named, so a structured
/// sink gets fields rather than one pre-formatted line; <c>CA1848</c> forbids the convenience
/// extension methods that would have thrown the structure away. Event ids are 1xxx here, 2xxx in
/// the client and 3xxx in the server, so that a record's origin is readable from its id alone.
/// Every peer-controlled argument is passed through <see cref="UntrustedText.Sanitize"/> by its
/// caller (reference §8 rule 11) — the sink's own escaping is not relied on, and nothing else
/// applies the 256-byte cap.
/// </summary>
internal static partial class ProtocolLog
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "TLS certificate verification is DISABLED for {Address}; the connection is not authenticated")]
    public static partial void TlsVerificationDisabled(this ILogger logger, string address);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "TlsTransportOptions.AllowInsecureCertificates is a client-side opt-out and is ignored when listening on {Address}")]
    public static partial void TlsInsecureOptOutIgnoredWhenListening(this ILogger logger, string address);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "{Address}: accept failed with {SocketError}; retrying in {RetryMilliseconds} ms")]
    public static partial void AcceptFailedRetrying(
        this ILogger logger, string address, SocketError socketError, double retryMilliseconds);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Warning,
        Message = "{Address} accepts any Origin; set WebSocketTransportOptions.AllowedOrigins before exposing it to browsers")]
    public static partial void WebSocketAcceptsAnyOrigin(this ILogger logger, string address);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Warning,
        Message = "refused a WebSocket upgrade from Origin '{Origin}': not in the allow-list")]
    public static partial void WebSocketOriginRefused(this ILogger logger, string origin);
}
