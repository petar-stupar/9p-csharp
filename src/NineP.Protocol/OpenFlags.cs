namespace NineP.Protocol;

/// <summary>
/// Flags that modify an open without changing its access mode (reference §4.5). OAPPEND is one of
/// these, not an access mode: OREAD plus OAPPEND stays a read open.
/// </summary>
[Flags]
public enum OpenFlags
{
    /// <summary>No flag is set.</summary>
    None = 0,

    /// <summary>Truncate the file first; needs write permission.</summary>
    Truncate = 1,

    /// <summary>Remove the file when the fid is clunked; needs remove permission in the parent.</summary>
    RemoveOnClose = 2,

    /// <summary>Writes on this fid go to the end of the file.</summary>
    Append = 4,

    /// <summary>Fail the open if the file already exists (.L O_EXCL on create).</summary>
    Exclusive = 8,

    /// <summary>Fail the open unless the file is a directory (.L O_DIRECTORY).</summary>
    Directory = 16,

    /// <summary>Fail the open if the final component is a symbolic link (.L O_NOFOLLOW).</summary>
    NoFollow = 32,
}
