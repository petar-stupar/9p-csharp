namespace NineP.Protocol;

/// <summary>
/// The protocol constants of reference §1, under their <c>fcall.h</c> / <c>linux-9p.h</c> names.
/// </summary>
public static class Constants
{
    /// <summary>The tag <c>Tversion</c> and <c>Rversion</c> carry (reference §1).</summary>
    public const ushort NOTAG = 0xFFFF;

    /// <summary>"No fid": the <c>afid</c> of a <c>Tattach</c> that does not authenticate.</summary>
    public const uint NOFID = 0xFFFFFFFF;

    /// <summary>The .u and .L <c>n_uname</c> value meaning "unspecified".</summary>
    public const uint NONUNAME = 0xFFFFFFFF;

    /// <summary>The largest <c>nwname</c> or <c>nwqid</c> one walk may carry (reference §8 rule 2).</summary>
    public const int MAXWELEM = 16;

    /// <summary>The header room reserved for I/O: the largest payload is <c>msize - IOHDRSZ</c>.</summary>
    public const int IOHDRSZ = 24;

    /// <summary>The same allowance for <c>Rreaddir</c> (reference §1).</summary>
    public const int READDIRHDRSZ = 24;

    /// <summary>The conventional cap on the length of an <c>ename</c> (reference §8 rule 10).</summary>
    public const int ERRMAX = 128;

    /// <summary>The fixed part of a 9P2000 stat record, including its leading <c>size[2]</c>.</summary>
    public const int STATFIXLEN = 49;

    /// <summary>The bytes every message spends on <c>size[4] type[1] tag[2]</c>.</summary>
    public const int HDRSZ = 7;

    /// <summary>The <c>Twrite</c> header: <c>size[4] type[1] tag[2] fid[4] offset[8] count[4]</c>.</summary>
    public const int TwriteHeaderSize = 23;

    /// <summary>The <c>Rread</c> header: <c>size[4] type[1] tag[2] count[4]</c>.</summary>
    public const int RreadHeaderSize = 11;

    /// <summary>The longest legal file-name component, in bytes (reference §8 rule 3).</summary>
    public const int MaxNameLength = 255;

    /// <summary>The wire string of the base dialect.</summary>
    public const string Version9P2000 = "9P2000";

    /// <summary>The wire string of the Unix extension.</summary>
    public const string Version9P2000u = "9P2000.u";

    /// <summary>The wire string of the Linux dialect.</summary>
    public const string Version9P2000L = "9P2000.L";

    /// <summary>The reply that means "no dialect was agreed" (reference §5.1).</summary>
    public const string VersionUnknown = "unknown";

    /// <summary>The optional WebSocket subprotocol name a 9P endpoint offers.</summary>
    public const string WebSocketSubprotocol = "9p";
}
