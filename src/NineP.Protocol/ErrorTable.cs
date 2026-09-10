namespace NineP.Protocol;

/// <summary>
/// The errno to ename projection table (workspace architecture §3): Plan 9 wording where one
/// exists, so a 9P2000 client and a 9P2000.L client are told the same thing in their own terms.
/// </summary>
public static class ErrorTable
{
    private static readonly (int Errno, string Ename)[] Table =
    [
        (Errno.EPERM, "Operation not permitted"),
        (Errno.ENOENT, "file not found"),
        (Errno.EINTR, "Interrupted system call"),
        (Errno.EIO, "i/o error"),
        (Errno.ENXIO, "No such device or address"),
        (Errno.E2BIG, "Argument list too long"),
        (Errno.EBADF, "fid unknown or out of range"),
        (Errno.EAGAIN, "Resource temporarily unavailable"),
        (Errno.ENOMEM, "Cannot allocate memory"),
        (Errno.EACCES, "permission denied"),
        (Errno.EFAULT, "Bad address"),
        (Errno.ENOTBLK, "Block device required"),
        (Errno.EBUSY, "Device or resource busy"),
        (Errno.EEXIST, "file already exists"),
        (Errno.EXDEV, "Invalid cross-device link"),
        (Errno.ENODEV, "No such device"),
        (Errno.ENOTDIR, "not a directory"),
        (Errno.EISDIR, "Is a directory"),
        (Errno.EINVAL, "Invalid argument"),
        (Errno.ENFILE, "Too many open files in system"),
        (Errno.EMFILE, "Too many open files"),
        (Errno.ETXTBSY, "Text file busy"),
        (Errno.EFBIG, "file too big"),
        (Errno.ENOSPC, "No space left on device"),
        (Errno.ESPIPE, "Illegal seek"),
        (Errno.EROFS, "Read-only file system"),
        (Errno.EMLINK, "Too many links"),
        (Errno.EPIPE, "Broken pipe"),
        (Errno.EDOM, "Numerical argument out of domain"),
        (Errno.ERANGE, "Numerical result out of range"),
        (Errno.EDEADLK, "Resource deadlock avoided"),
        (Errno.ENAMETOOLONG, "File name too long"),
        (Errno.ENOLCK, "No locks available"),
        (Errno.ENOSYS, "Function not implemented"),
        (Errno.ENOTEMPTY, "Directory not empty"),
        (Errno.ELOOP, "Too many levels of symbolic links"),
        (Errno.ENOMSG, "No message of desired type"),
        (Errno.EIDRM, "Identifier removed"),
        (Errno.ENODATA, "No data available"),
        (Errno.ENONET, "Machine is not on the network"),
        (Errno.ENOPKG, "Package not installed"),
        (Errno.EREMOTE, "Object is remote"),
        (Errno.ENOLINK, "Link has been severed"),
        (Errno.ECOMM, "Communication error on send"),
        (Errno.EPROTO, "protocol botch"),
        (Errno.EBADMSG, "Bad message"),
        (Errno.EOVERFLOW, "Value too large for defined data type"),
        (Errno.EBADFD, "File descriptor in bad state"),
        (Errno.ESTRPIPE, "Streams pipe error"),
        (Errno.EUSERS, "Too many users"),
        (Errno.ENOTSOCK, "Socket operation on non-socket"),
        (Errno.EMSGSIZE, "Message too long"),
        (Errno.ENOPROTOOPT, "Protocol not available"),
        (Errno.EPROTONOSUPPORT, "Protocol not supported"),
        (Errno.ESOCKTNOSUPPORT, "Socket type not supported"),
        (Errno.EOPNOTSUPP, "Operation not supported"),
        (Errno.EPFNOSUPPORT, "Protocol family not supported"),
        (Errno.ENETDOWN, "Network is down"),
        (Errno.ENETUNREACH, "Network is unreachable"),
        (Errno.ENETRESET, "Network dropped connection on reset"),
        (Errno.ECONNABORTED, "Software caused connection abort"),
        (Errno.ECONNRESET, "Connection reset by peer"),
        (Errno.ENOBUFS, "No buffer space available"),
        (Errno.EISCONN, "Transport endpoint is already connected"),
        (Errno.ENOTCONN, "Transport endpoint is not connected"),
        (Errno.ESHUTDOWN, "Cannot send after transport endpoint shutdown"),
        (Errno.ETIMEDOUT, "Connection timed out"),
        (Errno.ECONNREFUSED, "Connection refused"),
        (Errno.EHOSTDOWN, "Host is down"),
        (Errno.EHOSTUNREACH, "No route to host"),
        (Errno.EALREADY, "Operation already in progress"),
        (Errno.EINPROGRESS, "Operation now in progress"),
        (Errno.EISNAM, "Is a named type file"),
        (Errno.EREMOTEIO, "Remote I/O error"),
        (Errno.EDQUOT, "Disk quota exceeded"),
    ];

    /// <summary>
    /// The enames this table sent before 2026-09-10, when its wording was aligned with the strings
    /// the Linux kernel's 9P client accepts. They are still recognised on receipt, so a peer built
    /// from an earlier release of this workspace is understood; they are never sent.
    /// </summary>
    private static readonly (string Ename, int Errno)[] FormerEnames =
    [
        ("no such device or address", Errno.ENXIO),
        ("unknown fid", Errno.EBADF),
        ("try again", Errno.EAGAIN),
        ("out of memory", Errno.ENOMEM),
        ("is a directory", Errno.EISDIR),
        ("bad argument", Errno.EINVAL),
        ("too many fids", Errno.ENFILE),
        ("no space left", Errno.ENOSPC),
        ("read-only file system", Errno.EROFS),
        ("result too large", Errno.ERANGE),
        ("file name too long", Errno.ENAMETOOLONG),
        ("lock not available", Errno.ENOLCK),
        ("not implemented", Errno.ENOSYS),
        ("directory not empty", Errno.ENOTEMPTY),
        ("too many symbolic links", Errno.ELOOP),
        ("no such attribute", Errno.ENODATA),
        ("bad message", Errno.EPROTO),
        ("value too large", Errno.EOVERFLOW),
        ("not supported", Errno.EOPNOTSUPP),

        // Until 2026-09-10 the server refused the settable file flags outright (reference §8
        // rule 19 as it then read); they now reach the handler, so these are received only from
        // a server built before that.
        ("create cannot set DMAPPEND/DMEXCL/DMTMP", Errno.EPERM),
        ("wstat cannot change DMAPPEND/DMEXCL/DMTMP", Errno.EPERM),
    ];


    /// <summary>
    /// Enames a server produces that are not derived from an errno, with the errno they map back
    /// to. Every one of them keeps its Plan 9 wording.
    /// </summary>
    private static readonly (string Ename, int Errno)[] ServerEnames =
    [
        ("duplicate tag", Errno.EINVAL),
        ("duplicate fid", Errno.EINVAL),
        ("unknown message", Errno.EOPNOTSUPP),
        ("bad offset", Errno.EINVAL),
        ("bad open mode", Errno.EINVAL),
        ("bad name", Errno.EINVAL),
        ("cannot clone open fid", Errno.EINVAL),
        ("authentication failed", Errno.EACCES),
        ("authentication not required", Errno.ECONNREFUSED),
        ("version not negotiated", Errno.EPROTO),
        ("symlinks not supported", Errno.EOPNOTSUPP),
        ("file exists", Errno.EEXIST),

        // Reference §8 rule 19 and §5.8. Refused with an explicit EPERM on the wire, but a
        // 9P2000 peer is given only the text, and without a row here the ename mapped back to
        // EIO -- so a client that recovered an errno from it was told the wrong thing about a
        // refusal that had been perfectly specific.
        ("wstat cannot change DMDIR", Errno.EPERM),
        ("wstat cannot set DMAUTH or DMMOUNT", Errno.EPERM),
        ("create cannot set DMAUTH or DMMOUNT", Errno.EPERM),

        // The same, for the refusals the projector and the client raise locally. These are built
        // with FromEname, so before this row the *errno the caller saw* was EIO rather than the
        // one stat(5) and rule 15 name.
        ("wstat cannot change the owner", Errno.EPERM),
        ("wstat cannot set muid", Errno.EPERM),
        ("wstat cannot set atime", Errno.EPERM),
        ("wstat cannot set type", Errno.EPERM),
        ("wstat cannot set dev", Errno.EPERM),
        ("wstat cannot set qid", Errno.EPERM),
        ("cannot rename across directories", Errno.EOPNOTSUPP),
    ];

    /// <summary>
    /// Every string the Linux kernel's 9P client maps to an errno over plain 9P2000, by errno
    /// (<c>net/9p/error.c</c>, captured in <c>docs/9p/fixtures/linux-9p-errors.json</c>, which
    /// <c>ErrorTableTests.LinuxTableMatchesTheFixture</c> compares this against). All of them are
    /// understood on receipt; the one sent for each errno is the <see cref="Table"/> row.
    /// </summary>
    internal static readonly (int Errno, string[] Enames)[] LinuxTable =
    [
        (Errno.EPERM, ["Operation not permitted", "wstat prohibited", "wstat can't convert between files and directories", "not a member of proposed group", "no access to special file", "only support truncation to zero length", "cannot remove root"]),
        (Errno.ENOENT, ["No such file or directory", "directory entry not found", "file not found", "file does not exist", "illegal path element", "directory entry is not allocated"]),
        (Errno.EINTR, ["Interrupted system call"]),
        (Errno.EIO, ["Input/output error", "i/o error", "i/o count too large", "corrupted directory entry", "corrupted file entry", "corrupted block label", "corrupted meta data", "root of file system is corrupted", "corrupted super block", "venti i/o error"]),
        (Errno.ENXIO, ["No such device or address"]),
        (Errno.E2BIG, ["Argument list too long"]),
        (Errno.EBADF, ["Bad file descriptor", "fid unknown or out of range", "bad use of fid", "fid already in use"]),
        (Errno.EAGAIN, ["Resource temporarily unavailable", "exclusive use file already open", "file is in use"]),
        (Errno.ENOMEM, ["Cannot allocate memory"]),
        (Errno.EACCES, ["Permission denied", "permission denied", "not owner", "only owner can change group in wstat"]),
        (Errno.EFAULT, ["Bad address"]),
        (Errno.ENOTBLK, ["Block device required"]),
        (Errno.EBUSY, ["Device or resource busy"]),
        (Errno.EEXIST, ["File exists", "file exists", "file already exists", "file or directory already exists"]),
        (Errno.EXDEV, ["Invalid cross-device link"]),
        (Errno.ENODEV, ["No such device"]),
        (Errno.ENOTDIR, ["Not a directory", "not a directory"]),
        (Errno.EISDIR, ["Is a directory"]),
        (Errno.EINVAL, ["Invalid argument", "illegal mode", "unknown group", "unknown user", "illegal offset"]),
        (Errno.ENFILE, ["Too many open files in system"]),
        (Errno.EMFILE, ["Too many open files"]),
        (Errno.ETXTBSY, ["Text file busy", "file in use", "file already open for I/O"]),
        (Errno.EFBIG, ["File too large", "file too big"]),
        (Errno.ENOSPC, ["No space left on device", "file system is full"]),
        (Errno.ESPIPE, ["Illegal seek", "bad offset in directory read"]),
        (Errno.EROFS, ["Read-only file system", "read only file system", "file is read only"]),
        (Errno.EMLINK, ["Too many links"]),
        (Errno.EPIPE, ["Broken pipe"]),
        (Errno.EDOM, ["Numerical argument out of domain"]),
        (Errno.ERANGE, ["Numerical result out of range"]),
        (Errno.EDEADLK, ["Resource deadlock avoided"]),
        (Errno.ENAMETOOLONG, ["File name too long", "illegal name"]),
        (Errno.ENOLCK, ["No locks available"]),
        (Errno.ENOSYS, ["Function not implemented"]),
        (Errno.ENOTEMPTY, ["Directory not empty", "directory is not empty"]),
        (Errno.ELOOP, ["Too many levels of symbolic links"]),
        (Errno.ENOMSG, ["No message of desired type"]),
        (Errno.EIDRM, ["Identifier removed", "file has been removed"]),
        (Errno.ENODATA, ["No data available"]),
        (Errno.ENONET, ["Machine is not on the network"]),
        (Errno.ENOPKG, ["Package not installed"]),
        (Errno.EREMOTE, ["Object is remote"]),
        (Errno.ENOLINK, ["Link has been severed"]),
        (Errno.ECOMM, ["Communication error on send"]),
        (Errno.EPROTO, ["Protocol error", "bogus wstat buffer", "protocol botch"]),
        (Errno.EBADMSG, ["Bad message"]),
        (Errno.EBADFD, ["File descriptor in bad state"]),
        (Errno.ESTRPIPE, ["Streams pipe error"]),
        (Errno.EUSERS, ["Too many users"]),
        (Errno.ENOTSOCK, ["Socket operation on non-socket"]),
        (Errno.EMSGSIZE, ["Message too long"]),
        (Errno.ENOPROTOOPT, ["Protocol not available"]),
        (Errno.EPROTONOSUPPORT, ["Protocol not supported"]),
        (Errno.ESOCKTNOSUPPORT, ["Socket type not supported"]),
        (Errno.EOPNOTSUPP, ["Operation not supported"]),
        (Errno.EPFNOSUPPORT, ["Protocol family not supported"]),
        (Errno.ENETDOWN, ["Network is down"]),
        (Errno.ENETUNREACH, ["Network is unreachable"]),
        (Errno.ENETRESET, ["Network dropped connection on reset"]),
        (Errno.ECONNABORTED, ["Software caused connection abort"]),
        (Errno.ECONNRESET, ["Connection reset by peer"]),
        (Errno.ENOBUFS, ["No buffer space available"]),
        (Errno.EISCONN, ["Transport endpoint is already connected"]),
        (Errno.ENOTCONN, ["Transport endpoint is not connected"]),
        (Errno.ESHUTDOWN, ["Cannot send after transport endpoint shutdown"]),
        (Errno.ETIMEDOUT, ["Connection timed out"]),
        (Errno.ECONNREFUSED, ["Connection refused", "authentication failed"]),
        (Errno.EHOSTDOWN, ["Host is down"]),
        (Errno.EHOSTUNREACH, ["No route to host"]),
        (Errno.EALREADY, ["Operation already in progress"]),
        (Errno.EINPROGRESS, ["Operation now in progress"]),
        (Errno.EISNAM, ["Is a named type file"]),
        (Errno.EREMOTEIO, ["Remote I/O error"]),
        (Errno.EDQUOT, ["Disk quota exceeded"]),
    ];

    private static readonly Dictionary<int, string> ByErrno = BuildByErrno();
    private static readonly Dictionary<string, int> ByEname = BuildByEname();
    private static readonly NinePError[] AllErrors = [.. Table.Select(row => new NinePError(row.Ename, row.Errno))];

    /// <summary>Every (errno, ename) pair of the table, for documentation and tests.</summary>
    public static IReadOnlyList<NinePError> All => AllErrors;

    /// <summary>
    /// The Plan 9 ename for an errno, for example <c>ENOENT</c> to "file not found". An errno the
    /// table does not carry projects to "i/o error", the same place an unknown failure goes.
    /// </summary>
    /// <param name="errno">The Linux errno to project.</param>
    /// <returns>The ename a 9P2000 or 9P2000.u peer is told.</returns>
    public static string EnameFor(int errno) =>
        ByErrno.TryGetValue(errno, out string? ename) ? ename : "i/o error";

    /// <summary>
    /// The errno for a known ename, covering both the table and the enames a server writes itself.
    /// </summary>
    /// <param name="ename">The ename to map back.</param>
    /// <returns>The Linux errno, or <c>EIO</c> when the ename is not one of ours.</returns>
    public static int ErrnoFor(string ename) =>
        ename is not null && ByEname.TryGetValue(ename, out int errno) ? errno : Errno.EIO;

    private static Dictionary<int, string> BuildByErrno()
    {
        Dictionary<int, string> map = [];
        foreach ((int errno, string ename) in Table)
        {
            map[errno] = ename;
        }

        return map;
    }

    private static Dictionary<string, int> BuildByEname()
    {
        Dictionary<string, int> map = new(StringComparer.Ordinal);

        // Later rows win, which is what settles the one ename two errnos share: "permission
        // denied" maps back to EACCES, the answer a 9P2000.L peer expects for a refused access.
        foreach ((int errno, string[] enames) in LinuxTable)
        {
            foreach (string ename in enames)
            {
                map[ename] = errno;
            }
        }

        foreach ((string ename, int errno) in FormerEnames)
        {
            map[ename] = errno;
        }

        foreach ((int errno, string ename) in Table)
        {
            map[ename] = errno;
        }

        foreach ((string ename, int errno) in ServerEnames)
        {
            map[ename] = errno;
        }

        return map;
    }
}
