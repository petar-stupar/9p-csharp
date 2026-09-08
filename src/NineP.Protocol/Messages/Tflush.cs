namespace NineP.Protocol.Messages;

/// <summary>
/// Abandons an outstanding request (reference §5.3).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="OldTag">The tag of the request being abandoned.</param>
public readonly record struct Tflush(ushort Tag, ushort OldTag)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tflush;
}
