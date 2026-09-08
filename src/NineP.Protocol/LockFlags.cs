namespace NineP.Protocol;

/// <summary><c>Tlock</c> flags (reference §4.8). RECLAIM is reserved and never honoured.</summary>
[Flags]
public enum LockFlags
{
    /// <summary>No flag is set.</summary>
    None = 0,

    /// <summary>The client is willing to be told BLOCKED and to retry.</summary>
    Block = 1,

    /// <summary>Reserved by the reference; this workspace never honours it.</summary>
    Reclaim = 2,
}
