namespace NineP.Protocol.Messages;

/// <summary>
/// Opens an existing file with Linux open(2) flags (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid naming the file to open.</param>
/// <param name="Flags">Linux open(2) flags in their generic (x86) values.</param>
public readonly record struct Tlopen(ushort Tag, uint Fid, uint Flags)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tlopen;
}
