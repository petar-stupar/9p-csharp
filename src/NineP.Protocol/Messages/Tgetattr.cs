namespace NineP.Protocol.Messages;

/// <summary>
/// Asks for the attributes of a file (reference §4.6).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid naming the file.</param>
/// <param name="RequestMask">Which attributes the client wants; the reply is always the full 160
/// bytes.</param>
public readonly record struct Tgetattr(ushort Tag, uint Fid, GetAttrMask RequestMask)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tgetattr;
}
