namespace NineP.Protocol.Messages;

/// <summary>
/// Creates a file in the directory a fid names and opens the fid onto it (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid naming the directory; it becomes the new file.</param>
/// <param name="Name">The name of the new file.</param>
/// <param name="Flags">Linux open(2) flags for the new fid.</param>
/// <param name="Mode">The POSIX mode; servers mask it with 07777.</param>
/// <param name="Gid">The numeric group the new file belongs to.</param>
public readonly record struct Tlcreate(
    ushort Tag, uint Fid, string Name, uint Flags, uint Mode, uint Gid)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tlcreate;
}
