using NineP.Protocol;

namespace NineP.Server;

/// <summary>
/// Optional: POSIX byte-range locks (.L). A handler that does not implement it answers
/// <c>EOPNOTSUPP</c> from the core, never a crash (architecture §4).
/// </summary>
public interface ILockCapability
{
    /// <summary>Acquires or releases a lock.</summary>
    /// <param name="request">The range, type and owner.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the lock was granted, blocked, refused or in grace.</returns>
    ValueTask<LockStatus> LockAsync(LockRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reports the lock that conflicts with a range, if any.</summary>
    /// <param name="request">The range and type the caller would like to take.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The conflicting lock, or <see cref="LockType.Unlock"/> when there is none.</returns>
    ValueTask<LockQueryResult> GetLockAsync(
        LockRequest request, CancellationToken cancellationToken = default);
}
