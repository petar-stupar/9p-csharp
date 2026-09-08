using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;

namespace NineP.Server.Internal;

/// <summary>
/// One packer for both directory formats (§6.7): stat records for a 9P2000 or .u <c>Rread</c>,
/// dirents for a .L <c>Rreaddir</c>. Whole records only, in both — a client that received half a
/// record could not tell where the next one begins, and there is no length prefix outside the
/// record to resynchronise on.
/// </summary>
internal static class DirectoryPacker
{
    /// <summary>How many entries to ask a handler for at a time; the leftovers are re-listed.</summary>
    private const int Page = 64;

    /// <summary>
    /// Packs stat records for a 9P2000 or .u <c>Rread</c>. read(5) allows only two offsets — 0, or
    /// the previous offset plus the previous count — because the byte offset into a directory has
    /// no meaning the server can seek to; anything else is <c>"bad offset"</c>.
    /// </summary>
    /// <param name="directory">The directory being read.</param>
    /// <param name="entry">The open fid, which remembers where the last read stopped.</param>
    /// <param name="dialect">The session dialect, which decides the record shape.</param>
    /// <param name="offset">The offset the client asked for.</param>
    /// <param name="budget">The most bytes the reply may carry, already clamped.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A whole number of stat records; empty at the end of the directory.</returns>
    /// <exception cref="NinePException">The offset is not resumable, or one record cannot fit.</exception>
    public static async ValueTask<byte[]> PackStatRecordsAsync(
        IDirectoryHandler directory,
        FidEntry entry,
        Dialect dialect,
        ulong offset,
        int budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (budget == 0)
        {
            return [];
        }
        ArgumentNullException.ThrowIfNull(entry);

        if (offset == 0)
        {
            entry.DirOffset = 0;
            entry.DirCursor = 0;
        }
        else if (offset != entry.DirOffset)
        {
            throw new NinePException(NinePError.FromEname("bad offset"));
        }

        // The records are gathered first and written afterwards: a WireWriter is a ref struct
        // and cannot live across the awaits a handler lookup costs.
        List<StatRecord> records = [];
        int total = 0;
        ulong cursor = entry.DirCursor;

        while (true)
        {
            DirectoryListing listing = await directory
                .ReadDirAsync(cursor, Page, cancellationToken).ConfigureAwait(false);

            bool full = false;
            foreach (DirEntry child in Visible(listing.Entries))
            {
                StatRecord record = await RecordAsync(directory, child, dialect, cancellationToken)
                    .ConfigureAwait(false);
                // A directory read carries bare stat records: size[2] once, not the stat[n]
                // double count of Rstat (read(5), reference §4.2).
                int size = record.GetEncodedSize(dialect) + 2;

                if (total + size > budget)
                {
                    // §6.7: when even the first record does not fit, the reply would be an empty
                    // one the client would read as the end of the directory. That is a lie, so it
                    // is an error instead.
                    if (records.Count == 0)
                    {
                        throw new NinePException(NinePError.FromErrno(Errno.ERANGE));
                    }

                    full = true;
                    break;
                }

                records.Add(record);
                total += size;
                cursor = child.Cursor;
            }

            if (full || listing.EndOfDirectory || listing.Entries.Count == 0)
            {
                break;
            }

            cursor = listing.NextCursor;
        }

        byte[] buffer = new byte[total];
        WireWriter writer = new(buffer);
        foreach (StatRecord record in records)
        {
            StatCodec.WriteRecord(ref writer, in record, dialect);
        }

        entry.DirCursor = cursor;
        entry.DirCount = (ulong)total;
        entry.DirOffset = offset + (ulong)total;

        return buffer;
    }

    /// <summary>
    /// Packs dirents for a .L <c>Rreaddir</c>. Here the offset is the cookie of the last entry the
    /// client received, so the listing resumes <b>after</b> it and no byte-offset rule applies.
    /// </summary>
    /// <param name="directory">The directory being read.</param>
    /// <param name="cookie">The cursor to resume after; 0 starts the listing.</param>
    /// <param name="budget">The most bytes the reply may carry, already clamped.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A whole number of dirents; empty ends the listing.</returns>
    /// <exception cref="NinePException">One entry cannot fit in the whole budget.</exception>
    public static async ValueTask<byte[]> PackDirentsAsync(
        IDirectoryHandler directory, ulong cookie, int budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (budget == 0)
        {
            return [];
        }

        List<DirEntry> packed = [];
        int total = 0;
        ulong cursor = cookie;

        while (true)
        {
            DirectoryListing listing = await directory
                .ReadDirAsync(cursor, Page, cancellationToken).ConfigureAwait(false);

            bool full = false;
            foreach (DirEntry child in Visible(listing.Entries))
            {
                int size = DirEntryCodec.GetEncodedSize(in child);
                if (total + size > budget)
                {
                    if (packed.Count == 0)
                    {
                        throw new NinePException(NinePError.FromErrno(Errno.ERANGE));
                    }

                    full = true;
                    break;
                }

                packed.Add(child);
                total += size;
                cursor = child.Cursor;
            }

            if (full || listing.EndOfDirectory || listing.Entries.Count == 0)
            {
                break;
            }

            cursor = listing.NextCursor;
        }

        byte[] buffer = new byte[total];
        DirEntryCodec.Pack(buffer, packed, out int count);

        return count == packed.Count
            ? buffer
            : throw new NinePException(NinePError.FromErrno(Errno.EIO));
    }

    /// <summary>
    /// Drops "." and ".." (S-27). A handler may produce them — real .L servers do — but this
    /// workspace's listings never carry them, in either record format.
    /// </summary>
    /// <param name="entries">The page the handler produced.</param>
    /// <returns>The entries a client may see.</returns>
    private static IEnumerable<DirEntry> Visible(IReadOnlyList<DirEntry> entries) =>
        entries.Where(entry => entry.Name is not ("." or ".."));

    private static async ValueTask<StatRecord> RecordAsync(
        IDirectoryHandler directory, DirEntry child, Dialect dialect, CancellationToken cancellationToken)
    {
        IHandler? handler = await directory.LookupAsync(child.Name, cancellationToken).ConfigureAwait(false);
        if (handler is null)
        {
            // The entry disappeared between the listing and the lookup; the qid and kind from the
            // listing are still what the client was told about, so they carry the record.
            return AttrProjector.ToStat(
                new Attr { Qid = child.Qid, Kind = child.Kind, Perm = 0 }, child.Name, dialect);
        }

        Attr attr = await handler.GetAttrAsync(cancellationToken).ConfigureAwait(false);
        return AttrProjector.ToStat(attr, child.Name, dialect);
    }
}
