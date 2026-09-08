namespace NineP.Protocol;

/// <summary>
/// One directory entry, unified over 9P2000 stat records and .L dirents (reference §4.3). The
/// server's directory packer and the client's <c>ReadDir</c> both speak it, so a listing looks the
/// same to a caller whichever record format the session negotiated.
/// </summary>
/// <param name="Name">The entry's name, which never contains '/'.</param>
/// <param name="Qid">The qid of the file the entry names.</param>
/// <param name="Kind">The file type, which becomes the POSIX <c>d_type</c> byte in a dirent.</param>
/// <param name="Cursor">The cookie a <c>Treaddir</c> passes to continue after this entry.</param>
public readonly record struct DirEntry(string Name, Qid Qid, FileKind Kind, ulong Cursor)
{
    /// <summary>The POSIX <c>d_type</c> byte this entry carries in an <c>Rreaddir</c> record.</summary>
    public byte DirentType => Kind switch
    {
        FileKind.Fifo => DtFifo,
        FileKind.CharDevice => DtChr,
        FileKind.Directory => DtDir,
        FileKind.BlockDevice => DtBlk,
        FileKind.File => DtReg,
        FileKind.Symlink => DtLnk,
        FileKind.Socket => DtSock,
        _ => DtUnknown,
    };

    /// <summary>The Linux <c>DT_*</c> values of <c>dirent.h</c>, which reference §4.3 names.</summary>
    internal const byte DtUnknown = 0;
    internal const byte DtFifo = 1;
    internal const byte DtChr = 2;
    internal const byte DtDir = 4;
    internal const byte DtBlk = 6;
    internal const byte DtReg = 8;
    internal const byte DtLnk = 10;
    internal const byte DtSock = 12;

    /// <summary>The file kind a POSIX <c>d_type</c> byte names; anything unknown reads as a file.</summary>
    /// <param name="direntType">The <c>d_type</c> byte from an <c>Rreaddir</c> record.</param>
    /// <returns>The dialect-neutral file kind.</returns>
    public static FileKind KindOf(byte direntType) => direntType switch
    {
        DtFifo => FileKind.Fifo,
        DtChr => FileKind.CharDevice,
        DtDir => FileKind.Directory,
        DtBlk => FileKind.BlockDevice,
        DtLnk => FileKind.Symlink,
        DtSock => FileKind.Socket,
        _ => FileKind.File,
    };
}
