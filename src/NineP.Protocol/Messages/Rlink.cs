namespace NineP.Protocol.Messages;

/// <summary>
/// Confirms that a hard link has been created (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
public readonly record struct Rlink(ushort Tag)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rlink;
}
