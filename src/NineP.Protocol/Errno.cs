namespace NineP.Protocol;

/// <summary>
/// The Linux errno values this workspace's servers use (reference §5.9): every errno the Linux
/// kernel's 9P client can name (<c>docs/9p/fixtures/linux-9p-errors.json</c>), plus <c>EOVERFLOW</c>.
/// </summary>
public static class Errno
{
    /// <summary>Operation not permitted.</summary>
    public const int EPERM = 1;

    /// <summary>No such file or directory.</summary>
    public const int ENOENT = 2;

    /// <summary>Interrupted system call.</summary>
    public const int EINTR = 4;

    /// <summary>Input/output error.</summary>
    public const int EIO = 5;

    /// <summary>
    /// No such device or address: a <c>Tlopen</c> of a fifo, socket or device the server cannot
    /// open (reference §8 rule 23).
    /// </summary>
    public const int ENXIO = 6;

    /// <summary>Argument list too long.</summary>
    public const int E2BIG = 7;

    /// <summary>Bad file descriptor: in 9P, an unknown fid.</summary>
    public const int EBADF = 9;

    /// <summary>Resource temporarily unavailable.</summary>
    public const int EAGAIN = 11;

    /// <summary>Out of memory.</summary>
    public const int ENOMEM = 12;

    /// <summary>Permission denied.</summary>
    public const int EACCES = 13;

    /// <summary>Bad address.</summary>
    public const int EFAULT = 14;

    /// <summary>Block device required.</summary>
    public const int ENOTBLK = 15;

    /// <summary>Device or resource busy.</summary>
    public const int EBUSY = 16;

    /// <summary>File exists.</summary>
    public const int EEXIST = 17;

    /// <summary>Invalid cross-device link.</summary>
    public const int EXDEV = 18;

    /// <summary>No such device.</summary>
    public const int ENODEV = 19;

    /// <summary>Not a directory.</summary>
    public const int ENOTDIR = 20;

    /// <summary>Is a directory.</summary>
    public const int EISDIR = 21;

    /// <summary>Invalid argument.</summary>
    public const int EINVAL = 22;

    /// <summary>Too many open files in the system: in 9P, the fid cap.</summary>
    public const int ENFILE = 23;

    /// <summary>Too many open files.</summary>
    public const int EMFILE = 24;

    /// <summary>Text file busy.</summary>
    public const int ETXTBSY = 26;

    /// <summary>File too large.</summary>
    public const int EFBIG = 27;

    /// <summary>No space left on device.</summary>
    public const int ENOSPC = 28;

    /// <summary>Illegal seek.</summary>
    public const int ESPIPE = 29;

    /// <summary>Read-only filesystem.</summary>
    public const int EROFS = 30;

    /// <summary>Too many links.</summary>
    public const int EMLINK = 31;

    /// <summary>Broken pipe.</summary>
    public const int EPIPE = 32;

    /// <summary>Numerical argument out of domain.</summary>
    public const int EDOM = 33;

    /// <summary>Result too large.</summary>
    public const int ERANGE = 34;

    /// <summary>Resource deadlock avoided.</summary>
    public const int EDEADLK = 35;

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

    /// <summary>No message of desired type.</summary>
    public const int ENOMSG = 42;

    /// <summary>Identifier removed.</summary>
    public const int EIDRM = 43;

    /// <summary>No data available: no such extended attribute.</summary>
    public const int ENODATA = 61;

    /// <summary>Machine is not on the network.</summary>
    public const int ENONET = 64;

    /// <summary>Package not installed.</summary>
    public const int ENOPKG = 65;

    /// <summary>Object is remote.</summary>
    public const int EREMOTE = 66;

    /// <summary>Link has been severed.</summary>
    public const int ENOLINK = 67;

    /// <summary>Communication error on send.</summary>
    public const int ECOMM = 70;

    /// <summary>Protocol error: a malformed 9P message.</summary>
    public const int EPROTO = 71;

    /// <summary>Bad message.</summary>
    public const int EBADMSG = 74;

    /// <summary>Value too large for its type.</summary>
    public const int EOVERFLOW = 75;

    /// <summary>File descriptor in bad state.</summary>
    public const int EBADFD = 77;

    /// <summary>Streams pipe error.</summary>
    public const int ESTRPIPE = 86;

    /// <summary>Too many users.</summary>
    public const int EUSERS = 87;

    /// <summary>Socket operation on non-socket.</summary>
    public const int ENOTSOCK = 88;

    /// <summary>Message too long.</summary>
    public const int EMSGSIZE = 90;

    /// <summary>Protocol not available.</summary>
    public const int ENOPROTOOPT = 92;

    /// <summary>Protocol not supported.</summary>
    public const int EPROTONOSUPPORT = 93;

    /// <summary>Socket type not supported.</summary>
    public const int ESOCKTNOSUPPORT = 94;

    /// <summary>Operation not supported.</summary>
    public const int EOPNOTSUPP = 95;

    /// <summary>Protocol family not supported.</summary>
    public const int EPFNOSUPPORT = 96;

    /// <summary>Network is down.</summary>
    public const int ENETDOWN = 100;

    /// <summary>Network is unreachable.</summary>
    public const int ENETUNREACH = 101;

    /// <summary>Network dropped connection on reset.</summary>
    public const int ENETRESET = 102;

    /// <summary>Software caused connection abort.</summary>
    public const int ECONNABORTED = 103;

    /// <summary>Connection reset by peer.</summary>
    public const int ECONNRESET = 104;

    /// <summary>No buffer space available.</summary>
    public const int ENOBUFS = 105;

    /// <summary>Transport endpoint is already connected.</summary>
    public const int EISCONN = 106;

    /// <summary>Transport endpoint is not connected.</summary>
    public const int ENOTCONN = 107;

    /// <summary>Cannot send after transport endpoint shutdown.</summary>
    public const int ESHUTDOWN = 108;

    /// <summary>Connection timed out.</summary>
    public const int ETIMEDOUT = 110;

    /// <summary>Connection refused: in 9P, authentication is not required.</summary>
    public const int ECONNREFUSED = 111;

    /// <summary>Host is down.</summary>
    public const int EHOSTDOWN = 112;

    /// <summary>No route to host.</summary>
    public const int EHOSTUNREACH = 113;

    /// <summary>Operation already in progress.</summary>
    public const int EALREADY = 114;

    /// <summary>Operation now in progress.</summary>
    public const int EINPROGRESS = 115;

    /// <summary>Is a named type file.</summary>
    public const int EISNAM = 120;

    /// <summary>Remote I/O error.</summary>
    public const int EREMOTEIO = 121;

    /// <summary>Disk quota exceeded.</summary>
    public const int EDQUOT = 122;
}
