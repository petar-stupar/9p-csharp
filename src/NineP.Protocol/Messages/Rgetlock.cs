namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Tgetlock; a type of Unlock means nothing conflicts (reference §4.8).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Result">The conflicting lock, or Unlock when there is none.</param>
public readonly record struct Rgetlock(ushort Tag, LockQueryResult Result)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rgetlock;
}
