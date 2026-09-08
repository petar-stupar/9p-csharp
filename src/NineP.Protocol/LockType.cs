namespace NineP.Protocol;

/// <summary>The POSIX lock type carried by <c>Tlock</c> and <c>Tgetlock</c> (reference §4.8).</summary>
public enum LockType : byte
{
    /// <summary>A shared read lock.</summary>
    ReadLock = 0,

    /// <summary>An exclusive write lock.</summary>
    WriteLock = 1,

    /// <summary>Release a lock; as a <c>Rgetlock</c> answer it means "no conflicting lock".</summary>
    Unlock = 2,
}
