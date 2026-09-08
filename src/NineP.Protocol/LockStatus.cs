namespace NineP.Protocol;

/// <summary>The status an <c>Rlock</c> carries (reference §4.8).</summary>
public enum LockStatus : byte
{
    /// <summary>The lock was granted or released.</summary>
    Success = 0,

    /// <summary>A conflicting lock is held; the client may retry.</summary>
    Blocked = 1,

    /// <summary>The request could not be honoured.</summary>
    Error = 2,

    /// <summary>The server is in its post-restart grace period.</summary>
    Grace = 3,
}
