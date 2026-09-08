using Microsoft.Extensions.Logging;

namespace NineP.TodoFs;

/// <summary>
/// What todofs itself reports, as source-generated <see cref="LoggerMessage"/> methods. The
/// packages log through their own records; these are the example's, and its event ids are 81xx.
/// </summary>
internal static partial class TodoFsLog
{
    [LoggerMessage(
        EventId = 8101,
        Level = LogLevel.Warning,
        Message = "--tls-cert, --tls-key or --tls-client-ca is set but no --listen address is "
            + "tls:// or wss://, so the certificate is loaded and never used")]
    public static partial void TlsFlagsWithoutATlsListener(this ILogger logger);
}
