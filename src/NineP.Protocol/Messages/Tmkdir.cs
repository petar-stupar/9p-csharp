namespace NineP.Protocol.Messages;

/// <summary>
/// Creates a directory (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Dfid">The fid naming the parent directory.</param>
/// <param name="Name">The name of the new directory.</param>
/// <param name="Mode">The POSIX mode; servers mask it with 07777.</param>
/// <param name="Gid">The numeric group the directory belongs to.</param>
public readonly record struct Tmkdir(ushort Tag, uint Dfid, string Name, uint Mode, uint Gid)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tmkdir;
}
