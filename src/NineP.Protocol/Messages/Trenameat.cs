namespace NineP.Protocol.Messages;

/// <summary>
/// Renames by directory fid and name, without a fid on the file itself (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="OldDirFid">The fid naming the source directory.</param>
/// <param name="OldName">The name in the source directory.</param>
/// <param name="NewDirFid">The fid naming the destination directory.</param>
/// <param name="NewName">The name in the destination directory.</param>
public readonly record struct Trenameat(
    ushort Tag, uint OldDirFid, string OldName, uint NewDirFid, string NewName)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Trenameat;
}
