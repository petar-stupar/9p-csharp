namespace NineP.Protocol.Messages;

/// <summary>
/// Confirms that a fid is ready to take an attribute's bytes (reference §5.9).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
public readonly record struct Rxattrcreate(ushort Tag)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rxattrcreate;
}
