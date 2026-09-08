namespace NineP.Protocol.Messages;

/// <summary>
/// The error reply of 9P2000 and 9P2000.u (reference §3.1). A .L session never carries one; it
/// carries Rlerror instead.
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Ename">The Plan 9 error text, at most ERRMAX - 1 bytes.</param>
/// <param name="Errno">The Linux errno; 0 means none, which is what a 9P2000 reply carries. On the
/// wire in .u only, where the encoder refuses a negative value and the decoder rejects one above
/// <c>int.MaxValue</c> as <see cref="ProtocolErrorKind.Overflow"/>.</param>
public readonly record struct Rerror(ushort Tag, string Ename, int Errno)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rerror;
}
