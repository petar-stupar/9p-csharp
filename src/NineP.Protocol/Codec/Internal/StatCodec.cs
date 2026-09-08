using NineP.Protocol.Messages;

namespace NineP.Protocol.Codec.Internal;

/// <summary>
/// Reads and writes the stat record of reference §4.2, in both of its framings.
/// <para>
/// Inside <c>Rstat</c> and <c>Twstat</c> the record is a counted <c>stat[n]</c> field, so its
/// length is on the wire <b>twice</b> — once as that count and once as the record's own leading
/// <c>size[2]</c>, which stat(5) lists under BUGS — and the two must agree or the message is
/// malformed (reference §8 rule 2). That is <see cref="Read"/> and <see cref="Write"/>.
/// </para>
/// <para>
/// A 9P2000 directory read is a different thing: read(5) returns an integral number of <b>bare</b>
/// stat records, each carrying its <c>size[2]</c> exactly once. That is
/// <see cref="ReadRecord"/> and <see cref="WriteRecord"/>. Using the counted form there produces a
/// stream every other 9P2000 peer reads as garbage — plan9port's <c>9p ls</c> answers
/// <c>"malformed directory contents"</c> — while two implementations that both do it read each
/// other perfectly well, which is why only an external peer finds it.
/// </para>
/// </summary>
internal static class StatCodec
{
    /// <summary>Reads <c>stat[n]</c>: the outer count, the inner size, and the record's fields.</summary>
    /// <param name="reader">The reader, positioned at the outer <c>n[2]</c>.</param>
    /// <param name="dialect">The session dialect, which decides whether the .u fields are present.</param>
    /// <returns>The record, or the default when the reader failed.</returns>
    public static StatRecord Read(ref WireReader reader, Dialect dialect)
    {
        ushort outer = reader.ReadUInt16();
        if (reader.Failed)
        {
            return default;
        }

        int start = reader.Position;
        StatRecord record = ReadRecord(ref reader, dialect);
        if (reader.Failed)
        {
            return default;
        }

        if (reader.Position - start != outer)
        {
            reader.Fail(ProtocolErrorKind.Stat);
            return default;
        }

        return record;
    }

    /// <summary>
    /// Reads one bare stat record: its own <c>size[2]</c> and its fields, with no outer count.
    /// This is the form a 9P2000 or 9P2000.u directory read returns (read(5)).
    /// </summary>
    /// <param name="reader">The reader, positioned at the record's <c>size[2]</c>.</param>
    /// <param name="dialect">The session dialect, which decides whether the .u fields are present.</param>
    /// <returns>The record, or the default when the reader failed.</returns>
    public static StatRecord ReadRecord(ref WireReader reader, Dialect dialect)
    {
        ushort size = reader.ReadUInt16();
        if (reader.Failed)
        {
            return default;
        }

        int start = reader.Position;
        StatRecord record = ReadFields(ref reader, dialect);
        if (reader.Failed)
        {
            return default;
        }

        if (reader.Position - start != size)
        {
            reader.Fail(ProtocolErrorKind.Stat);
            return default;
        }

        return record;
    }

    /// <summary>Writes <c>stat[n]</c>: the outer count, the inner size, and the record's fields.</summary>
    /// <param name="writer">The writer, positioned where the outer <c>n[2]</c> goes.</param>
    /// <param name="record">The record to write.</param>
    /// <param name="dialect">The session dialect, which decides whether the .u fields are written.</param>
    public static void Write(ref WireWriter writer, in StatRecord record, Dialect dialect)
    {
        writer.WriteUInt16(Sized(in record, dialect, extra: 2));
        WriteRecord(ref writer, in record, dialect);
    }

    /// <summary>
    /// Writes one bare stat record: its own <c>size[2]</c> and its fields, with no outer count.
    /// This is the form a 9P2000 or 9P2000.u directory read returns (read(5)).
    /// </summary>
    /// <param name="writer">The writer, positioned where the record's <c>size[2]</c> goes.</param>
    /// <param name="record">The record to write.</param>
    /// <param name="dialect">The session dialect, which decides whether the .u fields are written.</param>
    public static void WriteRecord(ref WireWriter writer, in StatRecord record, Dialect dialect)
    {
        writer.WriteUInt16(Sized(in record, dialect, extra: 0));

        writer.WriteUInt16(record.Type);
        writer.WriteUInt32(record.Dev);
        writer.WriteQid(record.Qid);
        writer.WriteUInt32(record.Mode);
        writer.WriteUInt32(record.ATime);
        writer.WriteUInt32(record.MTime);
        writer.WriteUInt64(record.Length);
        writer.WriteString(record.Name);
        writer.WriteString(record.Uid);
        writer.WriteString(record.Gid);
        writer.WriteString(record.Muid);

        if (dialect != Dialect.P9_2000_u)
        {
            return;
        }

        writer.WriteString(record.Extension ?? string.Empty);
        writer.WriteUInt32(record.NUid);
        writer.WriteUInt32(record.NGid);
        writer.WriteUInt32(record.NMuid);
    }

    private static StatRecord ReadFields(ref WireReader reader, Dialect dialect)
    {
        StatRecord record = new()
        {
            Type = reader.ReadUInt16(),
            Dev = reader.ReadUInt32(),
            Qid = reader.ReadQid(),
            Mode = reader.ReadUInt32(),
            ATime = reader.ReadUInt32(),
            MTime = reader.ReadUInt32(),
            Length = reader.ReadUInt64(),

            // stat.name is "/" for the root of a served tree, so the name rules of reference §8
            // rule 3 do not apply here: this field is a label, not a path element.
            Name = reader.ReadString(),
            Uid = reader.ReadString(),
            Gid = reader.ReadString(),
            Muid = reader.ReadString(),
        };

        return dialect == Dialect.P9_2000_u
            ? record with
            {
                Extension = reader.ReadString(),
                NUid = reader.ReadUInt32(),
                NGid = reader.ReadUInt32(),
                NMuid = reader.ReadUInt32(),
            }
            : record;
    }

    /// <summary>
    /// The record's length as a <c>u16</c>, refusing one that does not fit. The build has no
    /// <c>CheckForOverflowUnderflow</c>, so an unchecked cast here wrapped <c>n[2]</c> and the
    /// record's own <c>size[2]</c> while the frame's outer <c>size[4]</c> stayed correct — a frame
    /// every peer rejects, produced silently. <c>WireWriter.WriteString</c> catches a single
    /// over-long string; nothing caught the record's total.
    /// </summary>
    /// <param name="record">The record being written.</param>
    /// <param name="dialect">The session dialect, which decides whether the .u fields are written.</param>
    /// <param name="extra">Two for the outer <c>n[2]</c>, zero for the bare record.</param>
    /// <returns>The length to write.</returns>
    /// <exception cref="NinePException">The record does not fit in a <c>u16</c>.</exception>
    private static ushort Sized(in StatRecord record, Dialect dialect, int extra)
    {
        int size = record.GetEncodedSize(dialect) + extra;

        return size <= ushort.MaxValue
            ? (ushort)size
            : throw new NinePException(NinePError.FromErrno(Errno.EOVERFLOW));
    }
}
