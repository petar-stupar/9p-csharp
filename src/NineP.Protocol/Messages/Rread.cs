namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Tread with the bytes that were read; a short reply is not an error (reference §5.6).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Data">
/// The bytes read; the wire <c>count</c> is its length. The memory <b>aliases the decoded
/// frame</b> and is valid only until that frame is released — for a server, the end of the
/// handler call; for a client, the return of the transaction that produced it. A caller who
/// keeps it copies (workspace architecture §12 rule 5).
/// </param>
public readonly record struct Rread(ushort Tag, ReadOnlyMemory<byte> Data)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rread;
}
