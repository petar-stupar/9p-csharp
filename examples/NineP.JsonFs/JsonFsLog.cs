using Microsoft.Extensions.Logging;

namespace NineP.JsonFs;

/// <summary>
/// What jsonfs itself reports, as source-generated <see cref="LoggerMessage"/> methods. The
/// packages log through their own records; these are the example's, and its event ids are 8xxx.
/// </summary>
internal static partial class JsonFsLog
{
    [LoggerMessage(
        EventId = 8001,
        Level = LogLevel.Warning,
        Message = "--tls-cert, --tls-key or --tls-client-ca is set but no --listen address is "
            + "tls:// or wss://, so the certificate is loaded and never used")]
    public static partial void TlsFlagsWithoutATlsListener(this ILogger logger);

    [LoggerMessage(
        EventId = 8002,
        Level = LogLevel.Warning,
        Message = "--ws-origin is set but no --listen address is ws:// or wss://, so the allowed "
            + "origins are parsed and never used")]
    public static partial void WebSocketOriginsWithoutAWebSocketListener(this ILogger logger);
}
