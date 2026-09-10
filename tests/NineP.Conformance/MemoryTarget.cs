using NineP.Cli;
using NineP.Client;
using NineP.JsonFs;
using NineP.Protocol;
using NineP.Protocol.Transports;
using NineP.Server;

namespace NineP.Conformance;

/// <summary>
/// The same scenario with no sockets and no processes: jsonfs's filesystem, the shipped server and
/// the shipped client over a <see cref="MemoryTransport"/>, driven through the cli's own command
/// implementations so that the bytes compared are the ones the cli would print (S-31).
/// </summary>
internal sealed class MemoryTarget : ConformanceTarget
{
    private readonly NinePServer _server;
    private readonly JsonFilesystem _filesystem;
    private readonly MemoryTransport _transport;

    private readonly NinePAddress _address;
    private readonly Task _serving;

    private MemoryTarget(
        Dialect dialect,
        NinePServer server,
        JsonFilesystem filesystem,
        MemoryTransport transport,
        NinePAddress address,
        Task serving)
        : base(dialect, "memory")
    {
        _server = server;
        _filesystem = filesystem;
        _transport = transport;
        _address = address;
        _serving = serving;
    }

    /// <summary>Serves a document in process.</summary>
    /// <param name="dialect">The dialect to serve and to ask for.</param>
    /// <param name="document">The JSON text to serve.</param>
    /// <param name="writable">True to accept writes.</param>
    /// <returns>The running target.</returns>
    public static async Task<MemoryTarget> StartAsync(Dialect dialect, string document, bool writable)
    {
        using MemoryStream stream = new(ConformanceText.Utf8.GetBytes(document));
        JsonTree tree = JsonTree.Parse(stream, "conformance.json");

        MemoryTransport transport = new();
        NinePAddress address = new(NinePScheme.Memory, "c" + Guid.NewGuid().ToString("N"), 0, string.Empty);

        NinePServer server = new(new ServerOptions
        {
            Listen = [address],
            Transports = [transport],
            Dialects = new HashSet<Dialect> { dialect },
        });

        JsonFilesystem filesystem = new(tree, writable);
        Task serving = server.ServeAsync(filesystem, CancellationToken.None);

        // Endpoints is valid once serving has started; the driver waits for the bind rather than
        // racing it.
        while (server.Endpoints.Count == 0)
        {
            await Task.Yield();
        }

        return new MemoryTarget(dialect, server, filesystem, transport, address, serving);
    }

    /// <summary>Runs one cli command in process, against a fresh session.</summary>
    /// <param name="arguments">The command and its arguments, plus any cli flags.</param>
    /// <param name="stdin">Bytes the command reads from standard input.</param>
    /// <returns>The exit code and the two streams, exactly as the process would produce them.</returns>
    public override async Task<CliResult> RunAsync(IReadOnlyList<string> arguments, byte[]? stdin = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        using MemoryStream output = new();
        using MemoryStream input = new(stdin ?? []);

        try
        {
            CliOptions options = CliOptions.Parse([.. arguments]);
            ClientOptions client = new()
            {
                Dialects = [Dialect],
                Uname = options.Uname,
                Aname = options.Aname,
            };

            await using NinePSession session = await NinePClient
                .ConnectAsync(_transport, _address, client, CancellationToken.None);
            await session.AttachAsync(CancellationToken.None);
            await CliCommands.RunAsync(session, options, output, input, CancellationToken.None);

            return new CliResult(0, output.ToArray(), string.Empty);
        }
        catch (Exception failure) when (CliCommands.ExitCodeFor(failure) is int code)
        {
            string message = failure is NinePException refusal
                and not NinePProtocolException and not NinePVersionException
                ? CliCommands.FormatError(refusal.Error)
                : "ninep: " + failure.Message;

            return new CliResult(code, output.ToArray(), message);
        }
    }

    /// <summary>Stops the server and the transport, then the filesystem behind them.</summary>
    /// <returns>A task that completes when the accept loop has stopped.</returns>
    public override async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();

        try
        {
            await _serving;
        }
        catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException)
        {
            // The accept loop was stopped on purpose.
        }

        _filesystem.Dispose();
    }
}
