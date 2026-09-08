namespace NineP.Protocol.Messages;

/// <summary>
/// Prepares a fid to write an extended attribute (reference §5.9).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid naming the file; it becomes the attribute.</param>
/// <param name="Name">The attribute's name.</param>
/// <param name="AttrSize">How many bytes the client will write.</param>
/// <param name="Flags">Whether the attribute must or must not already exist.</param>
public readonly record struct Txattrcreate(
    ushort Tag, uint Fid, string Name, ulong AttrSize, XattrFlags Flags)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Txattrcreate;
}
