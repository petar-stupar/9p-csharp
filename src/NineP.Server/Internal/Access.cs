using NineP.Protocol;
using NineP.Protocol.Auth;

namespace NineP.Server.Internal;

/// <summary>The three permission bits a request needs (intro(5) "ACCESS PERMISSIONS").</summary>
[Flags]
internal enum Access
{
    /// <summary>Nothing is required.</summary>
    None = 0,

    /// <summary>Execute, which on a directory means "search".</summary>
    Execute = 1,

    /// <summary>Write.</summary>
    Write = 2,

    /// <summary>Read.</summary>
    Read = 4,
}
