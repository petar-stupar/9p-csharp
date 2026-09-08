namespace NineP.Protocol.Messages;

/// <summary>
/// Changes a file through a stat record (reference §5.8). The change is atomic: all of it or none.
/// A record whose every field is "don't touch" is an fsync request.
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid naming the file to change.</param>
/// <param name="Stat">The fields to change; every other field carries its "don't touch"
/// value.</param>
public readonly record struct Twstat(ushort Tag, uint Fid, StatRecord Stat)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Twstat;
}
