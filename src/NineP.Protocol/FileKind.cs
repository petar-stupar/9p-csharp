namespace NineP.Protocol;

/// <summary>The dialect-neutral file type a handler declares (reference §7).</summary>
public enum FileKind
{
    /// <summary>A regular file.</summary>
    File = 0,

    /// <summary>A directory.</summary>
    Directory = 1,

    /// <summary>A symbolic link; only .u and .L can represent one.</summary>
    Symlink = 2,

    /// <summary>A named pipe.</summary>
    Fifo = 3,

    /// <summary>A Unix domain socket.</summary>
    Socket = 4,

    /// <summary>A character device.</summary>
    CharDevice = 5,

    /// <summary>A block device.</summary>
    BlockDevice = 6,
}
