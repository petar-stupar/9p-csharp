using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NineP.Protocol.Internal;
using NineP.Protocol.Transports.Internal;

namespace NineP.Protocol.Transports;

/// <summary>
/// TCP with <c>TCP_NODELAY</c> and keep-alive on, an accept backlog and a per-listener connection
/// cap. The cap <b>stops accepting</b>: a connection over the limit waits in the kernel's backlog
/// and is served when a slot frees, rather than being accepted and reset, which a client cannot
/// tell from a crash.
/// </summary>
public sealed class TcpTransport : ITransport
{
    private static readonly NinePScheme[] SupportedSchemes = [NinePScheme.Tcp];

    private readonly TcpTransportOptions _options;

    /// <summary>Creates a TCP transport.</summary>
    /// <param name="options">The tuning; the defaults of §5.5 when null.</param>
    /// <exception cref="ArgumentOutOfRangeException">A bound in the options is not positive.</exception>
    public TcpTransport(TcpTransportOptions? options = null)
    {
        _options = options ?? new TcpTransportOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.Backlog, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxConnections, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.ConnectTimeout, TimeSpan.Zero, nameof(options));
    }

    /// <summary>The schemes this transport can dial and bind.</summary>
    public IReadOnlyCollection<NinePScheme> Schemes => SupportedSchemes;

    /// <summary>Dials a TCP endpoint.</summary>
    /// <param name="address">The <c>tcp://host:port</c> endpoint.</param>
    /// <param name="cancellationToken">Cancels the dial.</param>
    /// <returns>The connected connection; the caller disposes it.</returns>
    /// <exception cref="ArgumentException">The address is not a TCP address.</exception>
    /// <exception cref="TimeoutException">The dial did not complete within the configured budget.</exception>
    public async ValueTask<INinePConnection> ConnectAsync(
        NinePAddress address, CancellationToken cancellationToken = default)
    {
        Require(address, NinePScheme.Tcp);

        // CA2000: the socket's lifetime becomes the connection's, and the connection is the
        // caller's to dispose — that is the contract of ITransport.ConnectAsync. Every path that
        // does not hand it over disposes it in the catch below.
#pragma warning disable CA2000
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
#pragma warning restore CA2000
        try
        {
            using CancellationTokenSource budget =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_options.ConnectTimeout);

            try
            {
                await socket.ConnectAsync(address.Host, address.Port, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(string.Format(
                    CultureInfo.InvariantCulture, "connecting to {0} took longer than {1}",
                    address, _options.ConnectTimeout));
            }

            Configure(socket, _options);
            return new TcpTransportConnection(socket, address, null);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Binds a TCP endpoint.</summary>
    /// <param name="address">The <c>tcp://host:port</c> endpoint; port 0 lets the kernel choose.</param>
    /// <param name="cancellationToken">Cancels the bind.</param>
    /// <returns>The listener, carrying the port that was actually bound.</returns>
    /// <exception cref="ArgumentException">The address is not a TCP address.</exception>
    public ValueTask<INinePListener> ListenAsync(
        NinePAddress address, CancellationToken cancellationToken = default)
    {
        Require(address, NinePScheme.Tcp);
        cancellationToken.ThrowIfCancellationRequested();

        // CA2000: both the socket and the listener are the caller's to dispose — that is the
        // contract of ITransport.ListenAsync — and the only path that does not hand them over is
        // the catch, which disposes the socket before rethrowing.
#pragma warning disable CA2000
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(Resolve(address.Host), address.Port));
            socket.Listen(_options.Backlog);
            return ValueTask.FromResult<INinePListener>(new TcpTransportListener(socket, address, _options));
        }
#pragma warning restore CA2000
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Applies the socket options every accepted and dialled socket carries.</summary>
    /// <param name="socket">The connected socket.</param>
    /// <param name="options">The tuning to apply.</param>
    internal static void Configure(Socket socket, TcpTransportOptions options)
    {
        socket.NoDelay = options.NoDelay;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, options.KeepAlive);
    }

    /// <summary>Refuses an address whose scheme belongs to another transport.</summary>
    /// <param name="address">The address the caller supplied.</param>
    /// <param name="expected">The scheme this transport owns.</param>
    /// <exception cref="ArgumentException">The schemes do not match.</exception>
    internal static void Require(NinePAddress address, NinePScheme expected)
    {
        if (address.Scheme != expected)
        {
            throw new ArgumentException(
                string.Format(CultureInfo.InvariantCulture, "this transport speaks {0}:// only", expected),
                nameof(address));
        }
    }

    /// <summary>The address of the peer of an accepted socket, for logging and for the session.</summary>
    /// <param name="socket">The accepted socket.</param>
    /// <param name="scheme">The scheme the accepting transport owns.</param>
    /// <returns>The peer's address.</returns>
    internal static NinePAddress RemoteAddressOf(Socket socket, NinePScheme scheme)
    {
        if (socket.RemoteEndPoint is not IPEndPoint endpoint)
        {
            return new NinePAddress(scheme, "unknown", 0, string.Empty);
        }

        // A dual-mode socket reports an IPv4 peer as ::ffff:127.0.0.1; the address a log or an
        // allow-list should carry is the one the peer actually dialled from.
        IPAddress peer = endpoint.Address.IsIPv4MappedToIPv6
            ? endpoint.Address.MapToIPv4()
            : endpoint.Address;

        string host = peer.AddressFamily == AddressFamily.InterNetworkV6
            ? "[" + peer.ToString() + "]"
            : peer.ToString();

        return new NinePAddress(scheme, host, endpoint.Port, scheme is NinePScheme.Ws or NinePScheme.Wss ? "/" : string.Empty);
    }

    /// <summary>The address to bind for a host that may be a name or a literal.</summary>
    /// <param name="host">The host from the address.</param>
    /// <returns>The address to bind.</returns>
    internal static IPAddress Resolve(string host) =>
        IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal)
            ? literal
            : Dns.GetHostAddresses(host)[0];
}
