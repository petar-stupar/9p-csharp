using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Threading.Channels;
using NineP.Protocol.Transports.Internal;

namespace NineP.Protocol.Transports;

/// <summary>
/// An in-process transport over duplex pipes (workspace architecture §3, S-31). It is the seam the
/// test suite proves: everything above <see cref="ITransport"/> is exercised over it, so a bug in
/// the session layer cannot hide behind a socket. Each instance is isolated — two transports never
/// share an endpoint name — and a close reason given to one end is visible at the other.
/// </summary>
public sealed class MemoryTransport : ITransport
{
    private static readonly NinePScheme[] SupportedSchemes = [NinePScheme.Memory];

    private readonly ConcurrentDictionary<string, MemoryListener> _listeners =
        new(StringComparer.Ordinal);

    /// <summary>Creates an isolated in-process transport; its addresses are <c>memory://name</c>.</summary>
    public MemoryTransport()
    {
    }

    /// <summary>The schemes this transport can dial and bind.</summary>
    public IReadOnlyCollection<NinePScheme> Schemes => SupportedSchemes;

    /// <summary>
    /// Creates a connected client/server pair without a listener, for tests that want two ends of a
    /// wire and nothing else.
    /// </summary>
    /// <returns>The two ends; the caller disposes both.</returns>
    public static (INinePConnection Client, INinePConnection Server) CreatePair()
    {
        NinePAddress address = new(NinePScheme.Memory, "pair", 0, string.Empty);
        return Link(address);
    }

    /// <summary>Dials an in-process endpoint.</summary>
    /// <param name="address">The <c>memory://name</c> endpoint to connect to.</param>
    /// <param name="cancellationToken">Cancels the dial.</param>
    /// <returns>The client end of a fresh connection.</returns>
    /// <exception cref="ArgumentException">The address is not a memory address.</exception>
    /// <exception cref="NinePException">Nothing is listening on that name.</exception>
    public ValueTask<INinePConnection> ConnectAsync(
        NinePAddress address, CancellationToken cancellationToken = default)
    {
        Require(address);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_listeners.TryGetValue(address.Host, out MemoryListener? listener))
        {
            throw new NinePException(string.Format(
                CultureInfo.InvariantCulture, "nothing is listening on {0}", address));
        }

        (INinePConnection client, INinePConnection server) = Link(address);
        if (!listener.TryEnqueue(server))
        {
            throw new NinePException(string.Format(
                CultureInfo.InvariantCulture, "nothing is listening on {0}", address));
        }

        return ValueTask.FromResult(client);
    }

    /// <summary>Binds an in-process endpoint.</summary>
    /// <param name="address">The <c>memory://name</c> endpoint to bind.</param>
    /// <param name="cancellationToken">Cancels the bind.</param>
    /// <returns>The listener; the caller disposes it.</returns>
    /// <exception cref="ArgumentException">The address is not a memory address.</exception>
    /// <exception cref="InvalidOperationException">The name is already bound on this transport.</exception>
    public ValueTask<INinePListener> ListenAsync(
        NinePAddress address, CancellationToken cancellationToken = default)
    {
        Require(address);
        cancellationToken.ThrowIfCancellationRequested();

        MemoryListener listener = new(address, () => _listeners.TryRemove(address.Host, out _));
        if (!_listeners.TryAdd(address.Host, listener))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture, "{0} is already bound", address));
        }

        return ValueTask.FromResult<INinePListener>(listener);
    }

    private static void Require(NinePAddress address)
    {
        if (address.Scheme != NinePScheme.Memory)
        {
            throw new ArgumentException("the memory transport speaks memory:// only", nameof(address));
        }
    }

    private static (INinePConnection Client, INinePConnection Server) Link(NinePAddress address)
    {
        Pipe clientToServer = new();
        Pipe serverToClient = new();
        MemoryEndpoint clientState = new();
        MemoryEndpoint serverState = new();

        MemoryConnection client = new(
            address, serverToClient.Reader, clientToServer.Writer, clientState, serverState);
        MemoryConnection server = new(
            address, clientToServer.Reader, serverToClient.Writer, serverState, clientState);

        return (client, server);
    }
}
