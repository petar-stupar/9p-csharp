using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Transports;
using NineP.Server;
using NineP.TestSupport;

namespace NineP.Server.Tests;

/// <summary>
/// One shipped server on one end of a <see cref="MemoryTransport"/> and whatever the test wants on
/// the other (S-31): every server test that can run in process does, so a bug in the session layer
/// cannot hide behind a socket.
/// </summary>
internal sealed class ServerHarness : IAsyncDisposable
{
    private readonly Task _serving;

    private ServerHarness(NinePServer server, MemoryTransport transport, NinePAddress address, MemoryFilesystem tree, Task serving)
    {
        Server = server;
        Transport = transport;
        Address = address;
        Tree = tree;
        _serving = serving;
    }

    /// <summary>The running server.</summary>
    public NinePServer Server { get; }

    /// <summary>The transport both ends share.</summary>
    public MemoryTransport Transport { get; }

    /// <summary>The address the server bound.</summary>
    public NinePAddress Address { get; }

    /// <summary>The tree being served.</summary>
    public MemoryFilesystem Tree { get; }

    /// <summary>Binds a server on a fresh in-process endpoint and starts serving.</summary>
    /// <param name="tune">Adjusts the options before the server is built.</param>
    /// <param name="tree">The tree to serve; a fresh one when null.</param>
    /// <param name="filesystem">A filesystem of the test's own, when it is not a memory tree.</param>
    /// <returns>The running harness.</returns>
    public static async Task<ServerHarness> StartAsync(
        Func<ServerOptions, ServerOptions>? tune = null,
        MemoryFilesystem? tree = null,
        IFilesystem? filesystem = null)
    {
        MemoryTransport transport = new();
        NinePAddress address = new(NinePScheme.Memory, "s" + Guid.NewGuid().ToString("N"), 0, string.Empty);
        MemoryFilesystem served = tree ?? Populate(new MemoryFilesystem());

        ServerOptions options = new() { Listen = [address], Transports = [transport] };
        NinePServer server = new(tune is null ? options : tune(options));

        Task serving = server.ServeAsync(filesystem ?? served, CancellationToken.None);
        await server.Listening;

        return new ServerHarness(server, transport, address, served, serving);
    }

    /// <summary>Connects a shipped client to this server and attaches.</summary>
    /// <param name="dialect">The dialect to offer.</param>
    /// <param name="tune">Adjusts the client options.</param>
    /// <returns>The attached session; the caller disposes it.</returns>
    public async Task<NinePSession> ConnectAsync(
        Dialect dialect, Func<ClientOptions, ClientOptions>? tune = null)
    {
        // The tree is owned by "glenda", and the identity of an unauthenticated attach is the
        // uname as claimed, so the client claims the owner (reference §5.2).
        ClientOptions options = new() { Dialects = [dialect], Msize = 8192, Uname = "glenda" };
        NinePSession session = await NinePClient
            .ConnectAsync(Transport, Address, tune is null ? options : tune(options), CancellationToken.None);

        await session.AttachAsync(CancellationToken.None);
        return session;
    }

    /// <summary>Opens a raw connection to this server, for tests that write frames by hand.</summary>
    /// <returns>The client end of a fresh connection.</returns>
    public ValueTask<INinePConnection> DialAsync() => Transport.ConnectAsync(Address);

    /// <summary>Stops the server and waits for the accept loop.</summary>
    /// <returns>A task that completes when everything has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();

        try
        {
            await _serving;
        }
        catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException)
        {
            // The accept loop was stopped on purpose.
        }
    }

    /// <summary>Fills a tree with the fixture every server test expects.</summary>
    /// <param name="tree">The tree to fill.</param>
    /// <returns>The same tree.</returns>
    internal static MemoryFilesystem Populate(MemoryFilesystem tree)
    {
        MemoryFile greeting = tree.NewFile("hello.txt", Perms.P0644);
        greeting.Data = "hello, 9P\n"u8.ToArray();
        tree.Root.Add(greeting);
        tree.Root.Add(tree.NewDirectory("sub", Perms.P0755));
        return tree;
    }
}
