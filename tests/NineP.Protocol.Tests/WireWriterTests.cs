using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>The primitive writer of reference §1, checked by reading its output back.</summary>
public sealed class WireWriterTests
{
    /// <summary>Every primitive is written little-endian and reads back unchanged.</summary>
    [Fact]
    public void PrimitivesRoundTrip()
    {
        byte[] buffer = new byte[64];
        WireWriter writer = new(buffer);

        writer.WriteUInt8(0xAB);
        writer.WriteUInt16(0x1234);
        writer.WriteUInt32(0x12345678);
        writer.WriteUInt64(0x123456789ABCDEF0);
        writer.WriteQid(new Qid(QidType.QTDIR, 2, 7));
        writer.WriteString("users");
        writer.WriteBytes([9, 8, 7]);

        WireReader reader = new(buffer.AsMemory(0, writer.Position));

        Assert.Equal(0xAB, reader.ReadUInt8());
        Assert.Equal(0x1234, reader.ReadUInt16());
        Assert.Equal(0x12345678u, reader.ReadUInt32());
        Assert.Equal(0x123456789ABCDEF0ul, reader.ReadUInt64());
        Assert.Equal(new Qid(QidType.QTDIR, 2, 7), reader.ReadQid());
        Assert.Equal("users", reader.ReadString());
        Assert.Equal(new byte[] { 9, 8, 7 }, reader.ReadBytes(3).ToArray());
        Assert.True(reader.EnsureAtEnd());
    }

    /// <summary>A string is written as len[2] plus its UTF-8 bytes, with no terminator.</summary>
    [Fact]
    public void StringIsLengthPrefixedUtf8WithoutTerminator()
    {
        byte[] buffer = new byte[32];
        WireWriter writer = new(buffer);

        writer.WriteString("é");

        Assert.Equal(4, writer.Position);
        Assert.Equal(new byte[] { 0x02, 0x00, 0xC3, 0xA9 }, buffer[..4]);
    }

    /// <summary>
    /// A reserved field can be back-patched once the body's length is known, which is how size[4]
    /// comes to hold the length of a message that was written before it was measured.
    /// </summary>
    [Fact]
    public void SizeIsBackPatchedAfterTheBody()
    {
        byte[] buffer = new byte[16];
        WireWriter writer = new(buffer);

        writer.WriteUInt32(0);
        writer.WriteUInt8((byte)MessageType.Rclunk);
        writer.WriteUInt16(42);
        writer.PatchUInt32(0, (uint)writer.Position);

        WireReader reader = new(buffer.AsMemory(0, writer.Position));
        Assert.Equal(7u, reader.ReadUInt32());
        Assert.Equal((byte)MessageType.Rclunk, reader.ReadUInt8());
        Assert.Equal(42, reader.ReadUInt16());
    }
}
