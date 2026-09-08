namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Tlopen with the qid and the iounit (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Qid">The qid of the file that was opened.</param>
/// <param name="Iounit">The largest payload the server guarantees to move in one message.</param>
public readonly record struct Rlopen(ushort Tag, Qid Qid, uint Iounit)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rlopen;
}
