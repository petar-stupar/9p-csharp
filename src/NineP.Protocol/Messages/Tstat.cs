namespace NineP.Protocol.Messages;

/// <summary>
/// Asks for the stat record of the file a fid names (reference §5.8).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid naming the file.</param>
public readonly record struct Tstat(ushort Tag, uint Fid)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tstat;
}
