using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Transports;

namespace NineP.Server;

/// <summary>Everything a server needs; init-only, with the defaults of the architecture §4.</summary>
public sealed record ServerOptions
{
    /// <summary>Addresses to bind. At least one is required.</summary>
    public required IReadOnlyList<NinePAddress> Listen { get; init; }

    /// <summary>Transports that own those schemes; the defaults cover tcp, tls, ws and wss.</summary>
    public IReadOnlyList<ITransport> Transports { get; init; } = [];

    /// <summary>
    /// The dialects this server will negotiate. Default: all three. Every step of reference §5.1
    /// is gated on this set, so a dialect left out here is never answered with (R-2).
    /// </summary>
    public IReadOnlySet<Dialect> Dialects { get; init; } =
        new HashSet<Dialect> { Dialect.P9_2000, Dialect.P9_2000_u, Dialect.P9_2000_L };

    /// <summary>The authenticator; null refuses <c>Tauth</c> and admits NOFID attaches.</summary>
    public IAuthenticator? Authenticator { get; init; }

    /// <summary>Resource bounds. Default <see cref="Limits.Default"/>.</summary>
    public Limits Limits { get; init; } = Limits.Default;

    /// <summary>Where the server logs; never a global. Default <see cref="NullLogger.Instance"/>.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <summary>Clock for timeouts, qid versions and server-set times. Default the system clock.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Optional per-request log hook; strings reach it escaped and capped.</summary>
    public IRequestLogSink? RequestLog { get; init; }
}
