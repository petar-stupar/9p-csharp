namespace NineP.Protocol.Messages;

/// <summary>
/// Introduces a fid to the root of a tree, as the user the afid authenticated (reference §5.2).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The fid to bind to the root of the tree.</param>
/// <param name="Afid">The afid from a completed Tauth, or NOFID when not authenticating.</param>
/// <param name="Uname">The user name the client claims; the session identity comes from the
/// authenticator.</param>
/// <param name="Aname">The tree to attach to.</param>
/// <param name="NUname">The numeric user id; NONUNAME when unspecified. On the wire in .u and .L
/// only: a 9P2000 frame encodes nothing and decodes as NONUNAME.</param>
public readonly record struct Tattach(
    ushort Tag, uint Fid, uint Afid, string Uname, string Aname, uint NUname)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tattach;
}
