namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Tstatfs (reference §4.9).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Stat">The filesystem statistics.</param>
public readonly record struct Rstatfs(ushort Tag, StatFs Stat)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rstatfs;
}
