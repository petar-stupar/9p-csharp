using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NineP.Protocol.Transports.Internal;

/// <summary>The parts of an RFC 6455 upgrade request the server needs in order to answer it.</summary>
/// <param name="Method">The HTTP method; anything but GET is refused.</param>
/// <param name="Target">The request target, which is the path the client dialled.</param>
/// <param name="Headers">Every header, with lower-cased names.</param>
internal sealed record HandshakeRequest(
    string Method, string Target, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>The <c>Origin</c> header, which the allow-list is checked against.</summary>
    public string? Origin => Header("origin");

    /// <summary>The <c>Sec-WebSocket-Key</c> the accept token is derived from.</summary>
    public string? Key => Header("sec-websocket-key");

    /// <summary>The subprotocols the client offered, in its order of preference.</summary>
    public IReadOnlyList<string> Subprotocols =>
        Header("sec-websocket-protocol") is { Length: > 0 } offered
            ? [.. offered.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)]
            : [];

    /// <summary>True when the request is a well-formed version-13 WebSocket upgrade.</summary>
    public bool IsWebSocketUpgrade =>
        string.Equals(Method, "GET", StringComparison.Ordinal)
        && string.Equals(Header("upgrade"), "websocket", StringComparison.OrdinalIgnoreCase)
        && Header("connection")?.Contains("upgrade", StringComparison.OrdinalIgnoreCase) == true
        && string.Equals(Header("sec-websocket-version"), "13", StringComparison.Ordinal)
        && WebSocketHandshake.IsLegalKey(Key);

    /// <summary>One header by its lower-cased name, or null when it was not sent.</summary>
    /// <param name="name">The lower-cased header name.</param>
    /// <returns>The header value, or null.</returns>
    public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;
}
