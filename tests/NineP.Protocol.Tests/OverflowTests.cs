using System.Buffers;
using System.Buffers.Binary;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>The u64 overflow guards of reference §8 rule 5.</summary>
public sealed class OverflowTests
{
    /// <summary>
    /// A Twrite whose offset plus count would wrap past 2^64 is refused. Without the guard a
    /// server would compute a range nowhere near the one the client named.
    /// </summary>
    [Fact]
    public void WriteOffsetOverflowRejected()
    {
        byte[] frame = Twrite(ulong.MaxValue - 1, [1, 2, 3, 4]);

        Assert.False(MessageCodec.TryDecode(frame, Dialect.P9_2000, out Twrite _, out ProtocolErrorKind failure));
        Assert.Equal(ProtocolErrorKind.Overflow, failure);
    }

    /// <summary>
    /// <c>errno[4]</c> and <c>ecode[4]</c> are unsigned on the wire and signed in the API. A value
    /// above <c>int.MaxValue</c> is no errno any peer defines, and reading it as a negative
    /// number would produce an error value this side could never encode again, so it is refused.
    /// </summary>
    [Theory]
    [InlineData(0x80000000u)]
    [InlineData(0xFFFFFFFFu)]
    public void ErrnoAboveInt32MaxIsRejected(uint raw)
    {
        byte[] linux = ErrorFrame(new Rlerror(1, int.MaxValue), Dialect.P9_2000_L, raw);
        byte[] unix = ErrorFrame(new Rerror(1, "i/o error", int.MaxValue), Dialect.P9_2000_u, raw);

        Assert.False(MessageCodec.TryDecode(linux, Dialect.P9_2000_L, out Rlerror _, out ProtocolErrorKind ecode));
        Assert.False(MessageCodec.TryDecode(unix, Dialect.P9_2000_u, out Rerror _, out ProtocolErrorKind errno));
        Assert.Equal(ProtocolErrorKind.Overflow, ecode);
        Assert.Equal(ProtocolErrorKind.Overflow, errno);
    }

    /// <summary>The largest errno the signed type holds is still read as it was sent.</summary>
    [Fact]
    public void ErrnoAtInt32MaxIsDecoded()
    {
        byte[] linux = ErrorFrame(new Rlerror(1, int.MaxValue), Dialect.P9_2000_L, int.MaxValue);

        Assert.Equal(int.MaxValue, MessageCodec.Decode<Rlerror>(linux, Dialect.P9_2000_L).Ecode);
    }

    /// <summary>An offset that leaves exactly enough room for the payload is legal.</summary>
    [Fact]
    public void WriteOffsetAtTheEdgeIsAccepted()
    {
        byte[] frame = Twrite(ulong.MaxValue - 4, [1, 2, 3, 4]);

        Twrite message = MessageCodec.Decode<Twrite>(frame, Dialect.P9_2000);

        Assert.Equal(ulong.MaxValue - 4, message.Offset);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, message.Data.ToArray());
    }

    /// <summary>
    /// A <c>Tread</c> is <b>not</b> guarded, because reference §8 rule 5 names <c>Twrite</c>,
    /// <c>Tsetattr</c> and <c>Tlock</c> and only those. A read past the end of the address space
    /// is a read that returns nothing; refusing it was a protocol error that closed the
    /// connection, so a client probing near EOF lost its session where Plan 9 answers count = 0.
    /// </summary>
    [Fact]
    public void ReadOffsetAtTheEndOfTheAddressSpaceIsDecoded()
    {
        byte[] frame = Tread(ulong.MaxValue, 1);

        Tread message = MessageCodec.Decode<Tread>(frame, Dialect.P9_2000);

        Assert.Equal(ulong.MaxValue, message.Offset);
        Assert.Equal(1u, message.Count);
    }

    /// <summary>
    /// Twrite.count must equal size - 23. A count that overruns the frame is Bounds; one that
    /// falls short leaves trailing bytes. Neither is read as a shorter or longer write.
    /// </summary>
    [Theory]
    [InlineData(5, ProtocolErrorKind.Bounds)]
    [InlineData(3, ProtocolErrorKind.Trailing)]
    public void WriteCountMustAgreeWithSize(uint count, ProtocolErrorKind expected)
    {
        byte[] frame = Twrite(0, [1, 2, 3, 4]);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(19, 4), count);

        Assert.False(MessageCodec.TryDecode(frame, Dialect.P9_2000, out Twrite _, out ProtocolErrorKind failure));
        Assert.Equal(expected, failure);
    }

    /// <summary>A frame whose size[4] disagrees with its length is a Size failure.</summary>
    [Fact]
    public void SizeMustEqualTheFramesLength()
    {
        byte[] frame = Tread(0, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frame.Length + 1);

        Assert.False(MessageCodec.TryDecode(frame, Dialect.P9_2000, out Tread _, out ProtocolErrorKind failure));
        Assert.Equal(ProtocolErrorKind.Size, failure);
    }

    /// <summary>A frame shorter than the seven-byte header cannot even be classified.</summary>
    [Fact]
    public void FrameShorterThanAHeaderIsASizeFailure()
    {
        Assert.False(MessageCodec.TryDecode(new byte[6], Dialect.P9_2000, out Tclunk _, out ProtocolErrorKind failure));
        Assert.Equal(ProtocolErrorKind.Size, failure);
    }

    /// <summary>Bytes after the last field of a message are malformed (reference §8 rule 2).</summary>
    [Fact]
    public void TrailingBytesAreRejected()
    {
        byte[] frame = [.. Tread(0, 16), 0];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frame.Length);

        Assert.False(MessageCodec.TryDecode(frame, Dialect.P9_2000, out Tread _, out ProtocolErrorKind failure));
        Assert.Equal(ProtocolErrorKind.Trailing, failure);
    }

    /// <summary>A Twalk claiming more than MAXWELEM names is refused before anything is allocated.</summary>
    [Fact]
    public void WalkBeyondMaxWelemIsRejected()
    {
        byte[] frame = new byte[17];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frame.Length);
        frame[4] = (byte)MessageType.Twalk;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(15, 2), Constants.MAXWELEM + 1);

        Assert.False(MessageCodec.TryDecode(frame, Dialect.P9_2000, out Twalk _, out ProtocolErrorKind failure));
        Assert.Equal(ProtocolErrorKind.NWName, failure);
    }

    private static byte[] Twrite(ulong offset, byte[] payload)
    {
        byte[] frame = new byte[Constants.TwriteHeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frame.Length);
        frame[4] = (byte)MessageType.Twrite;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7, 4), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(11, 8), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(19, 4), (uint)payload.Length);
        payload.CopyTo(frame, Constants.TwriteHeaderSize);
        return frame;
    }

    private static byte[] Tread(ulong offset, uint count)
    {
        byte[] frame = new byte[23];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frame.Length);
        frame[4] = (byte)MessageType.Tread;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7, 4), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(11, 8), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(19, 4), count);
        return frame;
    }
    // Encodes an error reply and then overwrites its trailing errno[4] / ecode[4] with a raw
    // value the encoder itself would refuse.
    private static byte[] ErrorFrame<TMessage>(TMessage message, Dialect dialect, uint raw)
        where TMessage : struct, IMessage
    {
        ArrayBufferWriter<byte> buffer = new();
        MessageCodec.Encode(buffer, in message, dialect);
        byte[] frame = buffer.WrittenSpan.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(frame.Length - 4), raw);
        return frame;
    }

}
