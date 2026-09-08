namespace NineP.Protocol;

/// <summary>The machine-readable classification of a codec failure (workspace architecture §3).</summary>
public enum ProtocolErrorKind
{
    /// <summary><c>size</c> is below 7, above the active bound, or the frame is short.</summary>
    Size,

    /// <summary>A counted field runs past the end of the frame.</summary>
    Bounds,

    /// <summary>A string is not valid UTF-8.</summary>
    Utf8,

    /// <summary>A string contains a NUL byte.</summary>
    Nul,

    /// <summary>A name contains '/', is ".", or exceeds 255 bytes.</summary>
    Name,

    /// <summary><c>nwname</c> or <c>nwqid</c> exceeds MAXWELEM (16).</summary>
    NWName,

    /// <summary>Bytes remain after the last field of the message.</summary>
    Trailing,

    /// <summary>
    /// An arithmetic overflow: offset plus count, a size, a lock's start plus length, or an
    /// <c>errno[4]</c> / <c>ecode[4]</c> above <c>int.MaxValue</c>, which no signed errno can hold.
    /// </summary>
    Overflow,

    /// <summary>A <c>stat[n]</c> whose inner <c>size[2]</c> disagrees with <c>n - 2</c>.</summary>
    Stat,

    /// <summary>An unknown type number, or one illegal for the session dialect.</summary>
    Type,
}
