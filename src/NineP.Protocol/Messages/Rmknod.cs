namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Tmknod with the qid of the new node (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Qid">The qid of the node that was created.</param>
public readonly record struct Rmknod(ushort Tag, Qid Qid)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rmknod;
}
