namespace NineP.Protocol.Messages;

/// <summary>
/// Confirms that a request has been abandoned; the client may reuse the old tag only now (reference
/// §8 rule 14).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
public readonly record struct Rflush(ushort Tag)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rflush;
}
