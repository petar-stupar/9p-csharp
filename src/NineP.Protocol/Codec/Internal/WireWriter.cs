using System.Buffers.Binary;
using NineP.Protocol.Internal;

namespace NineP.Protocol.Codec.Internal;

/// <summary>
/// Writes the primitives of reference §1 into a caller-supplied span. The span is sized by
/// <c>GetEncodedSize</c> before the first write, so running out of room here is a codec bug, not a
/// protocol error, and it surfaces as an <see cref="ArgumentOutOfRangeException"/> from the span.
/// </summary>
internal ref struct WireWriter
{
    private readonly Span<byte> _buffer;
    private int _position;

    /// <summary>Creates a writer over the exact bytes one frame will occupy.</summary>
    /// <param name="buffer">The destination, already sized for the whole message.</param>
    public WireWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>How many bytes have been written so far.</summary>
    public readonly int Position => _position;

    /// <summary>Writes <c>u8</c>.</summary>
    /// <param name="value">The byte to write.</param>
    public void WriteUInt8(byte value) => _buffer[_position++] = value;

    /// <summary>Writes little-endian <c>u16</c>.</summary>
    /// <param name="value">The value to write.</param>
    public void WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_position, 2), value);
        _position += 2;
    }

    /// <summary>Writes little-endian <c>u32</c>.</summary>
    /// <param name="value">The value to write.</param>
    public void WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(_position, 4), value);
        _position += 4;
    }

    /// <summary>Writes little-endian <c>u64</c>.</summary>
    /// <param name="value">The value to write.</param>
    public void WriteUInt64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.Slice(_position, 8), value);
        _position += 8;
    }

    /// <summary>Writes <c>qid[13]</c> (reference §4.1).</summary>
    /// <param name="qid">The qid to write.</param>
    public void WriteQid(in Qid qid)
    {
        WriteUInt8((byte)qid.Type);
        WriteUInt32(qid.Version);
        WriteUInt64(qid.Path);
    }

    /// <summary>Writes <c>len[2]</c> followed by the string's UTF-8 bytes, with no terminator.</summary>
    /// <param name="value">The string to write.</param>
    public void WriteString(string value)
    {
        int start = _position;
        _position += 2;
        int written = NinePText.Encode(value, _buffer[_position..]);
        if (written > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), written, "a 9P string is at most 65535 bytes");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(start, 2), (ushort)written);
        _position += written;
    }

    /// <summary>Copies a payload in as it stands.</summary>
    /// <param name="value">The bytes to copy.</param>
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(_buffer.Slice(_position, value.Length));
        _position += value.Length;
    }

    /// <summary>
    /// Back-patches a <c>u32</c> that was reserved earlier; this is how <c>size[4]</c> comes to
    /// hold the length of a message that was not measured until it had been written.
    /// </summary>
    /// <param name="offset">Where the reserved field starts.</param>
    /// <param name="value">The value to store there.</param>
    public readonly void PatchUInt32(int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(offset, 4), value);
}
