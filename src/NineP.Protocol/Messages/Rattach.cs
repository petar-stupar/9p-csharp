namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Tattach with the qid of the root of the tree (reference §5.2).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Qid">The qid of the root the fid now names.</param>
public readonly record struct Rattach(ushort Tag, Qid Qid)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rattach;
}
