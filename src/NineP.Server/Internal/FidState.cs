using NineP.Protocol;
using NineP.Protocol.Auth;

namespace NineP.Server.Internal;

/// <summary>The states one fid moves through (§6.5).</summary>
internal enum FidState
{
    /// <summary>Bound to a file but not opened.</summary>
    Bound = 0,

    /// <summary>Opened for I/O; walking from it is refused.</summary>
    Open = 1,

    /// <summary>An afid: read, write and clunk only.</summary>
    Auth = 2,
}
