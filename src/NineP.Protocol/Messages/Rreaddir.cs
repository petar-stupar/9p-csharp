namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Treaddir with whole directory entries; a count of 0 means the end (reference §4.3).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Data">The packed entries, as a view of the frame; entries are never split.</param>
public readonly record struct Rreaddir(ushort Tag, ReadOnlyMemory<byte> Data)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rreaddir;
}
