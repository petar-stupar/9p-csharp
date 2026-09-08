using System.Buffers.Binary;
using NineP.Protocol.Internal;

namespace NineP.Protocol.Codec.Internal;

/// <summary>
/// Reads the primitives of reference §1 out of one complete, contiguous frame. Every read is
/// bounds-checked against what is left of the frame <em>before</em> anything is allocated, and the
/// first failure is sticky: once the reader has failed, later reads return defaults and keep the
/// original kind, so a decoder can read a whole message and check once at the end.
/// </summary>
internal ref struct WireReader
{
    private readonly ReadOnlyMemory<byte> _frame;
    private int _position;
    private ProtocolErrorKind _failure;
    private bool _failed;

    /// <summary>Creates a reader over one complete frame, positioned at its first byte.</summary>
    /// <param name="frame">The frame, including its <c>size[4] type[1] tag[2]</c> header.</param>
    public WireReader(ReadOnlyMemory<byte> frame)
    {
        _frame = frame;
        _position = 0;
        _failure = default;
        _failed = false;
    }

    /// <summary>True once a read has failed; every later read is a no-op.</summary>
    public readonly bool Failed => _failed;

    /// <summary>The kind of the first failure, meaningful only when <see cref="Failed"/>.</summary>
    public readonly ProtocolErrorKind Failure => _failure;

    /// <summary>The offset of the next byte to read.</summary>
    public readonly int Position => _position;

    /// <summary>How many bytes of the frame are still unread.</summary>
    public readonly int Remaining => _frame.Length - _position;

    private readonly ReadOnlySpan<byte> Span => _frame.Span;

    /// <summary>Records a failure, keeping the first one, and returns false so callers can chain.</summary>
    /// <param name="kind">What was wrong.</param>
    /// <returns>Always false.</returns>
    public bool Fail(ProtocolErrorKind kind)
    {
        if (!_failed)
        {
            _failed = true;
            _failure = kind;
        }

        return false;
    }

    /// <summary>Moves the cursor past <paramref name="length"/> bytes, or fails with Bounds.</summary>
    /// <param name="length">How many bytes to consume.</param>
    /// <returns>True when the bytes were there.</returns>
    public bool Skip(int length) => Take(length);

    /// <summary>Reads <c>u8</c>.</summary>
    /// <returns>The byte, or zero when the reader has failed.</returns>
    public byte ReadUInt8()
    {
        int at = _position;
        return Take(1) ? Span[at] : (byte)0;
    }

    /// <summary>Reads little-endian <c>u16</c>.</summary>
    /// <returns>The value, or zero when the reader has failed.</returns>
    public ushort ReadUInt16()
    {
        int at = _position;
        return Take(2) ? BinaryPrimitives.ReadUInt16LittleEndian(Span.Slice(at, 2)) : (ushort)0;
    }

    /// <summary>Reads little-endian <c>u32</c>.</summary>
    /// <returns>The value, or zero when the reader has failed.</returns>
    public uint ReadUInt32()
    {
        int at = _position;
        return Take(4) ? BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice(at, 4)) : 0u;
    }

    /// <summary>Reads little-endian <c>u64</c>.</summary>
    /// <returns>The value, or zero when the reader has failed.</returns>
    public ulong ReadUInt64()
    {
        int at = _position;
        return Take(8) ? BinaryPrimitives.ReadUInt64LittleEndian(Span.Slice(at, 8)) : 0ul;
    }

    /// <summary>Reads <c>qid[13]</c> (reference §4.1).</summary>
    /// <returns>The qid, or the default when the reader has failed.</returns>
    public Qid ReadQid()
    {
        int at = _position;
        if (!Take(Qid.WireSize))
        {
            return default;
        }

        ReadOnlySpan<byte> qid = Span.Slice(at, Qid.WireSize);
        return new Qid(
            (QidType)qid[0],
            BinaryPrimitives.ReadUInt32LittleEndian(qid.Slice(1, 4)),
            BinaryPrimitives.ReadUInt64LittleEndian(qid.Slice(5, 8)));
    }

    /// <summary>Reads <c>len[2]</c> followed by that many bytes of strict UTF-8 without NUL.</summary>
    /// <returns>The string, or the empty string when the reader has failed.</returns>
    public string ReadString()
    {
        ushort length = ReadUInt16();
        int at = _position;
        if (!Take(length))
        {
            return string.Empty;
        }

        if (!NinePText.TryDecode(Span.Slice(at, length), out string value, out ProtocolErrorKind failure))
        {
            Fail(failure);
            return string.Empty;
        }

        return value;
    }

    /// <summary>
    /// Reads a file-name component: a string that is at most 255 bytes and obeys the name rules of
    /// reference §8 rule 3.
    /// </summary>
    /// <param name="allowParent">True inside a <c>Twalk</c>, where ".." is a legal element.</param>
    /// <param name="allowEmpty">True inside a <c>Txattrwalk</c>, where "" is the attribute list.</param>
    /// <returns>The name, or the empty string when the reader has failed.</returns>
    public string ReadName(bool allowParent, bool allowEmpty = false)
    {
        ushort length = ReadUInt16();
        if (_failed)
        {
            return string.Empty;
        }

        if (length > Constants.MaxNameLength)
        {
            Fail(ProtocolErrorKind.Name);
            return string.Empty;
        }

        int at = _position;
        if (!Take(length))
        {
            return string.Empty;
        }

        if (!NinePText.TryDecode(Span.Slice(at, length), out string value, out ProtocolErrorKind failure))
        {
            Fail(failure);
            return string.Empty;
        }

        if (!NinePText.IsLegalName(value, allowParent, allowEmpty))
        {
            Fail(ProtocolErrorKind.Name);
            return string.Empty;
        }

        return value;
    }

    /// <summary>Takes a view of the next <paramref name="count"/> bytes without copying them.</summary>
    /// <param name="count">How many bytes the field carries.</param>
    /// <returns>A view into the frame, empty when the reader has failed.</returns>
    public ReadOnlyMemory<byte> ReadBytes(int count)
    {
        int at = _position;
        return Take(count) ? _frame.Slice(at, count) : ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>Takes a view of everything that is left, and leaves the reader at the end.</summary>
    /// <returns>A view into the frame, empty when the reader has failed.</returns>
    public ReadOnlyMemory<byte> ReadRemaining() => ReadBytes(_failed ? 0 : Remaining);

    /// <summary>
    /// Asserts the message ended exactly where the reader did (reference §8 rule 2: trailing bytes
    /// are malformed).
    /// </summary>
    /// <returns>True when nothing is left over.</returns>
    public bool EnsureAtEnd() => _failed || Remaining == 0 || Fail(ProtocolErrorKind.Trailing);

    private bool Take(int length)
    {
        if (_failed)
        {
            return false;
        }

        if (length < 0 || length > Remaining)
        {
            return Fail(ProtocolErrorKind.Bounds);
        }

        _position += length;
        return true;
    }
}
