namespace NineP.Protocol;

/// <summary>
/// The errno to ename projection table (workspace architecture §3): Plan 9 wording where one
/// exists, so a 9P2000 client and a 9P2000.L client are told the same thing in their own terms.
/// </summary>
public static class ErrorTable
{
    private static readonly (int Errno, string Ename)[] Table =
    [
        (Errno.EPERM, "permission denied"),
        (Errno.ENOENT, "file not found"),
        (Errno.EIO, "i/o error"),
        (Errno.ENXIO, "no such device or address"),
        (Errno.EBADF, "unknown fid"),
        (Errno.EAGAIN, "try again"),
        (Errno.ENOMEM, "out of memory"),
        (Errno.EACCES, "permission denied"),
        (Errno.EEXIST, "file already exists"),
        (Errno.ENOTDIR, "not a directory"),
        (Errno.EISDIR, "is a directory"),
        (Errno.EINVAL, "bad argument"),
        (Errno.ENFILE, "too many fids"),
        (Errno.EFBIG, "file too big"),
        (Errno.ENOSPC, "no space left"),
        (Errno.EROFS, "read-only file system"),
        (Errno.ERANGE, "result too large"),
        (Errno.ENAMETOOLONG, "file name too long"),
        (Errno.ENOLCK, "lock not available"),
        (Errno.ENOSYS, "not implemented"),
        (Errno.ENOTEMPTY, "directory not empty"),
        (Errno.ELOOP, "too many symbolic links"),
        (Errno.ENODATA, "no such attribute"),
        (Errno.EPROTO, "bad message"),
        (Errno.EOVERFLOW, "value too large"),
        (Errno.EOPNOTSUPP, "not supported"),
        (Errno.ECONNREFUSED, "authentication not required"),
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
        ("version not negotiated", Errno.EPROTO),
        ("symlinks not supported", Errno.EOPNOTSUPP),
        ("file exists", Errno.EEXIST),

        // Reference §8 rule 19 and §5.8. These three are refused with an explicit EPERM on the
        // wire, but a 9P2000 peer is given only the text, and without a row here the ename mapped
        // back to EIO -- so a client that recovered an errno from it was told the wrong thing
        // about a refusal that had been perfectly specific.
        ("create cannot set DMAPPEND/DMEXCL/DMTMP", Errno.EPERM),
        ("wstat cannot change DMAPPEND/DMEXCL/DMTMP", Errno.EPERM),
        ("wstat cannot change DMDIR", Errno.EPERM),

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
