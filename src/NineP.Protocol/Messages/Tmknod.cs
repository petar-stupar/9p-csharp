namespace NineP.Protocol.Messages;

/// <summary>
/// Creates a device, socket or named pipe (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Dfid">The fid naming the directory to create the node in.</param>
/// <param name="Name">The name of the node.</param>
/// <param name="Mode">The POSIX mode, which carries the file type.</param>
/// <param name="Major">The device major number.</param>
/// <param name="Minor">The device minor number.</param>
/// <param name="Gid">The numeric group the node belongs to.</param>
public readonly record struct Tmknod(
    ushort Tag, uint Dfid, string Name, uint Mode, uint Major, uint Minor, uint Gid)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tmknod;
}
