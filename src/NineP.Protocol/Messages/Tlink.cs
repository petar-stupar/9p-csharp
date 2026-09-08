namespace NineP.Protocol.Messages;

/// <summary>
/// Creates a hard link (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Dfid">The fid naming the directory to create the link in.</param>
/// <param name="Fid">The fid naming the file to link to.</param>
/// <param name="Name">The name of the new link.</param>
public readonly record struct Tlink(ushort Tag, uint Dfid, uint Fid, string Name)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tlink;
}
