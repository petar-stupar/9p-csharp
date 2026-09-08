namespace NineP.Protocol.Messages;

/// <summary>
/// Confirms an attribute change (reference §4.6).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
public readonly record struct Rsetattr(ushort Tag)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rsetattr;
}
