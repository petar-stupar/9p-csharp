namespace NineP.Protocol.Messages;

/// <summary>
/// Proposes a dialect and a message size (reference §3.1). It resets the session: every fid is
/// clunked and every outstanding request is abandoned.
/// </summary>
/// <param name="Tag">The message tag; NOTAG by convention, though any tag is accepted.</param>
/// <param name="Msize">The largest frame the client is willing to handle.</param>
/// <param name="Version">The dialect string the client prefers, for example "9P2000.L".</param>
public readonly record struct Tversion(ushort Tag, uint Msize, string Version)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tversion;
}
