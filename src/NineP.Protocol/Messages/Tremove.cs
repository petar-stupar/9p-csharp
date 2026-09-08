namespace NineP.Protocol.Messages;

/// <summary>
/// Removes the file a fid names and forgets the fid (reference §5.7). The fid is forgotten even
/// when the removal fails.
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid naming the file to remove.</param>
public readonly record struct Tremove(ushort Tag, uint Fid)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tremove;
}
