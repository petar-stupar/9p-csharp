namespace NineP.Protocol;

/// <summary>A POSIX-style timestamp with nanosecond resolution (reference §3.4, §4.6).</summary>
/// <param name="Seconds">Whole seconds since the Unix epoch.</param>
/// <param name="Nanoseconds">Nanoseconds within the second.</param>
public readonly record struct TimeSpec(long Seconds, uint Nanoseconds);
