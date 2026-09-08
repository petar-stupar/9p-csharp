using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace NineP.Protocol.Transports;

/// <summary>
/// A 9P endpoint URL: <c>tcp://host:port</c>, <c>tls://host:port</c>, <c>ws://host:port/path</c>,
/// <c>wss://host:port/path</c>, <c>unix:///path</c>, <c>memory://name</c>. Parsing is exact — an
/// address this type cannot render back is not an address it accepts.
/// </summary>
/// <param name="Scheme">Which transport owns the endpoint.</param>
/// <param name="Host">The host name or IP literal; the endpoint's name for <c>memory</c>; empty for <c>unix</c>.</param>
/// <param name="Port">The TCP port; 0 asks a listener for whatever port the kernel picks, and is
/// what <c>unix</c> and <c>memory</c> always carry.</param>
/// <param name="Path">The WebSocket path or the socket path; empty otherwise.</param>
public readonly record struct NinePAddress(NinePScheme Scheme, string Host, int Port, string Path)
{
    private const string Separator = "://";
    private const int MaxPort = 65535;

    /// <summary>True when the scheme carries TLS: <c>tls</c> or <c>wss</c>.</summary>
    public bool IsSecure => Scheme is NinePScheme.Tls or NinePScheme.Wss;

    /// <summary>Parses an address URL.</summary>
    /// <param name="value">The URL text.</param>
    /// <returns>The address.</returns>
    /// <exception cref="FormatException">The text is not an address this workspace speaks.</exception>
    public static NinePAddress Parse(string value) =>
        TryParse(value, out NinePAddress address)
            ? address
            : throw new FormatException(string.Format(
                CultureInfo.InvariantCulture, "not a 9P address: '{0}'", value ?? "<null>"));

    /// <summary>Parses an address URL, returning false instead of throwing.</summary>
    /// <param name="value">The URL text.</param>
    /// <param name="address">The address when the text parses; the default otherwise.</param>
    /// <returns>True when the text is a legal address.</returns>
    public static bool TryParse([NotNullWhen(true)] string? value, out NinePAddress address)
    {
        address = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int mark = value.IndexOf(Separator, StringComparison.Ordinal);
        if (mark <= 0 || !TryParseScheme(value[..mark], out NinePScheme scheme))
        {
            return false;
        }

        string rest = value[(mark + Separator.Length)..];
        return scheme switch
        {
            NinePScheme.Memory => TryParseMemory(rest, out address),
            NinePScheme.Unix => TryParseUnix(rest, out address),
            _ => TryParseNetwork(scheme, rest, out address),
        };
    }

    /// <summary>The canonical URL form of this address.</summary>
    /// <returns>The text <see cref="Parse"/> accepts and returns this address for.</returns>
    public override string ToString() => Scheme switch
    {
        NinePScheme.Memory => "memory://" + Host,
        NinePScheme.Unix => "unix://" + Path,
        NinePScheme.Ws or NinePScheme.Wss => string.Format(
            CultureInfo.InvariantCulture, "{0}://{1}:{2}{3}", SchemeText(Scheme), Host, Port, PathOrRoot),
        _ => string.Format(
            CultureInfo.InvariantCulture, "{0}://{1}:{2}", SchemeText(Scheme), Host, Port),
    };

    private string PathOrRoot => Path.Length == 0 ? "/" : Path;

    private static string SchemeText(NinePScheme scheme) => scheme switch
    {
        NinePScheme.Tcp => "tcp",
        NinePScheme.Tls => "tls",
        NinePScheme.Ws => "ws",
        NinePScheme.Wss => "wss",
        NinePScheme.Unix => "unix",
        _ => "memory",
    };

    private static bool TryParseScheme(string text, out NinePScheme scheme)
    {
        switch (text)
        {
            case "tcp": scheme = NinePScheme.Tcp; return true;
            case "tls": scheme = NinePScheme.Tls; return true;
            case "ws": scheme = NinePScheme.Ws; return true;
            case "wss": scheme = NinePScheme.Wss; return true;
            case "unix": scheme = NinePScheme.Unix; return true;
            case "memory": scheme = NinePScheme.Memory; return true;
            default: scheme = default; return false;
        }
    }

    private static bool TryParseMemory(string rest, out NinePAddress address)
    {
        address = default;

        // A memory endpoint is named, not routed: a slash or a colon would only ever be a typo for
        // one of the network forms, and accepting it would make two spellings of one endpoint.
        if (rest.Length == 0 || rest.AsSpan().IndexOfAny('/', ':') >= 0)
        {
            return false;
        }

        address = new NinePAddress(NinePScheme.Memory, rest, 0, string.Empty);
        return true;
    }

    private static bool TryParseUnix(string rest, out NinePAddress address)
    {
        address = default;
        if (rest.Length < 2 || rest[0] != '/')
        {
            return false;
        }

        address = new NinePAddress(NinePScheme.Unix, string.Empty, 0, rest);
        return true;
    }

    private static bool TryParseNetwork(NinePScheme scheme, string rest, out NinePAddress address)
    {
        address = default;

        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        string authority = slash < 0 ? rest : rest[..slash];
        string path = slash < 0 ? string.Empty : rest[slash..];
        bool webSocket = scheme is NinePScheme.Ws or NinePScheme.Wss;

        // Only a WebSocket endpoint has a path; tcp://host:port/anything is a typo, not an address.
        if (path.Length != 0 && !webSocket)
        {
            return false;
        }

        if (!TrySplitAuthority(authority, out string host, out int port))
        {
            return false;
        }

        string canonical = webSocket ? (path.Length == 0 ? "/" : path) : string.Empty;
        address = new NinePAddress(scheme, host, port, canonical);
        return true;
    }

    private static bool TrySplitAuthority(string authority, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        // An IPv6 literal carries colons of its own, so the port separator is the last colon and
        // it has to fall outside the brackets.
        int colon = authority.LastIndexOf(':');
        if (colon <= 0 || colon == authority.Length - 1 || authority.IndexOf(']', StringComparison.Ordinal) > colon)
        {
            return false;
        }

        if (!int.TryParse(authority[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port > MaxPort)
        {
            return false;
        }

        host = authority[..colon];
        return host.Length != 0;
    }
}
