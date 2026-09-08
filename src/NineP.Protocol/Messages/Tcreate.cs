namespace NineP.Protocol.Messages;

/// <summary>
/// Creates a file in the directory a fid names and opens the fid onto it (reference §5.5).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid naming the directory to create in; it becomes the new file.</param>
/// <param name="Name">The name of the new file; it may not contain '/', be "." or be "..".</param>
/// <param name="Perm">The permission and type bits of reference §4.4.</param>
/// <param name="Mode">The mode the new fid is opened with.</param>
/// <param name="Extension">The symlink target or device specification, present in .u sessions
/// only.</param>
public readonly record struct Tcreate(
    ushort Tag, uint Fid, string Name, uint Perm, byte Mode, string? Extension)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tcreate;
}
