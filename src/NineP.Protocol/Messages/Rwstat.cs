namespace NineP.Protocol.Messages;

/// <summary>
/// Confirms that a file has been changed (reference §5.8).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
public readonly record struct Rwstat(ushort Tag)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rwstat;
}
