namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Txattrwalk with the size of the attribute (reference §5.9).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Size">The number of bytes the attribute holds.</param>
public readonly record struct Rxattrwalk(ushort Tag, ulong Size)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rxattrwalk;
}
