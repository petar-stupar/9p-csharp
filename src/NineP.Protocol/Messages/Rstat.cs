namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Tstat with one stat record (reference §4.2). The record's size appears twice on the
/// wire.
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Stat">The stat record describing the file.</param>
public readonly record struct Rstat(ushort Tag, StatRecord Stat)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rstat;
}
