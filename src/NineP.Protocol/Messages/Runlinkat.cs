namespace NineP.Protocol.Messages;

/// <summary>
/// Confirms a removal (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
public readonly record struct Runlinkat(ushort Tag)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Runlinkat;
}
