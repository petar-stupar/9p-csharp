namespace NineP.Protocol.Codec.Internal;

/// <summary>
/// The two mode vocabularies the projector translates between: the 9P2000 / .u <c>DM*</c> bits of
/// reference §4.4 and the POSIX <c>S_IF*</c> values of reference §4.7. Neither header is vendored,
/// so the constants are written out here once and pinned by <c>ModeBitsTests</c>.
/// </summary>
internal static class ModeBits
{
    /// <summary>A directory.</summary>
    public const uint DMDIR = 0x80000000;

    /// <summary>Append only: writes ignore the offset and OTRUNC is ignored.</summary>
    public const uint DMAPPEND = 0x40000000;

    /// <summary>Exclusive use: one open fid at a time.</summary>
    public const uint DMEXCL = 0x20000000;

    /// <summary>A mounted channel; the one high bit the qid mirror skips.</summary>
    public const uint DMMOUNT = 0x10000000;

    /// <summary>An authentication file, the file an afid names.</summary>
    public const uint DMAUTH = 0x08000000;

    /// <summary>Not backed up.</summary>
    public const uint DMTMP = 0x04000000;

    /// <summary>A symbolic link (.u); the extension field holds the target.</summary>
    public const uint DMSYMLINK = 0x02000000;

    /// <summary>A hard link (.u, Linux).</summary>
    public const uint DMLINK = 0x01000000;

    /// <summary>A device (.u); the extension field holds "b maj min" or "c maj min".</summary>
    public const uint DMDEVICE = 0x00800000;

    /// <summary>A named pipe (.u).</summary>
    public const uint DMNAMEDPIPE = 0x00200000;

    /// <summary>A socket (.u).</summary>
    public const uint DMSOCKET = 0x00100000;

    /// <summary>Setuid (.u).</summary>
    public const uint DMSETUID = 0x00080000;

    /// <summary>Setgid (.u).</summary>
    public const uint DMSETGID = 0x00040000;

    /// <summary>Sticky (.u, Linux).</summary>
    public const uint DMSETVTX = 0x00010000;

    /// <summary>
    /// The mode bits a client may set, at create and through <c>Twstat</c> (open(2), stat(5);
    /// reference §8 rule 19): append-only, exclusive use and temporary.
    /// </summary>
    public const uint SettableFlagBits = DMAPPEND | DMEXCL | DMTMP;

    /// <summary>
    /// The mode bits only a server puts on a file: the authentication file behind an afid and a
    /// mounted channel. A create or a wstat asking for either is refused (rule 19).
    /// </summary>
    public const uint ServerOwnedFlagBits = DMAUTH | DMMOUNT;

    /// <summary>The rwx permission bits a 9P2000 stat record carries.</summary>
    public const uint Permissions = 0x000001FF;

    /// <summary>The permission bits plus setuid, setgid and sticky: the mask reference §4.7 fixes.</summary>
    public const uint FullPermissions = 0xFFF;

    /// <summary>The bits of a qid type byte that the high mode byte does not supply: QTMOUNT.</summary>
    public const byte QidMirrorMask = 0xEF;

    // IDE1006: S_IFMT and its neighbours are the POSIX <sys/stat.h> spellings that reference §4.7
    // names, and §5.0 rule 1 keeps the protocol's own vocabulary; the repository's naming rule
    // wants no underscore. The names are pinned by ModeBitsTests, so a typo is a failing test.
#pragma warning disable IDE1006
    /// <summary>The POSIX file-type mask.</summary>
    public const uint S_IFMT = 0xF000;

    /// <summary>A socket.</summary>
    public const uint S_IFSOCK = 0xC000;

    /// <summary>A symbolic link.</summary>
    public const uint S_IFLNK = 0xA000;

    /// <summary>A regular file.</summary>
    public const uint S_IFREG = 0x8000;

    /// <summary>A block device.</summary>
    public const uint S_IFBLK = 0x6000;

    /// <summary>A directory.</summary>
    public const uint S_IFDIR = 0x4000;

    /// <summary>A character device.</summary>
    public const uint S_IFCHR = 0x2000;

    /// <summary>A named pipe.</summary>
    public const uint S_IFIFO = 0x1000;

    /// <summary>Setuid.</summary>
    public const uint S_ISUID = 0x800;

    /// <summary>Setgid.</summary>
    public const uint S_ISGID = 0x400;

    /// <summary>Sticky.</summary>
    public const uint S_ISVTX = 0x200;
#pragma warning restore IDE1006

    /// <summary>The Linux <c>open(2)</c> access-mode mask: the low two bits of the flag word.</summary>
    public const uint AccessMask = 0x3;

    // IDE1006: the O_* spellings are Linux's own, which reference §4.5 lists in octal; §5.0 rule 1
    // keeps the protocol's vocabulary. The octal figure of the reference is in each comment.
#pragma warning disable IDE1006
    /// <summary>Create the file if it does not exist (0100).</summary>
    public const uint O_CREAT = 0x40;

    /// <summary>Fail if the file already exists (0200).</summary>
    public const uint O_EXCL = 0x80;

    /// <summary>Truncate the file to zero length (01000).</summary>
    public const uint O_TRUNC = 0x200;

    /// <summary>Writes go to the end of the file (02000).</summary>
    public const uint O_APPEND = 0x400;

    /// <summary>Fail unless the file is a directory (0200000).</summary>
    public const uint O_DIRECTORY = 0x10000;

    /// <summary>Fail if the final component is a symbolic link (0400000).</summary>
    public const uint O_NOFOLLOW = 0x20000;
#pragma warning restore IDE1006

    /// <summary>The 9P2000 / .u <c>Topen.mode</c> bits of reference §4.5.</summary>
    public const byte OTRUNC = 0x10;

    /// <summary>Close on exec; client-local, and servers ignore it.</summary>
    public const byte OCEXEC = 0x20;

    /// <summary>Remove the file when the fid is clunked.</summary>
    public const byte ORCLOSE = 0x40;

    /// <summary>Writes go to the end of the file; a flag, never an access mode (S-21).</summary>
    public const byte OAPPEND = 0x80;

    /// <summary>The access mode of a 9P2000 open: the low two bits and nothing else (S-21).</summary>
    public const byte OpenAccessMask = 0x3;

    /// <summary>Every <c>Topen.mode</c> bit this workspace honours or ignores; any other is an error.</summary>
    public const byte KnownOpenBits = OpenAccessMask | OTRUNC | OCEXEC | ORCLOSE | OAPPEND;

    /// <summary>The flags <c>Topen.mode</c> has no room for: reference §8 rule 15 refuses them.</summary>
    public const OpenFlags LinuxOnlyOpenFlags = OpenFlags.Exclusive | OpenFlags.Directory | OpenFlags.NoFollow;

    /// <summary>
    /// The <c>Topen.mode</c> byte an access mode and a flag set project to (reference §4.5).
    /// <c>O_EXCL</c>, <c>O_DIRECTORY</c> and <c>O_NOFOLLOW</c> have no <c>mode[1]</c> spelling at
    /// all — <c>OEXCL</c> is <c>0x1000</c> and does not fit in a byte — so a caller that asks for
    /// one is refused here rather than sent a <c>Topen</c> with the flag quietly missing
    /// (reference §8 rule 15).
    /// </summary>
    /// <param name="mode">The access mode.</param>
    /// <param name="flags">The flags accompanying it; the .L-only ones have no 9P2000 spelling.</param>
    /// <returns>The mode byte.</returns>
    /// <exception cref="NinePException">A flag 9P2000 and .u cannot carry was asked for.</exception>
    public static byte ToOpenByte(OpenMode mode, OpenFlags flags)
    {
        if ((flags & LinuxOnlyOpenFlags) != OpenFlags.None)
        {
            throw new NinePException(new NinePError(
                "9P2000 and 9P2000.u have no O_EXCL, O_DIRECTORY or O_NOFOLLOW", (int)Errno.EOPNOTSUPP));
        }

        byte value = (byte)((byte)mode & OpenAccessMask);
        if (flags.HasFlag(OpenFlags.Truncate))
        {
            value |= OTRUNC;
        }

        if (flags.HasFlag(OpenFlags.RemoveOnClose))
        {
            value |= ORCLOSE;
        }

        if (flags.HasFlag(OpenFlags.Append))
        {
            value |= OAPPEND;
        }

        return value;
    }

    /// <summary>
    /// Splits a <c>Topen.mode</c> byte into its access mode and flags. <c>OAPPEND</c> is a flag, so
    /// <c>OREAD | OAPPEND</c> stays a read open (S-21).
    /// </summary>
    /// <param name="value">The byte the client sent.</param>
    /// <param name="mode">The access mode: the low two bits.</param>
    /// <param name="flags">The flags the byte carries.</param>
    /// <returns>False when the byte sets a bit reference §4.5 does not define.</returns>
    public static bool TryFromOpenByte(byte value, out OpenMode mode, out OpenFlags flags)
    {
        mode = (OpenMode)(value & OpenAccessMask);
        flags = OpenFlags.None;

        if ((value & ~KnownOpenBits) != 0)
        {
            return false;
        }

        if ((value & OTRUNC) != 0)
        {
            flags |= OpenFlags.Truncate;
        }

        if ((value & ORCLOSE) != 0)
        {
            flags |= OpenFlags.RemoveOnClose;
        }

        if ((value & OAPPEND) != 0)
        {
            flags |= OpenFlags.Append;
        }

        return true;
    }

    /// <summary>
    /// The <c>Tlopen.flags</c> word an access mode and a flag set project to (reference §4.5).
    /// Two rules of reference §8 meet here. Rule 15: Linux has no <c>ORCLOSE</c>, so a
    /// remove-on-close open is refused rather than sent as an ordinary open that leaves the file
    /// behind. Rule 16: <c>OEXEC</c> goes out as <c>O_RDONLY</c>, because execute permission is
    /// the Linux client's own concern and access mode 3 is <c>O_NOACCESS</c>, which never leaves
    /// a client.
    /// </summary>
    /// <param name="mode">The access mode.</param>
    /// <param name="flags">The flags accompanying it.</param>
    /// <returns>The Linux flag word, in its generic (x86) values.</returns>
    /// <exception cref="NinePException">Remove-on-close was asked for, which .L cannot express.</exception>
    public static uint ToLinuxFlags(OpenMode mode, OpenFlags flags)
    {
        if (flags.HasFlag(OpenFlags.RemoveOnClose))
        {
            throw new NinePException(new NinePError(
                "9P2000.L has no ORCLOSE; remove the file after clunking it", (int)Errno.EOPNOTSUPP));
        }

        uint value = mode == OpenMode.Exec ? 0 : (uint)mode & AccessMask;
        if (flags.HasFlag(OpenFlags.Truncate))
        {
            value |= O_TRUNC;
        }

        if (flags.HasFlag(OpenFlags.Append))
        {
            value |= O_APPEND;
        }

        if (flags.HasFlag(OpenFlags.Exclusive))
        {
            value |= O_EXCL;
        }

        if (flags.HasFlag(OpenFlags.Directory))
        {
            value |= O_DIRECTORY;
        }

        if (flags.HasFlag(OpenFlags.NoFollow))
        {
            value |= O_NOFOLLOW;
        }

        return value;
    }

    /// <summary>
    /// Splits a <c>Tlopen.flags</c> word. Only the flags reference §4.5 tells servers to honour are
    /// read; the rest — <c>O_NONBLOCK</c>, <c>O_CLOEXEC</c> and the others — are ignored, not refused.
    /// </summary>
    /// <param name="value">The flag word the client sent.</param>
    /// <param name="mode">The access mode: the low two bits.</param>
    /// <param name="flags">The flags this workspace honours.</param>
    public static void FromLinuxFlags(uint value, out OpenMode mode, out OpenFlags flags)
    {
        mode = (OpenMode)(value & AccessMask);
        flags = OpenFlags.None;

        if ((value & O_TRUNC) != 0)
        {
            flags |= OpenFlags.Truncate;
        }

        if ((value & O_APPEND) != 0)
        {
            flags |= OpenFlags.Append;
        }

        if ((value & O_EXCL) != 0)
        {
            flags |= OpenFlags.Exclusive;
        }

        if ((value & O_DIRECTORY) != 0)
        {
            flags |= OpenFlags.Directory;
        }

        if ((value & O_NOFOLLOW) != 0)
        {
            flags |= OpenFlags.NoFollow;
        }
    }
}
