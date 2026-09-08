using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NineP.Protocol.Transports.Internal;

/// <summary>
/// The hand-written half of the RFC 6455 upgrade (S-1). It exists because the origin allow-list and
/// the peer identity both need the request headers, which no framed WebSocket API on either target
/// framework exposes; once the response has gone out, the socket is handed to
/// <c>WebSocket.CreateFromStream</c> and this code is done.
/// </summary>
internal static class WebSocketHandshake
{
    /// <summary>The RFC 6455 §4.2.2 magic value the accept token is computed over.</summary>
    public const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>The largest request head the server will read before giving up (a slowloris cap).</summary>
    public const int MaxHeaderBytes = 8192;

    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    /// <summary>The <c>Sec-WebSocket-Accept</c> value for a client's key.</summary>
    /// <param name="key">The <c>Sec-WebSocket-Key</c> the client sent.</param>
    /// <returns>Base64 of SHA-1 over the key concatenated with the RFC's GUID.</returns>
    public static string AcceptFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        // SHA-1 here is not a security decision: RFC 6455 §1.3 fixes this exact construction, and
        // it proves only that the peer read the request, not that it is trusted.
#pragma warning disable CA5350
        byte[] digest = SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptGuid));
#pragma warning restore CA5350
        return Convert.ToBase64String(digest);
    }

    /// <summary>True when a key is the base64 of exactly sixteen bytes, as RFC 6455 §4.1 requires.</summary>
    /// <param name="key">The key from the request, or null.</param>
    /// <returns>False for a missing or malformed key.</returns>
    public static bool IsLegalKey(string? key)
    {
        if (key is null)
        {
            return false;
        }

        Span<byte> nonce = stackalloc byte[18];
        return Convert.TryFromBase64String(key, nonce, out int written) && written == 16;
    }

    /// <summary>Reads the request head, stopping at the blank line or at the header cap.</summary>
    /// <param name="stream">The accepted stream, TLS already established when the scheme is wss.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The parsed request, or null when the peer sent something that is not one.</returns>
    public static async ValueTask<HandshakeRequest?> ReadRequestAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] head = new byte[MaxHeaderBytes];
        int filled = 0;

        while (filled < head.Length)
        {
            int read = await stream.ReadAsync(head.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            filled += read;
            int end = head.AsSpan(0, filled).IndexOf(HeaderTerminator);
            if (end >= 0)
            {
                return filled == end + HeaderTerminator.Length
                    ? Parse(Encoding.ASCII.GetString(head, 0, end))
                    : null; // Bytes after the head mean the peer started framing before the upgrade.
            }
        }

        return null;
    }

    /// <summary>Writes the 101 response that completes the upgrade.</summary>
    /// <param name="stream">The accepted stream.</param>
    /// <param name="accept">The value of <c>Sec-WebSocket-Accept</c>.</param>
    /// <param name="subprotocol">The subprotocol to echo, or null to echo none.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the response has been flushed.</returns>
    public static ValueTask WriteAcceptAsync(
        Stream stream, string accept, string? subprotocol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        StringBuilder response = new();
        response.Append("HTTP/1.1 101 Switching Protocols\r\n");
        response.Append("Upgrade: websocket\r\n");
        response.Append("Connection: Upgrade\r\n");
        response.Append(CultureInfo.InvariantCulture, $"Sec-WebSocket-Accept: {accept}\r\n");

        if (subprotocol is not null)
        {
            response.Append(CultureInfo.InvariantCulture, $"Sec-WebSocket-Protocol: {subprotocol}\r\n");
        }

        response.Append("\r\n");
        return WriteAsciiAsync(stream, response.ToString(), cancellationToken);
    }

    /// <summary>Writes a refusal and nothing else; the caller then closes the socket.</summary>
    /// <param name="stream">The accepted stream.</param>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="reason">The reason phrase, which carries no detail about the peer.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the response has been flushed.</returns>
    public static ValueTask WriteRefusalAsync(
        Stream stream, int status, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        string response = string.Format(
            CultureInfo.InvariantCulture,
            "HTTP/1.1 {0} {1}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n",
            status,
            reason);

        return WriteAsciiAsync(stream, response, cancellationToken);
    }

    private static async ValueTask WriteAsciiAsync(
        Stream stream, string text, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static HandshakeRequest? Parse(string head)
    {
        string[] lines = head.Split("\r\n");
        string[] start = lines[0].Split(' ');
        if (start.Length != 3)
        {
            return null;
        }

        Dictionary<string, string> headers = new(StringComparer.Ordinal);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                return null;
            }

            // CA1308: §5.5 states that PeerIdentity.Headers carries lower-cased keys, and the
            // lookups in HandshakeRequest are written against that; these names are never
            // round-tripped or compared to a user's uppercased input.
#pragma warning disable CA1308
            string name = lines[i][..colon].Trim().ToLowerInvariant();
#pragma warning restore CA1308
            string value = lines[i][(colon + 1)..].Trim();

            // A repeated header is joined the way RFC 9110 §5.3 allows, so Connection: keep-alive
            // followed by Connection: Upgrade still reads as an upgrade.
            headers[name] = headers.TryGetValue(name, out string? existing) ? existing + ", " + value : value;
        }

        return new HandshakeRequest(start[0], start[1], headers);
    }
}
