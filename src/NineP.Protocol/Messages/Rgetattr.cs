namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Tgetattr; the frame is always 160 bytes and fields not marked valid carry zero
/// (reference §4.6).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Valid">Which of the fields below the server filled in.</param>
/// <param name="Qid">The qid of the file; always valid.</param>
/// <param name="Mode">The POSIX mode word, which is the authority for the file type in .L.</param>
/// <param name="Uid">The numeric owner.</param>
/// <param name="Gid">The numeric group.</param>
/// <param name="NLink">The hard-link count.</param>
/// <param name="Rdev">The device numbers of a device node.</param>
/// <param name="Size">The file length in bytes.</param>
/// <param name="BlkSize">The preferred I/O block size.</param>
/// <param name="Blocks">The allocated 512-byte block count.</param>
/// <param name="ATime">The last access time.</param>
/// <param name="MTime">The last modification time.</param>
/// <param name="CTime">The last status-change time.</param>
/// <param name="BTime">The creation time; zero when unknown.</param>
/// <param name="Gen">The generation number.</param>
/// <param name="DataVersion">The data version.</param>
public readonly record struct Rgetattr(
    ushort Tag, GetAttrMask Valid, Qid Qid, uint Mode, uint Uid, uint Gid, ulong NLink, ulong Rdev,
    ulong Size, ulong BlkSize, ulong Blocks, TimeSpec ATime, TimeSpec MTime, TimeSpec CTime,
    TimeSpec BTime, ulong Gen, ulong DataVersion)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rgetattr;
}
