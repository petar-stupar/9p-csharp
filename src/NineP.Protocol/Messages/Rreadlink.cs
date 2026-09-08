namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Treadlink with the link's target (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Target">The text the link points at.</param>
public readonly record struct Rreadlink(ushort Tag, string Target)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rreadlink;
}
