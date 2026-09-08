namespace NineP.Protocol;

/// <summary>The non-permission mode bits of reference §4.4, dialect-neutral.</summary>
[Flags]
public enum FileFlags
{
    /// <summary>No flag is set.</summary>
    None = 0,

    /// <summary>Append only: writes ignore the offset and OTRUNC is ignored.</summary>
    Append = 1,

    /// <summary>Exclusive use: one open fid at a time.</summary>
    Exclusive = 2,

    /// <summary>Not backed up.</summary>
    Temporary = 4,

    /// <summary>The authentication file behind an afid.</summary>
    Auth = 8,

    /// <summary>A mounted channel.</summary>
    Mount = 16,
}
