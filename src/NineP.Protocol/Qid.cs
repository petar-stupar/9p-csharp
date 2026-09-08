namespace NineP.Protocol;

#pragma warning restore CA1008

/// <summary>A 9P qid: <c>type[1] version[4] path[8]</c> (reference §4.1).</summary>
/// <param name="Type">The type bits, which mirror the high 8 bits of the file's mode word.</param>
/// <param name="Version">A number that changes whenever the file changes.</param>
/// <param name="Path">The identity of the file within the server, unique for the server's life.</param>
public readonly record struct Qid(QidType Type, uint Version, ulong Path)
{
    /// <summary>The 13 bytes this qid occupies on the wire.</summary>
    public const int WireSize = 13;
}
