namespace NineP.Protocol;

/// <summary>The Linux errno values this workspace's servers use (reference §5.9).</summary>
public static class Errno
{
    /// <summary>Operation not permitted.</summary>
    public const int EPERM = 1;

    /// <summary>No such file or directory.</summary>
    public const int ENOENT = 2;

    /// <summary>Input/output error.</summary>
    public const int EIO = 5;

    /// <summary>
    /// No such device or address: a <c>Tlopen</c> of a fifo, socket or device the server cannot
    /// open (reference §8 rule 23).
    /// </summary>
    public const int ENXIO = 6;

    /// <summary>Bad file descriptor: in 9P, an unknown fid.</summary>
    public const int EBADF = 9;

    /// <summary>Resource temporarily unavailable.</summary>
    public const int EAGAIN = 11;

    /// <summary>Out of memory.</summary>
    public const int ENOMEM = 12;

    /// <summary>Permission denied.</summary>
    public const int EACCES = 13;

    /// <summary>File exists.</summary>
    public const int EEXIST = 17;

    /// <summary>Not a directory.</summary>
    public const int ENOTDIR = 20;

    /// <summary>Is a directory.</summary>
    public const int EISDIR = 21;

    /// <summary>Invalid argument.</summary>
    public const int EINVAL = 22;

    /// <summary>Too many open files in the system: in 9P, the fid cap.</summary>
    public const int ENFILE = 23;

    /// <summary>File too large.</summary>
    public const int EFBIG = 27;

    /// <summary>No space left on device.</summary>
    public const int ENOSPC = 28;

    /// <summary>Read-only filesystem.</summary>
    public const int EROFS = 30;

    /// <summary>Result too large.</summary>
    public const int ERANGE = 34;

    /// <summary>File name too long.</summary>
    public const int ENAMETOOLONG = 36;

    /// <summary>No locks available.</summary>
    public const int ENOLCK = 37;

    /// <summary>Function not implemented.</summary>
    public const int ENOSYS = 38;

    /// <summary>Directory not empty.</summary>
    public const int ENOTEMPTY = 39;

    /// <summary>Too many levels of symbolic links.</summary>
    public const int ELOOP = 40;

    /// <summary>No data available: no such extended attribute.</summary>
    public const int ENODATA = 61;

    /// <summary>Protocol error: a malformed 9P message.</summary>
    public const int EPROTO = 71;

    /// <summary>Value too large for its type.</summary>
    public const int EOVERFLOW = 75;

    /// <summary>Operation not supported.</summary>
    public const int EOPNOTSUPP = 95;

    /// <summary>Connection refused: in 9P, authentication is not required.</summary>
    public const int ECONNREFUSED = 111;
}
