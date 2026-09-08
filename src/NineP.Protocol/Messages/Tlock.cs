namespace NineP.Protocol.Messages;

/// <summary>
/// Acquires or releases a byte-range lock (reference §4.8).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The open fid to lock.</param>
/// <param name="Request">The range, type and owner of the lock.</param>
public readonly record struct Tlock(ushort Tag, uint Fid, LockRequest Request)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tlock;
}
