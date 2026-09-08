namespace NineP.Protocol;

/// <summary>The answer to <c>Tgetlock</c>; <see cref="LockType.Unlock"/> means no conflict.</summary>
/// <param name="Type">The type of the conflicting lock, or Unlock when there is none.</param>
/// <param name="Start">The first byte of the conflicting range.</param>
/// <param name="Length">The length of the conflicting range.</param>
/// <param name="ProcId">The process that holds the conflicting lock.</param>
/// <param name="ClientId">The client that holds the conflicting lock.</param>
public readonly record struct LockQueryResult(
    LockType Type, ulong Start, ulong Length, uint ProcId, string ClientId);
