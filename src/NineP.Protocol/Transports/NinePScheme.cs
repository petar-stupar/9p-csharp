using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace NineP.Protocol.Transports;

/// <summary>The address schemes a <see cref="NinePAddress"/> may carry (workspace architecture §3).</summary>
public enum NinePScheme
{
    /// <summary>Plain TCP.</summary>
    Tcp,

    /// <summary>TLS over TCP.</summary>
    Tls,

    /// <summary>A WebSocket over plain HTTP.</summary>
    Ws,

    /// <summary>A WebSocket over TLS.</summary>
    Wss,

    /// <summary>A Unix domain socket, where the platform has them.</summary>
    Unix,

    /// <summary>The in-process transport, whose "host" is the endpoint's name.</summary>
    Memory,
}
