namespace NineP.Protocol.Messages;

/// <summary>
/// Reads the target of a symbolic link (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid naming the link.</param>
public readonly record struct Treadlink(ushort Tag, uint Fid)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Treadlink;
}
