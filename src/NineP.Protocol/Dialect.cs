namespace NineP.Protocol;

/// <summary>
/// The three separately negotiated 9P dialects (reference §2). The workspace's umbrella label
/// "9P2000.uL" means "implements all three" and is never sent on the wire.
/// </summary>
public enum Dialect
{
    /// <summary>Base 9P2000, the Plan 9 protocol of intro(5).</summary>
    P9_2000 = 0,

    /// <summary>9P2000.u, the Unix extension: numeric ids, errno, extension strings.</summary>
    P9_2000_u = 1,

    /// <summary>9P2000.L, the Linux dialect of diod and v9fs.</summary>
    P9_2000_L = 2,
}
