using System.Buffers.Binary;
using System.Text;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using Xunit;

namespace NineP.Protocol.Tests.Robustness;

/// <summary>
/// The primitive reader of reference §1 and the string and name rules of reference §8 rule 3.
/// Every case asserts the failure <em>kind</em>, not merely that something went wrong: the kind is
/// what tells a server whether the connection can be resynced.
/// </summary>
[Trait("Category", "Robustness")]
public sealed class WireReaderRejectionTests
{
    /// <summary>A string whose bytes are not valid UTF-8 is a Utf8 failure, not a substitution.</summary>
    [Theory]
    [InlineData(new byte[] { 0xC0, 0x80 })]
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]
    [InlineData(new byte[] { 0xE2, 0x82 })]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0x41, 0x80, 0x42 })]
    public void RejectsInvalidUtf8(byte[] payload)
    {
        WireReader reader = new(StringField(payload));

        Assert.Equal(string.Empty, reader.ReadString());
        Assert.True(reader.Failed);
        Assert.Equal(ProtocolErrorKind.Utf8, reader.Failure);
    }

    /// <summary>NUL is illegal inside any 9P string, and is caught before decoding.</summary>
    [Fact]
    public void RejectsNulInString()
    {
        WireReader reader = new(StringField([0x41, 0x00, 0x42]));

        Assert.Equal(string.Empty, reader.ReadString());
        Assert.True(reader.Failed);
        Assert.Equal(ProtocolErrorKind.Nul, reader.Failure);
    }

    /// <summary>A name may not contain '/': path separators never cross the wire inside a name.</summary>
    [Fact]
    public void RejectsSlashInName()
    {
        WireReader reader = new(StringField(Encoding.ASCII.GetBytes("etc/passwd")));

        Assert.Equal(string.Empty, reader.ReadName(allowParent: false));
        Assert.True(reader.Failed);
        Assert.Equal(ProtocolErrorKind.Name, reader.Failure);
    }





    /// <summary>A counted field that runs past the frame is a Bounds failure, not an exception.</summary>
    [Fact]
    public void RejectsStringThatRunsPastTheFrame()
    {
        byte[] frame = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, 10);

        WireReader reader = new(frame);
        Assert.Equal(string.Empty, reader.ReadString());
        Assert.True(reader.Failed);
        Assert.Equal(ProtocolErrorKind.Bounds, reader.Failure);
    }

    /// <summary>Reading an integer off the end of a short buffer is a Bounds failure.</summary>
    [Fact]
    public void RejectsIntegerPastTheEnd()
    {
        WireReader reader = new(new byte[3]);

        Assert.Equal(0u, reader.ReadUInt32());
        Assert.True(reader.Failed);
        Assert.Equal(ProtocolErrorKind.Bounds, reader.Failure);
    }

    /// <summary>The first failure is the one that is reported; later reads do not overwrite it.</summary>
    [Fact]
    public void FirstFailureIsSticky()
    {
        WireReader reader = new(StringField([0xFF]));

        reader.ReadString();
        Assert.Equal(ProtocolErrorKind.Utf8, reader.Failure);

        reader.ReadUInt64();
        Assert.Equal(ProtocolErrorKind.Utf8, reader.Failure);
    }

    /// <summary>Bytes left after the last field are malformed (reference §8 rule 2).</summary>
    [Fact]
    public void TrailingBytesAreRejected()
    {
        WireReader reader = new(new byte[] { 1, 2, 3, 4, 9 });

        Assert.Equal(0x04030201u, reader.ReadUInt32());
        Assert.False(reader.EnsureAtEnd());
        Assert.Equal(ProtocolErrorKind.Trailing, reader.Failure);
    }



    private static byte[] StringField(byte[] payload)
    {
        byte[] frame = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)payload.Length);
        payload.CopyTo(frame, 2);
        return frame;
    }
}
