namespace NineP.Protocol.Messages;

/// <summary>
/// Accepts an authentication exchange and names the file behind the afid (reference §5.2).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Aqid">The qid of the authentication file; its type carries QTAUTH.</param>
public readonly record struct Rauth(ushort Tag, Qid Aqid)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rauth;
}
