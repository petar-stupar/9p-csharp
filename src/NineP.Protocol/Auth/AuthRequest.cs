namespace NineP.Protocol.Auth;

/// <summary>
/// The triple a <c>Tauth</c> binds an afid to (reference §5.2, S-22). Binding on
/// <c>uname</c> and <c>aname</c> alone would let an afid opened for one numeric user satisfy an
/// attach claiming another in a .u or .L session.
/// </summary>
/// <param name="Uname">The textual user name; empty means "unspecified".</param>
/// <param name="NUname">The numeric user id; NONUNAME means "unspecified".</param>
/// <param name="Aname">The tree the client intends to attach to.</param>
public readonly record struct AuthRequest(string Uname, uint NUname, string Aname);
