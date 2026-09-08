namespace NineP.Protocol.Messages;

/// <summary>
/// Asks for statistics about the filesystem behind a fid (reference §4.9).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">Any fid on the filesystem being asked about.</param>
public readonly record struct Tstatfs(ushort Tag, uint Fid)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tstatfs;
}
