namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Tlock with the outcome; BLOCKED asks the client to retry (reference §4.8).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Status">Whether the lock was granted, blocked, refused, or in grace.</param>
public readonly record struct Rlock(ushort Tag, LockStatus Status)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rlock;
}
