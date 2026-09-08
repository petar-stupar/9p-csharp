using System.Globalization;
using System.Net.Http;
using System.Net.WebSockets;
using NineP.Protocol.Internal;
using NineP.Protocol.Transports.Internal;

namespace NineP.Protocol.Transports;

/// <summary>
/// RFC 6455 WebSocket: one 9P message per binary WebSocket message (S-1). Fragmentation stays
/// inside WebSocket, the size cap is enforced while a message is being reassembled, a text message
/// is a protocol error, and the server checks the <c>Origin</c> allow-list before the upgrade
/// completes. No ASP.NET dependency: the client is <see cref="ClientWebSocket"/> and the server is
/// a raw socket listener plus <c>WebSocket.CreateFromStream</c>.
/// </summary>
public sealed class WebSocketTransport : ITransport
{
    private static readonly NinePScheme[] SupportedSchemes = [NinePScheme.Ws, NinePScheme.Wss];

    private readonly WebSocketTransportOptions _options;

    /// <summary>Creates a WebSocket transport.</summary>
    /// <param name="options">The configuration; the defaults of §5.5 when null.</param>
    /// <exception cref="ArgumentOutOfRangeException">The message cap is not positive.</exception>
    public WebSocketTransport(WebSocketTransportOptions? options = null)
    {
        _options = options ?? new WebSocketTransportOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxMessageSize, nameof(options));
    }

    /// <summary>The schemes this transport can dial and bind.</summary>
    public IReadOnlyCollection<NinePScheme> Schemes => SupportedSchemes;

    /// <summary>Dials a WebSocket endpoint and completes the upgrade before returning.</summary>
    /// <param name="address">The <c>ws://host:port/path</c> or <c>wss://…</c> endpoint.</param>
    /// <param name="cancellationToken">Cancels the dial and the upgrade.</param>
    /// <returns>The connected connection; the caller disposes it.</returns>
    /// <exception cref="ArgumentException">The address is not a WebSocket address.</exception>
    /// <exception cref="WebSocketException">The upgrade was refused.</exception>
    public async ValueTask<INinePConnection> ConnectAsync(
        NinePAddress address, CancellationToken cancellationToken = default)
    {
        if (address.Scheme is not (NinePScheme.Ws or NinePScheme.Wss))
        {
            throw new ArgumentException("this transport speaks ws:// and wss:// only", nameof(address));
        }

        // CA2000: the socket and the invoker become the connection's, and the connection is the
        // caller's to dispose; the only path that returns neither disposes both in its catch.
#pragma warning disable CA2000
        ClientWebSocket socket = new();
#pragma warning restore CA2000
        HttpMessageInvoker? invoker = null;
        try
        {
            if (_options.Subprotocol is not null)
            {
                socket.Options.AddSubProtocol(_options.Subprotocol);
            }

            socket.Options.KeepAliveInterval = _options.KeepAliveInterval;

            using CancellationTokenSource budget =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_options.HandshakeTimeout);

            Uri uri = ToUri(address);
            if (address.Scheme != NinePScheme.Wss || _options.Tls is null)
            {
                await socket.ConnectAsync(uri, budget.Token).ConfigureAwait(false);
                return new WebSocketConnection(socket, address, null, _options.MaxMessageSize);
            }

            // A caller-supplied certificate policy reaches ClientWebSocket only through an invoker:
            // ClientWebSocketOptions cannot express a custom trust store. The invoker outlives the
            // handshake — the connection owns it and disposes it with the socket.
#pragma warning disable CA2000
            invoker = CreateInvoker(address);
#pragma warning restore CA2000
            await socket.ConnectAsync(uri, invoker, budget.Token).ConfigureAwait(false);
            return new WebSocketConnection(socket, address, null, _options.MaxMessageSize, invoker);
        }
        catch
        {
            socket.Dispose();
            invoker?.Dispose();
            throw;
        }
    }

    /// <summary>Binds a WebSocket endpoint.</summary>
    /// <param name="address">The endpoint to bind; port 0 lets the kernel choose.</param>
    /// <param name="cancellationToken">Cancels the bind.</param>
    /// <returns>The listener, carrying the port that was actually bound.</returns>
    /// <exception cref="ArgumentException">The address is not a WebSocket address.</exception>
    /// <exception cref="InvalidOperationException">A wss listener has no server certificate.</exception>
    public async ValueTask<INinePListener> ListenAsync(
        NinePAddress address, CancellationToken cancellationToken = default)
    {
        if (address.Scheme is not (NinePScheme.Ws or NinePScheme.Wss))
        {
            throw new ArgumentException("this transport speaks ws:// and wss:// only", nameof(address));
        }

        cancellationToken.ThrowIfCancellationRequested();

        TlsTransport? tls = null;
        if (address.Scheme == NinePScheme.Wss)
        {
            if (_options.Tls?.ServerCertificate is null)
            {
                throw new InvalidOperationException(
                    "a wss:// listener needs WebSocketTransportOptions.Tls.ServerCertificate");
            }

            tls = new TlsTransport(_options.Tls);
        }

        // CA2000: the listener is the caller's to dispose, which is the contract of ITransport.
#pragma warning disable CA2000
        INinePListener inner = await new TcpTransport(_options.Tcp)
            .ListenAsync(address with { Scheme = NinePScheme.Tcp }, cancellationToken).ConfigureAwait(false);
        return new RawWebSocketListener(address, _options, tls, inner);
#pragma warning restore CA2000
    }

    private static Uri ToUri(NinePAddress address) => new(string.Format(
        CultureInfo.InvariantCulture,
        "{0}://{1}:{2}{3}",
        address.Scheme == NinePScheme.Wss ? "wss" : "ws",
        address.Host,
        address.Port,
        address.Path.Length == 0 ? "/" : address.Path));

    private HttpMessageInvoker CreateInvoker(NinePAddress address)
    {
        TlsTransport tls = new(_options.Tls!);

        // CA2000: the handler is owned by the invoker, which the caller of this method disposes.
#pragma warning disable CA2000
        SocketsHttpHandler handler = new()
        {
            SslOptions = tls.ClientAuthenticationOptions(address.Host),
        };
#pragma warning restore CA2000

        return new HttpMessageInvoker(handler, disposeHandler: true);
    }
}
