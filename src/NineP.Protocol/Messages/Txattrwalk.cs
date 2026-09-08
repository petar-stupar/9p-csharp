namespace NineP.Protocol.Messages;

/// <summary>
/// Walks a fid onto an extended attribute, or onto the attribute list (reference §5.9).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid naming the file.</param>
/// <param name="NewFid">The fid to bind to the attribute.</param>
/// <param name="Name">The attribute's name, or the empty string for the list of names.</param>
public readonly record struct Txattrwalk(ushort Tag, uint Fid, uint NewFid, string Name)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Txattrwalk;
}
