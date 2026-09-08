namespace NineP.Protocol.Messages;

/// <summary>
/// Confirms that a file has been committed (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
public readonly record struct Rfsync(ushort Tag)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rfsync;
}
