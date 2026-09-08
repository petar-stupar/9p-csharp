namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Tmkdir with the qid of the new directory (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Qid">The qid of the directory that was created.</param>
public readonly record struct Rmkdir(ushort Tag, Qid Qid)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rmkdir;
}
