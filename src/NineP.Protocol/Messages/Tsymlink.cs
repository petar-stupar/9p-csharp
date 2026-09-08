namespace NineP.Protocol.Messages;

/// <summary>
/// Creates a symbolic link (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid naming the directory to create the link in.</param>
/// <param name="Name">The name of the link.</param>
/// <param name="Symtgt">The text the link points at; it is not interpreted by the server.</param>
/// <param name="Gid">The numeric group the link belongs to.</param>
public readonly record struct Tsymlink(ushort Tag, uint Fid, string Name, string Symtgt, uint Gid)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tsymlink;
}
