namespace NineP.Protocol.Messages;

/// <summary>
/// Reads bytes from an open fid (reference §5.6).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The open fid to read from.</param>
/// <param name="Offset">The byte offset to read from.</param>
/// <param name="Count">How many bytes the client wants; the server may return fewer.</param>
public readonly record struct Tread(ushort Tag, uint Fid, ulong Offset, uint Count)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tread;
}
