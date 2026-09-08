using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NineP.Protocol;
using NineP.Protocol.Auth;

namespace NineP.Client;

/// <summary>Everything a client session needs; init-only, with the defaults of the architecture §6.</summary>
public sealed record ClientOptions
{
    /// <summary>The msize a .L session asks for by default: 1 MiB (reference §5.1).</summary>
    public const uint DefaultLinuxMsize = 1024 * 1024;

    /// <summary>The msize a 9P2000 or .u session asks for by default: 128 KiB.</summary>
    public const uint DefaultLegacyMsize = 128 * 1024;

    /// <summary>
    /// Dialects to offer, most preferred first. Default: .L, .u, 9P2000. The list is walked: an
    /// <c>Rversion "unknown"</c> refuses the version that was offered and not the connection
    /// (reference §5.1), so the next entry gets its turn.
    /// </summary>
    public IReadOnlyList<Dialect> Dialects { get; init; } =
        [Dialect.P9_2000_L, Dialect.P9_2000_u, Dialect.P9_2000];

    /// <summary>
    /// The least dialect the caller accepts. It gates exactly one answer: the plain
    /// <c>"9P2000"</c> that version(5) lets a server give to a suffixed offer, which is the only
    /// downgrade there is (reference §8 rule 18). Below this floor that answer throws
    /// <see cref="NinePVersionException"/> rather than silently degrading the session. Every other
    /// mismatch — a dialect that is neither the one offered nor the base version, a dialect higher
    /// than was offered, an unknown string — is a <see cref="NinePVersionException"/> whatever
    /// this is set to; a floor of <see cref="Dialect.P9_2000"/> is not permission to accept
    /// anything at all.
    /// </summary>
    public Dialect MinDialect { get; init; } = Dialect.P9_2000;

    /// <summary>The msize to request; the dialect's default when null.</summary>
    public uint? Msize { get; init; }

    /// <summary>The credential driving the afid exchange; a null one attaches with NOFID.</summary>
    public ICredential? Credential { get; init; }

    /// <summary>The textual user name sent in <c>Tauth</c> and <c>Tattach</c>. Default "".</summary>
    public string Uname { get; init; } = "";

    /// <summary>The numeric user id sent in .u and .L. Default NONUNAME.</summary>
    public uint NUname { get; init; } = Constants.NONUNAME;

    /// <summary>The tree name sent in <c>Tauth</c> and <c>Tattach</c>. Default "".</summary>
    public string Aname { get; init; } = "";

    /// <summary>Maximum bytes returned by a whole-file or xattr read. Default 256 MiB; zero permits only empty values.</summary>
    /// <remarks>Checked against metadata before reading and against actual bytes as they arrive.</remarks>
    public int MaxReadAll { get; init; } = 256 * 1024 * 1024;

    /// <summary>Requests kept outstanding during a chunked transfer. Default 4.</summary>
    /// <remarks>Must be at least 1; invalid values are rejected before dialing or negotiation.</remarks>
    public int InFlightWindow { get; init; } = 4;

    /// <summary>How long one request may take before it is flushed and cancelled. Default 60 s.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long <see cref="NinePSession.DisposeAsync"/> may spend clunking the fids that are
    /// still open, in total rather than per fid. Default 5 s. The clunks are issued together and
    /// the effective bound is the smaller of this and <see cref="RequestTimeout"/>; whatever has
    /// not been answered by then is abandoned, because closing the transport makes the server
    /// forget those fids anyway. Zero means no bound, as it does everywhere else here.
    /// </summary>
    public TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long <c>ConnectAsync</c> may take, handshake and Tversion included. Default 30 s.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The bounds this side enforces on incoming frames. Default <see cref="Limits.Default"/>.</summary>
    public Limits Limits { get; init; } = Limits.Default;

    /// <summary>Where the session logs. Default <see cref="NullLogger.Instance"/>.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <summary>The msize this configuration asks for, given the dialect it offers first.</summary>
    /// <returns>The requested msize, clamped to <see cref="Limits"/>.</returns>
    /// <exception cref="InvalidOperationException">No dialect was offered.</exception>
    internal uint RequestedMsize()
    {
        if (Dialects.Count == 0)
        {
            throw new InvalidOperationException("ClientOptions.Dialects must offer at least one dialect");
        }

        uint wanted = Msize
            ?? (Dialects[0] == Dialect.P9_2000_L ? DefaultLinuxMsize : DefaultLegacyMsize);

        return Math.Clamp(wanted, Limits.MinMsize, Limits.MaxMsize);
    }
}
