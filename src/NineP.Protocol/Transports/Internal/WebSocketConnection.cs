using System.Globalization;
using System.Net.Http;
using System.Net.WebSockets;
using NineP.Protocol.Internal;

namespace NineP.Protocol.Transports.Internal;

/// <summary>
/// One 9P connection over a WebSocket. Reads reassemble a whole message before handing bytes on,
/// with the size cap applied to the running total rather than to the finished message, so a peer
/// cannot make this side buffer more than the cap before being refused.
/// </summary>
internal sealed class WebSocketConnection(
    WebSocket socket, NinePAddress address, PeerIdentity? identity, int maxMessageSize,
    IDisposable? owned = null, INinePConnection? parent = null)
    : INinePConnection
{
    private const int ChunkSize = 8192;

    private byte[] _message = [];
    private int _offset;
    private int _length;
    private int _closed;

    /// <summary>The address of the peer.</summary>
    public NinePAddress RemoteAddress => address;

    /// <summary>The Origin and headers of the upgrade, and a client certificate when wss carried one.</summary>
    public PeerIdentity? PeerIdentity => identity;

    /// <summary>The subprotocol that was negotiated, or null when none was.</summary>
    public string? Subprotocol => socket.SubProtocol;

    /// <summary>Why this end closed, once it has.</summary>
    public CloseReason? LocalCloseReason { get; private set; }

    /// <summary>
    /// Reads up to <c>buffer.Length</c> bytes of the current message, receiving the next one when
    /// the current is exhausted.
    /// </summary>
    /// <param name="buffer">Where the bytes go.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes read; zero once the peer has closed.</returns>
    /// <exception cref="NinePProtocolException">A text message, or one over the size cap.</exception>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset == _length && !await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        int taken = Math.Min(buffer.Length, _length - _offset);
        _message.AsMemory(_offset, taken).CopyTo(buffer);
        _offset += taken;
        return taken;
    }

    /// <summary>Writes exactly one complete 9P message as one binary WebSocket message.</summary>
    /// <param name="message">The frame, <c>size[4]</c> included.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the message has been sent.</returns>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
        socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);

    /// <summary>Closes with the WebSocket status the close reason maps to.</summary>
    /// <param name="reason">Why the connection is closing.</param>
    /// <param name="cancellationToken">Cancels the close handshake.</param>
    /// <returns>A task that completes when the close frame has been sent.</returns>
    public async ValueTask CloseAsync(CloseReason reason, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        LocalCloseReason = reason;

        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket
                    .CloseOutputAsync(StatusFor(reason), reason.ToString(), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (failure is WebSocketException or IOException
            or ObjectDisposedException or OperationCanceledException)
        {
            // The peer is already gone; the close frame has nowhere to go.
        }

        socket.Dispose();
        owned?.Dispose();
        identity?.ClientCertificate?.Dispose();
        if (parent is not null)
        {
            await parent.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Closes the connection normally if it is still open.</summary>
    /// <returns>A task that completes when the connection has closed.</returns>
    public ValueTask DisposeAsync() => CloseAsync(CloseReason.Normal);

    /// <summary>The RFC 6455 status code a close reason travels as.</summary>
    /// <param name="reason">The close reason.</param>
    /// <returns>The status code the peer sees.</returns>
    internal static WebSocketCloseStatus StatusFor(CloseReason reason) => reason switch
    {
        CloseReason.MessageTooLarge => WebSocketCloseStatus.MessageTooBig,
        CloseReason.ProtocolViolation => WebSocketCloseStatus.ProtocolError,
        CloseReason.ResourceLimit => WebSocketCloseStatus.PolicyViolation,
        CloseReason.Shutdown or CloseReason.Timeout => WebSocketCloseStatus.EndpointUnavailable,
        CloseReason.TransportError => WebSocketCloseStatus.InternalServerError,
        _ => WebSocketCloseStatus.NormalClosure,
    };

    private async ValueTask<bool> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        byte[] accumulated = _message.Length >= ChunkSize ? _message : new byte[ChunkSize];

        while (true)
        {
            int total = await ReceiveOneAsync(accumulated, cancellationToken).ConfigureAwait(false)
                is (int received, byte[] grown)
                ? Store(grown, received)
                : -1;

            if (total < 0)
            {
                return false;
            }

            if (total > 0)
            {
                return true;
            }
        }
    }

    private int Store(byte[] accumulated, int total)
    {
        _message = accumulated;
        _offset = 0;
        _length = total;
        return total;
    }

    private async ValueTask<(int Count, byte[] Buffer)?> ReceiveOneAsync(
        byte[] accumulated, CancellationToken cancellationToken)
    {
        int total = 0;

        while (true)
        {
            if (total == accumulated.Length)
            {
                Array.Resize(ref accumulated, Math.Min(accumulated.Length * 2, maxMessageSize + 1));
            }

            ValueWebSocketReceiveResult result;
            try
            {
                result = await socket
                    .ReceiveAsync(accumulated.AsMemory(total), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is WebSocketException or IOException or ObjectDisposedException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                await CloseAsync(CloseReason.ProtocolViolation, CancellationToken.None).ConfigureAwait(false);
                throw new NinePProtocolException(
                    ProtocolErrorKind.Type, "a 9P message never travels as a WebSocket text message");
            }

            total += result.Count;

            // The cap is checked on the running total, while the message is still arriving: by the
            // time the last fragment lands, a peer that meant to exhaust this side already has.
            if (total > maxMessageSize)
            {
                await CloseAsync(CloseReason.MessageTooLarge, CancellationToken.None).ConfigureAwait(false);
                throw new NinePProtocolException(
                    ProtocolErrorKind.Size,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "a WebSocket message exceeded the {0}-byte cap", maxMessageSize));
            }

            if (result.EndOfMessage)
            {
                return (total, accumulated);
            }
        }
    }
}
