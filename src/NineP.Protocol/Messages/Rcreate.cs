namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Tcreate with the qid of the new file and the iounit (reference §5.5).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Qid">The qid of the file that was created.</param>
/// <param name="Iounit">The largest payload the server guarantees to move in one message.</param>
public readonly record struct Rcreate(ushort Tag, Qid Qid, uint Iounit)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rcreate;
}
