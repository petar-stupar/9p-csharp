using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NineP.Protocol;
using NineP.Protocol.Transports;
using NineP.Server;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>
/// Everything a todofs command line composes: the store, the authenticator, the tree and the
/// server, wired together exactly once. The entry point builds one of these and serves it, and so
/// does the test that exercises the shipped wiring, which is the point of the type — a harness
/// that assembles the same parts differently proves nothing about the binary an operator runs.
/// </summary>
internal sealed class TodoFsHost : IAsyncDisposable
{
    private TodoFsHost(
        TodoFsOptions options,
        TodoStore store,
        KeycloakAuthenticator authenticator,
        TodoFilesystem filesystem,
        NinePServer server)
    {
        Options = options;
        Store = store;
        Authenticator = authenticator;
        Filesystem = filesystem;
        Server = server;
    }

    /// <summary>The command line this host was built from.</summary>
    public TodoFsOptions Options { get; }

    /// <summary>The database behind the tree.</summary>
    public TodoStore Store { get; }

    /// <summary>The realm this server validates tokens against.</summary>
    public KeycloakAuthenticator Authenticator { get; }

    /// <summary>The tree being served.</summary>
    public TodoFilesystem Filesystem { get; }

    /// <summary>The server, bound but not yet serving.</summary>
    public NinePServer Server { get; }

    /// <summary>Opens the database and composes the server one command line asks for.</summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="logger">Where the server and its transports report; nothing when null.</param>
    /// <param name="cancellationToken">Cancels opening the database.</param>
    /// <returns>The composed host; the caller disposes it.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    public static async Task<TodoFsHost> CreateAsync(
        TodoFsOptions options,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        ILogger log = logger ?? NullLogger.Instance;
        TodoStore store = await TodoStore.OpenAsync(options.Database, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        try
        {
            KeycloakAuthenticator authenticator = new(new KeycloakOptions
            {
                Issuer = options.Issuer,
                Audience = options.Audience,
                AdminRole = options.AdminRole,
                JwksCache = options.JwksCache,
                RequireHttps = options.RequireHttps,
            });

            TodoFilesystem filesystem = new(store, new TodoFsSettings(options.AdminRole));

            NinePServer server = new(new ServerOptions
            {
                Listen = options.Listen,
                Transports = Transports(options, log),
                Dialects = options.Dialects,
                Authenticator = authenticator,
                Logger = log,
            });

            return new TodoFsHost(options, store, authenticator, filesystem, server);
        }
        catch
        {
            await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The transports the configured addresses need.</summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="logger">Where a transport reports a refusal.</param>
    /// <returns>The transports to hand the server.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    public static IReadOnlyList<ITransport> Transports(TodoFsOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        // A certificate that is loaded and never presented reads as a server that is protected
        // when it is not, so a --tls-* flag with no tls:// or wss:// listener is said out loud.
        if ((options.ServerCertificate is not null || options.ClientCertificateAuthority is not null)
            && !options.Listen.Any(address => address.Scheme is NinePScheme.Tls or NinePScheme.Wss))
        {
            logger.TlsFlagsWithoutATlsListener();
        }

        TlsTransportOptions tls = new()
        {
            ServerCertificate = options.ServerCertificate,
            TrustedRoots = options.ClientCertificateAuthority,
            RequireClientCertificate = options.ClientCertificateAuthority is not null,
            Logger = logger,
        };

        return
        [
            new TcpTransport(),
            new TlsTransport(tls),
            new WebSocketTransport(new WebSocketTransportOptions { Tls = tls, Logger = logger }),
        ];
    }

    /// <summary>Prints the addresses actually bound, so a caller that asked for port 0 learns them.</summary>
    /// <param name="server">The running server.</param>
    /// <param name="options">The parsed command line.</param>
    /// <returns>A task that completes once the addresses have been printed.</returns>
    /// <exception cref="ArgumentNullException">The server or the options are null.</exception>
    public static async Task AnnounceAsync(NinePServer server, TodoFsOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        // ListeningAsync completes once every configured address is bound, and it *faults* when
        // one could not be. A spin on Endpoints.Count could only ever spin when nothing was
        // going to bind.
        await server.ListeningAsync().ConfigureAwait(false);

        foreach (NinePAddress endpoint in server.Endpoints)
        {
            await Console.Out.WriteLineAsync(string.Format(
                CultureInfo.InvariantCulture,
                "listening {0} db={1}",
                endpoint,
                options.Database));
        }

        await Console.Out.FlushAsync();
    }

    /// <summary>Stops the server and closes the database.</summary>
    /// <returns>A task that completes when everything is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync().ConfigureAwait(false);
        Authenticator.Dispose();
        await Store.DisposeAsync().ConfigureAwait(false);
    }
}
