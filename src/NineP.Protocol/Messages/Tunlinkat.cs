namespace NineP.Protocol.Messages;

/// <summary>
/// Removes by directory fid and name (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="DirFid">The fid naming the directory.</param>
/// <param name="Name">The name to remove.</param>
/// <param name="Flags">AT_REMOVEDIR (0x200) to remove a directory.</param>
public readonly record struct Tunlinkat(ushort Tag, uint DirFid, string Name, uint Flags)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tunlinkat;
}
