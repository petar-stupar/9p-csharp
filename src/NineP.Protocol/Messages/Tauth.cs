namespace NineP.Protocol.Messages;

/// <summary>
/// Opens an afid for the authentication exchange (reference §5.2). What flows over the afid is not
/// 9P's business.
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Afid">The fid the client wants the authentication file on.</param>
/// <param name="Uname">The user name the client claims.</param>
/// <param name="Aname">The tree the client intends to attach to.</param>
/// <param name="NUname">The numeric user id; NONUNAME when unspecified. On the wire in .u and .L
/// only: a 9P2000 frame encodes nothing and decodes as NONUNAME.</param>
public readonly record struct Tauth(ushort Tag, uint Afid, string Uname, string Aname, uint NUname)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tauth;
}
