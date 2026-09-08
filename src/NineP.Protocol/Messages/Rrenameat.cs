namespace NineP.Protocol.Messages;

/// <summary>
/// Confirms a rename (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
public readonly record struct Rrenameat(ushort Tag)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rrenameat;
}
