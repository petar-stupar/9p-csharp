using System.Buffers.Binary;
using System.Text;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

/// <summary>
/// The primitive reader of reference §1 and the string and name rules of reference §8 rule 3.
/// Every case asserts the failure <em>kind</em>, not merely that something went wrong: the kind is
/// what tells a server whether the connection can be resynced.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class WireReaderTests
{



    /// <summary>"." is never a legal name; ".." is legal only inside a Twalk.</summary>
    [Theory]
    [InlineData(".", false, false)]
    [InlineData(".", true, false)]
    [InlineData("..", false, false)]
    [InlineData("..", true, true)]
    [InlineData("users", false, true)]
    public void DotAndDotDotFollowTheWalkRule(string name, bool allowParent, bool legal)
    {
        WireReader reader = new(StringField(Encoding.UTF8.GetBytes(name)));
        string read = reader.ReadName(allowParent);

        if (legal)
        {
            Assert.False(reader.Failed);
            Assert.Equal(name, read);
        }
        else
        {
            Assert.True(reader.Failed);
            Assert.Equal(ProtocolErrorKind.Name, reader.Failure);
        }
    }

    /// <summary>
    /// An empty name names no file. It used to be accepted everywhere, so <c>Tmkdir name=""</c>
    /// made a directory whose listing entry had no name and <c>Twalk [""]</c> bound a fid to it.
    /// <c>Txattrwalk</c> is the one message where the empty name means something: the list of
    /// attribute names.
    /// </summary>
    /// <param name="allowEmpty">Whether the message being decoded is a <c>Txattrwalk</c>.</param>
    /// <param name="legal">Whether the read is expected to succeed.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void AnEmptyNameIsOnlyLegalForXattrwalk(bool allowEmpty, bool legal)
    {
        WireReader reader = new(StringField([]));
        string read = reader.ReadName(allowParent: false, allowEmpty);

        Assert.Equal(string.Empty, read);
        Assert.Equal(!legal, reader.Failed);

        if (!legal)
        {
            Assert.Equal(ProtocolErrorKind.Name, reader.Failure);
        }
    }

    /// <summary>A name of 255 bytes is legal; 256 is a Name failure, refused before decoding.</summary>
    [Fact]
    public void NameLengthCapIs255Bytes()
    {
        WireReader legal = new(StringField(Encoding.ASCII.GetBytes(new string('a', 255))));
        Assert.Equal(new string('a', 255), legal.ReadName(allowParent: false));
        Assert.False(legal.Failed);

        WireReader tooLong = new(StringField(Encoding.ASCII.GetBytes(new string('a', 256))));
        Assert.Equal(string.Empty, tooLong.ReadName(allowParent: false));
        Assert.True(tooLong.Failed);
        Assert.Equal(ProtocolErrorKind.Name, tooLong.Failure);
    }

    /// <summary>A string may be 65535 bytes long: that is the maximum len[2] can express.</summary>
    [Fact]
    public void AcceptsTheLongestPossibleString()
    {
        string value = new('x', 65535);
        WireReader reader = new(StringField(Encoding.ASCII.GetBytes(value)));

        Assert.Equal(value, reader.ReadString());
        Assert.False(reader.Failed);
        Assert.Equal(0, reader.Remaining);
    }





    /// <summary>Every primitive reads back little-endian, exactly as reference §1 lays it out.</summary>
    [Fact]
    public void ReadsPrimitivesLittleEndian()
    {
        byte[] frame =
        [
            0xAB,
            0x34, 0x12,
            0x78, 0x56, 0x34, 0x12,
            0xF0, 0xDE, 0xBC, 0x9A, 0x78, 0x56, 0x34, 0x12,
            0x80, 0x02, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];

        WireReader reader = new(frame);

        Assert.Equal(0xAB, reader.ReadUInt8());
        Assert.Equal(0x1234, reader.ReadUInt16());
        Assert.Equal(0x12345678u, reader.ReadUInt32());
        Assert.Equal(0x123456789ABCDEF0ul, reader.ReadUInt64());
        Assert.Equal(new Qid(QidType.QTDIR, 2, 7), reader.ReadQid());
        Assert.True(reader.EnsureAtEnd());
        Assert.False(reader.Failed);
    }

    /// <summary>A payload is a view of the frame, so nothing is copied to hand it to a handler.</summary>
    [Fact]
    public void ReadBytesTakesAViewOfTheFrame()
    {
        byte[] frame = [1, 2, 3, 4, 5, 6];
        WireReader reader = new(frame);

        reader.ReadUInt16();
        ReadOnlyMemory<byte> payload = reader.ReadBytes(3);

        Assert.Equal(new byte[] { 3, 4, 5 }, payload.ToArray());
        Assert.Equal(1, reader.Remaining);
    }

    private static byte[] StringField(byte[] payload)
    {
        byte[] frame = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)payload.Length);
        payload.CopyTo(frame, 2);
        return frame;
    }
}
