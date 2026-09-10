using Microsoft.Extensions.Logging;
using NineP.Protocol;
using NineP.Protocol.Transports;

namespace NineP.Server.Internal;

/// <summary>
/// Every record <c>NineP.Server</c> emits, as source-generated <see cref="LoggerMessage"/> methods.
/// See <c>NineP.Protocol.Internal.ProtocolLog</c> for why the records are shaped this way; the
/// server's event ids are 3xxx.
/// </summary>
internal static partial class ServerLog
{
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "9P listening on {Address}")]
    public static partial void Listening(this ILogger logger, NinePAddress address);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Warning,
        Message = "{Address} did not drain within {Grace}; closing it")]
    public static partial void ListenerDidNotDrain(this ILogger logger, NinePAddress address, TimeSpan grace);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Error,
        Message = "a 9P handler threw; answering i/o error")]
    public static partial void HandlerThrew(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Warning,
        Message = "a request-log sink threw")]
    public static partial void RequestLogSinkThrew(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3005,
        Level = LogLevel.Warning,
        Message = "closing a 9P connection after a framing violation: {Kind}")]
    public static partial void FramingViolation(this ILogger logger, ProtocolErrorKind kind);

    [LoggerMessage(
        EventId = 3006,
        Level = LogLevel.Debug,
        Message = "a 9P connection ended: {Reason}")]
    public static partial void ConnectionEnded(this ILogger logger, string reason);
    [LoggerMessage(
        EventId = 3007,
        Level = LogLevel.Warning,
        Message = "a fid could not be finalized during session cleanup")]
    public static partial void FidCleanupFailed(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3008,
        Level = LogLevel.Warning,
        Message = "{Address} holds {Limit} connections; further ones are refused until it releases some")]
    public static partial void ConnectionsPerAddressExceeded(this ILogger logger, string address, int limit);

    [LoggerMessage(
        EventId = 3009,
        Level = LogLevel.Warning,
        Message = "refusing Tauth from {Address}: {Budget} failed authentications inside the window")]
    public static partial void AuthAttemptsThrottled(this ILogger logger, string address, int budget);

    /// <summary>Reports detached cleanup failures without allowing a broken sink to abort later cleanup.</summary>
    /// <param name="logger">The configured sink.</param>
    /// <param name="failure">The failure encountered after the connection has already closed.</param>
    public static void CleanupFailedSafely(this ILogger logger, Exception failure)
    {
        try
        {
            logger.FidCleanupFailed(failure);
        }
#pragma warning disable CA1031 // The connection is already closed; all remaining resources must still be released.
        catch (Exception)
#pragma warning restore CA1031
        {
            // There is no usable error channel after a logger itself fails during teardown.
        }
    }
}
