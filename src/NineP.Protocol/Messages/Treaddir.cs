namespace NineP.Protocol.Messages;

/// <summary>
/// Reads packed directory entries (reference §4.3). In .L this is the only way to read a directory.
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The open directory fid.</param>
/// <param name="Offset">The cookie from the previous entry, or 0 to start.</param>
/// <param name="Count">How many bytes of entries the client wants.</param>
public readonly record struct Treaddir(ushort Tag, uint Fid, ulong Offset, uint Count)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Treaddir;
}
