namespace NineP.Protocol.Messages;

/// <summary>
/// The <c>Tgetattr.request_mask</c> and <c>Rgetattr.valid</c> bits (reference §4.6). A server
/// always sends the full 160-byte reply; fields it did not mark valid carry zero.
/// </summary>
[Flags]
public enum GetAttrMask : ulong
{
    /// <summary>Nothing is requested or valid.</summary>
    None = 0,

    /// <summary>The POSIX mode word.</summary>
    Mode = 0x1,

    /// <summary>The hard-link count.</summary>
    NLink = 0x2,

    /// <summary>The numeric owner.</summary>
    Uid = 0x4,

    /// <summary>The numeric group.</summary>
    Gid = 0x8,

    /// <summary>The device numbers of a device node.</summary>
    Rdev = 0x10,

    /// <summary>The last access time.</summary>
    ATime = 0x20,

    /// <summary>The last modification time.</summary>
    MTime = 0x40,

    /// <summary>The last status-change time.</summary>
    CTime = 0x80,

    /// <summary>The inode number, which 9P carries as the qid path.</summary>
    Ino = 0x100,

    /// <summary>The file length.</summary>
    Size = 0x200,

    /// <summary>The allocated 512-byte block count.</summary>
    Blocks = 0x400,

    /// <summary>The creation time.</summary>
    BTime = 0x800,

    /// <summary>The generation number.</summary>
    Gen = 0x1000,

    /// <summary>The data version.</summary>
    DataVersion = 0x2000,

    /// <summary>Everything through <see cref="Blocks"/>: what a stat(2) needs.</summary>
    Basic = 0x7FF,

    /// <summary>Every defined bit.</summary>
    All = 0x3FFF,
}
