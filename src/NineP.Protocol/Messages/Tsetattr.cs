namespace NineP.Protocol.Messages;

/// <summary>
/// Changes the attributes of a file (reference §4.6). A time bit without its _SET twin means "use
/// the server's clock".
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid naming the file.</param>
/// <param name="Valid">Which of the fields below the client is changing.</param>
/// <param name="Mode">The new permission bits.</param>
/// <param name="Uid">The new numeric owner.</param>
/// <param name="Gid">The new numeric group.</param>
/// <param name="Size">The new length: truncate or extend.</param>
/// <param name="ATime">The new access time.</param>
/// <param name="MTime">The new modification time.</param>
public readonly record struct Tsetattr(
    ushort Tag, uint Fid, SetAttrMask Valid, uint Mode, uint Uid, uint Gid, ulong Size,
    TimeSpec ATime, TimeSpec MTime)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tsetattr;
}
