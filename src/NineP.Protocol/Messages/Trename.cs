namespace NineP.Protocol.Messages;

/// <summary>
/// Renames a file into another directory (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid naming the file to rename.</param>
/// <param name="Dfid">The fid naming the destination directory.</param>
/// <param name="Name">The new name.</param>
public readonly record struct Trename(ushort Tag, uint Fid, uint Dfid, string Name)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Trename;
}
