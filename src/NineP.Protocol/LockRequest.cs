namespace NineP.Protocol;

/// <summary>A byte-range lock request; a <c>Length</c> of 0 means "to the end of the file".</summary>
/// <param name="Type">Read, write, or unlock.</param>
/// <param name="Flags">Whether the client will retry on BLOCKED.</param>
/// <param name="Start">The first byte of the range.</param>
/// <param name="Length">The length of the range, or 0 for "to the end of the file".</param>
/// <param name="ProcId">The client's process identifier, which owns the lock.</param>
/// <param name="ClientId">The client's identifier, which scopes <paramref name="ProcId"/>.</param>
public readonly record struct LockRequest(
    LockType Type, LockFlags Flags, ulong Start, ulong Length, uint ProcId, string ClientId);
