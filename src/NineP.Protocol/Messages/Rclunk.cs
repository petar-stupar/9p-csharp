namespace NineP.Protocol.Messages;

/// <summary>
/// Confirms that a fid has been forgotten (reference §5.7).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
public readonly record struct Rclunk(ushort Tag)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rclunk;
}
