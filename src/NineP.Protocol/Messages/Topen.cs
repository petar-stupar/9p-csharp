namespace NineP.Protocol.Messages;

/// <summary>
/// Opens an existing file (reference §5.5). The access mode is the low two bits of Mode; OAPPEND is
/// a flag.
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid naming the file to open.</param>
/// <param name="Mode">The open mode of reference §4.5.</param>
public readonly record struct Topen(ushort Tag, uint Fid, byte Mode)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Topen;
}
