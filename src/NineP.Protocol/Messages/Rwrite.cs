namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Twrite with how many bytes were written; fewer than asked is a short write, not an
/// error (reference §5.6).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Count">The number of bytes written.</param>
public readonly record struct Rwrite(ushort Tag, uint Count)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rwrite;
}
