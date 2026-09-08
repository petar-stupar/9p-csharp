using System.Globalization;
using Microsoft.Extensions.Logging;
using NineP.Protocol;
using NineP.Protocol.Transports;
using NineP.Server;

namespace NineP.JsonFs;

/// <summary>The parts of jsonfs's startup that are worth a name of their own.</summary>
internal static class JsonFsHost
{
    /// <summary>
    /// The transports the configured addresses need. A <c>tls://</c> or <c>wss://</c> listener
    /// needs a certificate, so it is built here from the flags rather than defaulted.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="logger">Where a transport reports a refusal.</param>
    /// <returns>The transports to hand the server.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    public static IReadOnlyList<ITransport> Transports(JsonFsOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        Warn(options, logger);

        TlsTransportOptions tls = new()
        {
            ServerCertificate = options.ServerCertificate,
            TrustedRoots = options.ClientCertificateAuthority,
            RequireClientCertificate = options.ClientCertificateAuthority is not null,
            Logger = logger,
        };

        WebSocketTransportOptions websocket = new()
        {
            AllowedOrigins = options.WebSocketOrigins,
            Tls = tls,
            Logger = logger,
        };

        return [new TcpTransport(), new TlsTransport(tls), new WebSocketTransport(websocket)];
    }

    /// <summary>
    /// Reports a flag that cannot apply to any configured listener. jsonfs still starts — the
    /// listeners it was told to bind are the ones it binds — but a certificate that is loaded and
    /// never presented, or an <c>Origin</c> allow-list that is never consulted, reads as a server
    /// that is protected when it is not, so it is said out loud at startup.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="logger">Where the warnings go.</param>
    private static void Warn(JsonFsOptions options, ILogger logger)
    {
        bool tls = options.ServerCertificate is not null || options.ClientCertificateAuthority is not null;
        if (tls && !options.Listen.Any(
            address => address.Scheme is NinePScheme.Tls or NinePScheme.Wss))
        {
            logger.TlsFlagsWithoutATlsListener();
        }

        if (options.WebSocketOrigins.Count > 0 && !options.Listen.Any(
            address => address.Scheme is NinePScheme.Ws or NinePScheme.Wss))
        {
            logger.WebSocketOriginsWithoutAWebSocketListener();
        }
    }

    /// <summary>Prints the addresses actually bound, so a caller that asked for port 0 learns them.</summary>
    /// <param name="server">The running server.</param>
    /// <param name="options">The parsed command line.</param>
    /// <returns>A task that completes once the addresses have been printed.</returns>
    /// <exception cref="ArgumentNullException">The server or the options are null.</exception>
    public static async Task AnnounceAsync(NinePServer server, JsonFsOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        // ListeningAsync completes once every configured address is bound, and it *faults* when
        // one could not be — a port already in use, an address that is not this machine's. A
        // spin on Endpoints.Count could only ever spin: nothing was going to bind, and the
        // process burned a core announcing an address it did not have.
        await server.ListeningAsync().ConfigureAwait(false);

        foreach (NinePAddress endpoint in server.Endpoints)
        {
            await Console.Out.WriteLineAsync(string.Format(
                CultureInfo.InvariantCulture,
                "listening {0} file={1} writable={2}",
                endpoint,
                options.File,
                options.Writable ? "yes" : "no"));
        }

        await Console.Out.FlushAsync();
    }
}
