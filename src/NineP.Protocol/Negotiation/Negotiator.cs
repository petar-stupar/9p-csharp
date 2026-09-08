namespace NineP.Protocol.Negotiation;

/// <summary>
/// The version negotiation function of reference §5.1, conditioned on the configured dialect set.
/// Every step is gated on that set: a dialect the server was told not to speak is never answered
/// with, however the client asks (R-2). The refusals are all <c>Rversion "unknown"</c> because
/// version(5) forbids <c>Rerror</c> for <c>Tversion</c> and forbids answering with an msize larger
/// than the client's, which rules out replying with the floor.
/// </summary>
public static class Negotiator
{
    /// <summary>Answers a Tversion: every step is gated on the configured set (reference §5.1, R-2).</summary>
    /// <param name="configured">The dialects this side was configured to speak.</param>
    /// <param name="clientVersion">The version string the client sent.</param>
    /// <param name="clientMsize">The msize the client sent.</param>
    /// <param name="limits">The bounds this side was configured with.</param>
    /// <returns>What the <c>Rversion</c> must carry.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static NegotiationResult Negotiate(
        IReadOnlySet<Dialect> configured, string clientVersion, uint clientMsize, Limits limits)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(clientVersion);
        ArgumentNullException.ThrowIfNull(limits);

        // Below the floor the per-message payload collapses, so the session is declined rather
        // than served badly — and it is declined with "unknown", echoing the client's own msize.
        if (clientMsize < limits.MinMsize)
        {
            return Unknown(clientMsize);
        }

        uint msize = Math.Min(clientMsize, limits.MaxMsize);

        if (Offers(configured, clientVersion, Constants.Version9P2000L, Dialect.P9_2000_L))
        {
            return new NegotiationResult(Constants.Version9P2000L, msize, Dialect.P9_2000_L);
        }

        if (Offers(configured, clientVersion, Constants.Version9P2000u, Dialect.P9_2000_u))
        {
            return new NegotiationResult(Constants.Version9P2000u, msize, Dialect.P9_2000_u);
        }

        // version(5):70-78: a period-separated suffix is stripped and the base version answered,
        // but only when the base version is one this side was configured to speak.
        if (clientVersion.StartsWith(Constants.Version9P2000, StringComparison.Ordinal)
            && configured.Contains(Dialect.P9_2000))
        {
            return new NegotiationResult(Constants.Version9P2000, msize, Dialect.P9_2000);
        }

        return Unknown(clientMsize);
    }

    /// <summary>The wire string for a dialect ("9P2000", "9P2000.u", "9P2000.L").</summary>
    /// <param name="dialect">The dialect to name.</param>
    /// <returns>The string that goes on the wire.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a dialect.</exception>
    public static string VersionString(Dialect dialect) => dialect switch
    {
        Dialect.P9_2000 => Constants.Version9P2000,
        Dialect.P9_2000_u => Constants.Version9P2000u,
        Dialect.P9_2000_L => Constants.Version9P2000L,
        _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "not a dialect"),
    };

    /// <summary>Parses an exact dialect string; false for anything else, including "unknown".</summary>
    /// <param name="version">The string from the wire.</param>
    /// <param name="dialect">The dialect it names, when it names one.</param>
    /// <returns>True only for an exact match on one of the three strings.</returns>
    public static bool TryParseVersion(string version, out Dialect dialect)
    {
        switch (version)
        {
            case Constants.Version9P2000:
                dialect = Dialect.P9_2000;
                return true;
            case Constants.Version9P2000u:
                dialect = Dialect.P9_2000_u;
                return true;
            case Constants.Version9P2000L:
                dialect = Dialect.P9_2000_L;
                return true;
            default:
                dialect = default;
                return false;
        }
    }

    private static bool Offers(
        IReadOnlySet<Dialect> configured, string clientVersion, string wanted, Dialect dialect) =>
        string.Equals(clientVersion, wanted, StringComparison.Ordinal) && configured.Contains(dialect);

    private static NegotiationResult Unknown(uint clientMsize) =>
        new(Constants.VersionUnknown, clientMsize, null);
}
