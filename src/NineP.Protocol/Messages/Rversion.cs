namespace NineP.Protocol.Messages;

/// <summary>
/// Answers a Tversion with the dialect and size the server agrees to, or "unknown" (reference
/// §5.1). An "unknown" reply echoes the client's msize, never the server's.
/// </summary>
/// <param name="Tag">The message tag; NOTAG by convention, though any tag is accepted.</param>
/// <param name="Msize">The agreed size; never larger than the client asked for.</param>
/// <param name="Version">The agreed dialect string, or "unknown".</param>
public readonly record struct Rversion(ushort Tag, uint Msize, string Version)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Rversion;
}
