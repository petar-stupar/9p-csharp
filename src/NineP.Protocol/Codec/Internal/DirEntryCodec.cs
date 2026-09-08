using NineP.Protocol.Internal;

namespace NineP.Protocol.Codec.Internal;

/// <summary>
/// Reads and writes the 9P2000.L directory record of reference §4.3,
/// <c>qid[13] offset[8] type[1] name[s]</c>. An entry is never split across a reply: the packer
/// stops before one that will not fit, and the reader treats a partial trailing record as
/// malformed (reference §8 rule 13).
/// </summary>
internal static class DirEntryCodec
{
    /// <summary>The bytes one entry occupies inside an <c>Rreaddir</c> payload.</summary>
    /// <param name="entry">The entry to measure.</param>
    /// <returns>The encoded length.</returns>
    public static int GetEncodedSize(in DirEntry entry) =>
        Qid.WireSize + 8 + 1 + 2 + NinePText.GetByteCount(entry.Name);

    /// <summary>
    /// Packs as many whole entries as fit, in order, and stops at the first that does not.
    /// </summary>
    /// <param name="destination">The payload buffer, sized by the caller's count budget.</param>
    /// <param name="entries">The entries to pack, in listing order.</param>
    /// <param name="packed">How many entries were written.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Pack(Span<byte> destination, IReadOnlyList<DirEntry> entries, out int packed)
    {
        ArgumentNullException.ThrowIfNull(entries);

        WireWriter writer = new(destination);
        packed = 0;

        foreach (DirEntry entry in entries)
        {
            int size = GetEncodedSize(in entry);
            if (writer.Position + size > destination.Length)
            {
                break;
            }

            Write(ref writer, in entry);
            packed++;
        }

        return writer.Position;
    }

    /// <summary>Reads every entry of an <c>Rreaddir</c> payload.</summary>
    /// <param name="data">The payload, which must end exactly on a record boundary.</param>
    /// <param name="entries">The entries, in the order they appeared.</param>
    /// <param name="failure">What was wrong when the result is false.</param>
    /// <returns>True when the payload is a whole number of well-formed records.</returns>
    public static bool TryReadAll(
        ReadOnlyMemory<byte> data, out IReadOnlyList<DirEntry> entries, out ProtocolErrorKind failure)
    {
        List<DirEntry> read = [];
        WireReader reader = new(data);

        while (reader.Remaining > 0)
        {
            DirEntry entry = Read(ref reader);
            if (reader.Failed)
            {
                entries = [];
                failure = reader.Failure;
                return false;
            }

            read.Add(entry);
        }

        entries = read;
        failure = default;
        return true;
    }

    private static void Write(ref WireWriter writer, in DirEntry entry)
    {
        writer.WriteQid(entry.Qid);
        writer.WriteUInt64(entry.Cursor);
        writer.WriteUInt8(entry.DirentType);
        writer.WriteString(entry.Name);
    }

    /// <summary>
    /// The kind of a dirent. Reference §4.3 makes <c>type[1]</c> the POSIX <c>d_type</c>, and a
    /// byte that names a kind is believed. <c>DT_UNKNOWN</c>, and a byte that is not a
    /// <c>d_type</c> at all, fall back to the qid, which reference §4.1 makes authoritative for
    /// the two kinds a qid can express.
    /// <para>
    /// The fallback is not hypothetical: hugelgupf/p9's <c>p9ufs</c> writes the <b>9P qid type</b>
    /// into this field rather than the POSIX one — a directory entry arrives as
    /// <c>… 01000000 00000000 80 0800 "emptyobj"</c>, where <c>0x80</c> is <c>QTDIR</c> and not
    /// <c>DT_DIR (4)</c> — so without this every directory a p9ufs server lists would be reported
    /// as a regular file.
    /// </para>
    /// </summary>
    /// <param name="direntType">The <c>type[1]</c> byte of the dirent.</param>
    /// <param name="qid">The entry's qid.</param>
    /// <returns>The file kind.</returns>
    private static FileKind KindOf(byte direntType, Qid qid)
    {
        FileKind named = DirEntry.KindOf(direntType);
        if (named != FileKind.File || direntType == DirEntry.DtReg)
        {
            return named;
        }

        if (qid.Type.HasFlag(QidType.QTDIR))
        {
            return FileKind.Directory;
        }

        return qid.Type.HasFlag(QidType.QTSYMLINK) ? FileKind.Symlink : FileKind.File;
    }

    private static DirEntry Read(ref WireReader reader)
    {
        Qid qid = reader.ReadQid();
        ulong cursor = reader.ReadUInt64();
        byte direntType = reader.ReadUInt8();

        // A listing name is read as a plain string rather than through the name rules: real .L
        // servers do emit "." and ".." here (reference §4.3, diod's own trace), and a client that
        // rejected them could not read a diod directory. Our servers never write them (S-27).
        string name = reader.ReadString();
        if (!reader.Failed && NinePText.GetByteCount(name) > Constants.MaxNameLength)
        {
            reader.Fail(ProtocolErrorKind.Name);
        }

        return reader.Failed ? default : new DirEntry(name, qid, KindOf(direntType, qid), cursor);
    }
}
