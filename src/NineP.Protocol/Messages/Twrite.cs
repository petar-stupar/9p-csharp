namespace NineP.Protocol.Messages;

/// <summary>
/// Writes bytes to an open fid (reference §5.6). The count must equal size - 23 (reference §8 rule
/// 4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The open fid to write to.</param>
/// <param name="Offset">The byte offset to write at; ignored on an append-only file.</param>
/// <param name="Data">
/// The bytes to write; the wire <c>count</c> is its length. On the receiving side the memory
/// <b>aliases the decoded frame</b> and is valid only until that frame is released — the end of
/// the handler call. A caller who keeps it copies (workspace architecture §12 rule 5).
/// </param>
public readonly record struct Twrite(ushort Tag, uint Fid, ulong Offset, ReadOnlyMemory<byte> Data)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Twrite;
}
