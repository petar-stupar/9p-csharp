namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Twalk with one qid per element that was walked (reference §5.4). Fewer qids than names
/// is a partial walk, and NewFid is not bound.
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Wqids">The qid of each element walked, in order, at most 16.</param>
public readonly record struct Rwalk(ushort Tag, IReadOnlyList<Qid> Wqids)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rwalk;
}
