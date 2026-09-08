namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Tsymlink with the qid of the new link (reference §3.4).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Qid">The qid of the link that was created.</param>
public readonly record struct Rsymlink(ushort Tag, Qid Qid)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rsymlink;
}
