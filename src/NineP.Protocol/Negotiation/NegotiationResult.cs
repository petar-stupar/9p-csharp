namespace NineP.Protocol.Negotiation;

/// <summary>
/// The outcome of reference §5.1: the string to answer with, the msize, and the dialect it
/// selects. A result whose version is "unknown" carries no dialect and echoes the client's msize —
/// a server may never answer with a larger one.
/// </summary>
/// <param name="Version">The version string to put in the <c>Rversion</c>.</param>
/// <param name="Msize">The msize to put in the <c>Rversion</c>; never larger than the client's.</param>
/// <param name="Dialect">The dialect the answer selects, or null when none was agreed.</param>
public readonly record struct NegotiationResult(string Version, uint Msize, Dialect? Dialect)
{
    /// <summary>True when Version is "unknown": no dialect was agreed.</summary>
    public bool IsUnknown =>
        string.Equals(Version, Constants.VersionUnknown, StringComparison.Ordinal);
}
