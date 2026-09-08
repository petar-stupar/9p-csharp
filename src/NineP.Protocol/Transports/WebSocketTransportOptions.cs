using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace NineP.Protocol.Transports;

/// <summary>
/// WebSocket configuration (workspace architecture §3, Decision Log WebSocket row). One 9P message
/// travels as one binary WebSocket message, so the size cap here bounds a 9P frame directly.
/// </summary>
public sealed record WebSocketTransportOptions
{
    /// <summary>The subprotocol offered and echoed. Default "9p"; null offers none.</summary>
    public string? Subprotocol { get; init; } = Constants.WebSocketSubprotocol;

    /// <summary>
    /// Origins the server accepts. Empty means "accept any", which is logged as a warning at listen
    /// time: a browser-reachable 9P server with no allow-list is a cross-origin hole.
    /// </summary>
    public IReadOnlyCollection<string> AllowedOrigins { get; init; } = [];

    /// <summary>
    /// The largest message accepted, enforced <b>during</b> accumulation rather than at the end:
    /// a peer must not be able to make the server buffer a gigabyte before it is refused. Over the
    /// cap the socket closes 1009. Default 1 MiB.
    /// </summary>
    public int MaxMessageSize { get; init; } = 1024 * 1024;

    /// <summary>The ping interval. Default 30 s.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The handshake timeout. Default 30 s.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>TLS settings for <c>wss://</c>; ignored for <c>ws://</c>.</summary>
    public TlsTransportOptions? Tls { get; init; }

    /// <summary>The underlying TCP tuning.</summary>
    public TcpTransportOptions? Tcp { get; init; }

    /// <summary>Where handshake refusals are logged; <see cref="NullLogger.Instance"/> when null.</summary>
    public ILogger? Logger { get; init; }
}
