namespace NineP.Protocol.Messages;

/// <summary>
/// Forgets a fid (reference §5.7). The fid is gone whatever the reply says.
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid to forget.</param>
public readonly record struct Tclunk(ushort Tag, uint Fid)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tclunk;
}
