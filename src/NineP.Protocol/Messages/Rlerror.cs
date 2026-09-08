namespace NineP.Protocol.Messages;

/// <summary>
/// The error reply of 9P2000.L: an errno and nothing else (reference §3.1).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Ecode">The Linux errno, signed like every errno in this API. The encoder refuses
/// a negative value and the decoder rejects one above <c>int.MaxValue</c> as
/// <see cref="ProtocolErrorKind.Overflow"/>.</param>
public readonly record struct Rlerror(ushort Tag, int Ecode)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rlerror;
}
