namespace NineP.Protocol.Messages;

/// <summary>
/// Moves a fid through the tree one element at a time (reference §5.4). At most MAXWELEM (16)
/// elements per message.
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid to walk from; it must not be open.</param>
/// <param name="NewFid">The fid to bind to the result; it may equal Fid.</param>
/// <param name="Wnames">The path elements, at most 16; ".." is legal here and nowhere else.</param>
public readonly record struct Twalk(ushort Tag, uint Fid, uint NewFid, IReadOnlyList<string> Wnames)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Twalk;
}
