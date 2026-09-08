namespace NineP.Protocol;

/// <summary>
/// The access mode of an open fid: the low two bits of reference §4.5, unified across dialects.
/// </summary>
public enum OpenMode
{
    /// <summary>Open for reading.</summary>
    Read = 0,

    /// <summary>Open for writing.</summary>
    Write = 1,

    /// <summary>Open for reading and writing.</summary>
    ReadWrite = 2,

    /// <summary>Open for execution: a read that also checks execute permission.</summary>
    Exec = 3,
}
