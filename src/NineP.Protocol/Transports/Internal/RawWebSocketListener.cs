using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NineP.Protocol.Internal;
using NineP.Protocol.Transports;

namespace NineP.Protocol.Transports.Internal;

/// <summary>
/// The WebSocket server of S-1: a raw <see cref="Socket"/> listener, optionally wrapped in TLS, a
/// hand-written RFC 6455 upgrade and then
/// <c>WebSocket.CreateFromStream(stream, isServer: true, …)</c>. <c>HttpListener</c> is not used
/// (S-2): its <c>https</c> prefix cannot complete a TLS handshake on macOS, and only the raw path
/// exposes the request headers the origin allow-list and the peer identity need.
/// </summary>
internal sealed class RawWebSocketListener : INinePListener
{
    private readonly HandshakeListener _handshakes;
    private readonly WebSocketTransportOptions _options;
    private readonly TlsTransport? _tls;
    private readonly ILogger _logger;
    private readonly NinePAddress _address;

    /// <summary>Binds a listening socket for a <c>ws://</c> or <c>wss://</c> address.</summary>
    /// <param name="address">The address to bind; port 0 lets the kernel choose.</param>
    /// <param name="options">The WebSocket configuration.</param>
    /// <param name="tls">The TLS transport to wrap accepted sockets in, for <c>wss://</c>.</param>
    /// <param name="inner">The bounded TCP listener owned by this adapter.</param>
    public RawWebSocketListener(NinePAddress address, WebSocketTransportOptions options, TlsTransport? tls, INinePListener inner)
    {
        _options = options;
        _tls = tls;
        _logger = options.Logger ?? NullLogger.Instance;

        _address = address with { Port = inner.LocalAddress.Port };
        _handshakes = new HandshakeListener(inner, _address, UpgradeAsync,
            (options.Tcp ?? new TcpTransportOptions()).MaxConnections);

        if (options.AllowedOrigins.Count == 0)
        {
            _logger.WebSocketAcceptsAnyOrigin(UntrustedText.Sanitize(_address.ToString()));
        }
    }

    /// <summary>The address actually bound, carrying the real port when 0 was requested.</summary>
    public NinePAddress LocalAddress => _address;

    /// <summary>
    /// Accepts the next connection whose upgrade succeeds. A refused upgrade closes that one socket
    /// and the listener goes on accepting: a peer with a bad Origin must not stop the service. A
    /// transient accept failure is retried behind a backoff for the same reason; see
    /// <see cref="AcceptPolicy"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The connection, or null once the listener has been disposed.</returns>
    public ValueTask<INinePConnection?> AcceptAsync(CancellationToken cancellationToken = default) =>
        _handshakes.AcceptAsync(cancellationToken);

    /// <summary>Stops accepting and drains pending upgrades and unclaimed connections.</summary>
    /// <returns>A task that completes when listener-owned connections are closed.</returns>
    public ValueTask DisposeAsync() => _handshakes.DisposeAsync();

    private static Dictionary<string, string> SelectedHeaders(HandshakeRequest request)
    {
        string[] wanted = ["origin", "user-agent", "sec-websocket-protocol", "host", "x-forwarded-for"];
        Dictionary<string, string> selected = new(StringComparer.Ordinal);

        foreach (string name in wanted)
        {
            if (request.Header(name) is { } value)
            {
                selected[name] = UntrustedText.Sanitize(value);
            }
        }

        return selected;
    }

    private async ValueTask<INinePConnection?> UpgradeAsync(INinePConnection raw, CancellationToken cancellationToken)
    {
        // CA2000: everything built here is handed to the returned connection, which the caller
        // disposes; every path that returns null disposes the stream and the certificate first.
#pragma warning disable CA2000
        Socket accepted = ((TcpTransportConnection)raw).Socket;
        Stream stream = new NetworkStream(accepted, ownsSocket: false);
#pragma warning restore CA2000
        X509Certificate2? clientCertificate = null;
        bool handedOff = false;

        try
        {
            using CancellationTokenSource budget =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_options.HandshakeTimeout);

            if (_tls is not null)
            {
                System.Net.Security.SslStream secure =
                    await _tls.AuthenticateServerStreamAsync(stream, budget.Token).ConfigureAwait(false);
                stream = secure;
#pragma warning disable CA2000
                clientCertificate = secure.RemoteCertificate is null
                    ? null
                    : X509CertificateLoader2.FromCertificate(secure.RemoteCertificate);
#pragma warning restore CA2000
            }

            HandshakeRequest? request = await WebSocketHandshake
                .ReadRequestAsync(stream, budget.Token).ConfigureAwait(false);

            if (request is null || !request.IsWebSocketUpgrade)
            {
                await Refuse(stream, 400, "Bad Request", budget.Token).ConfigureAwait(false);
                return null;
            }

            if (!IsOriginAllowed(request.Origin))
            {
                _logger.WebSocketOriginRefused(UntrustedText.Sanitize(request.Origin ?? "<none>"));

                await Refuse(stream, 403, "Forbidden", budget.Token).ConfigureAwait(false);
                return null;
            }

            string? subprotocol = Negotiate(request);
            await WebSocketHandshake
                .WriteAcceptAsync(stream, WebSocketHandshake.AcceptFor(request.Key!), subprotocol, budget.Token)
                .ConfigureAwait(false);

#pragma warning disable CA2000
            WebSocket socket = WebSocket.CreateFromStream(
                stream, isServer: true, subProtocol: subprotocol, keepAliveInterval: _options.KeepAliveInterval);
#pragma warning restore CA2000

            PeerIdentity identity = new()
            {
                ClientCertificate = clientCertificate,
                Origin = request.Origin,
                Headers = SelectedHeaders(request),
                RemoteAddress = accepted.RemoteEndPoint?.ToString(),
            };

            // CA2000: the WebSocket owns the stream and the connection owns the WebSocket; the
            // connection is the caller's to dispose, which is the contract of INinePListener.
#pragma warning disable CA2000
            WebSocketConnection connection = new(
                socket,
                TcpTransport.RemoteAddressOf(accepted, _address.Scheme),
                identity,
                _options.MaxMessageSize, parent: raw);
            handedOff = true;
            return connection;
#pragma warning restore CA2000
        }
        catch (Exception failure) when (failure is IOException or SocketException
            or System.Security.Authentication.AuthenticationException or WebSocketException
            or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            if (!handedOff)
            {
                clientCertificate?.Dispose();
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask Refuse(Stream stream, int status, string reason, CancellationToken cancellationToken)
    {
        await WebSocketHandshake.WriteRefusalAsync(stream, status, reason, cancellationToken).ConfigureAwait(false);
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    private bool IsOriginAllowed(string? origin)
    {
        if (_options.AllowedOrigins.Count == 0)
        {
            return true;
        }

        // A request with no Origin is not a browser request; with an allow-list configured it is
        // still refused, because the allow-list is the whole statement of who may connect.
        return origin is not null && _options.AllowedOrigins.Contains(origin, StringComparer.Ordinal);
    }

    private string? Negotiate(HandshakeRequest request)
    {
        if (_options.Subprotocol is null)
        {
            return null;
        }

        return request.Subprotocols.Contains(_options.Subprotocol, StringComparer.Ordinal)
            ? _options.Subprotocol
            : null;
    }
}
