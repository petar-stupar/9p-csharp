using System.Buffers;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>
/// <c>Tfsync</c> has two shapes in the wild and the sources disagree: diod and v9fs carry
/// <c>datasync[4]</c> (15 bytes), hugelgupf/p9 sends only <c>fid[4]</c> (11 bytes). We decode both
/// and always encode the long one (S-19), and this is the sole short frame reference §8 rule 2
/// allows.
/// </summary>
public sealed class TfsyncTests
{
    private const string ShortVectorName = "Tfsync (no datasync)";
    private const int LongFrameSize = 15;
    private const int ShortFrameSize = 11;

    /// <summary>The 11-byte frame decodes with datasync 0 and re-encodes as the 15-byte frame.</summary>
    [Fact]
    public void ShortDecodesLongEncodes()
    {
        WireVector shortForm = WireVectors.All.First(v => v.Name == ShortVectorName);
        WireVector longForm = WireVectors.All.First(
            v => v.Type == MessageType.Tfsync && v.Name != ShortVectorName);

        Assert.Equal(ShortFrameSize, shortForm.Size);
        Assert.Equal(LongFrameSize, longForm.Size);

        Tfsync decoded = MessageCodec.Decode<Tfsync>(shortForm.Frame, Dialect.P9_2000_L);

        Assert.Equal((uint)shortForm.Num("fid"), decoded.Fid);
        Assert.Equal(0u, decoded.Datasync);
        Assert.Equal(LongFrameSize, MessageCodec.GetEncodedSize(in decoded, Dialect.P9_2000_L));

        ArrayBufferWriter<byte> buffer = new();
        MessageCodec.Encode(buffer, in decoded, Dialect.P9_2000_L);

        // The long golden vector carries datasync 0 for the same fid, so the two forms converge
        // on exactly one frame once they have been through us.
        Assert.Equal(0u, longForm.Num("datasync"));
        Assert.Equal((uint)longForm.Num("fid"), decoded.Fid);
        Assert.Equal(longForm.ToBytes(), buffer.WrittenSpan.ToArray());
    }

    /// <summary>The 15-byte frame still decodes to the value it carries and round-trips.</summary>
    [Fact]
    public void LongFormKeepsItsDatasync()
    {
        WireVector longForm = WireVectors.All.First(
            v => v.Type == MessageType.Tfsync && v.Name != ShortVectorName);

        Tfsync decoded = MessageCodec.Decode<Tfsync>(longForm.Frame, Dialect.P9_2000_L);
        Tfsync set = decoded with { Datasync = 1 };

        ArrayBufferWriter<byte> buffer = new();
        MessageCodec.Encode(buffer, in set, Dialect.P9_2000_L);

        Assert.Equal(LongFrameSize, buffer.WrittenCount);
        Assert.Equal(1u, MessageCodec.Decode<Tfsync>(buffer.WrittenMemory, Dialect.P9_2000_L).Datasync);
    }

    /// <summary>
    /// No other message may drop a trailing fixed-width field: a <c>Tclunk</c> without its fid and
    /// a <c>Tlopen</c> without its flags are both malformed (AC-d).
    /// </summary>
    /// <param name="type">The message type whose vector is being cut short.</param>
    /// <param name="keep">How many bytes of the frame to keep.</param>
    [Theory]
    [InlineData(MessageType.Tclunk, Constants.HDRSZ)]
    [InlineData(MessageType.Tlopen, ShortFrameSize)]
    [InlineData(MessageType.Treadlink, Constants.HDRSZ)]
    public void ShortFormLegalOnlyForTfsync(MessageType type, int keep)
    {
        WireVector vector = WireVectors.All.First(v => v.Type == type);
        byte[] frame = vector.ToBytes()[..keep];
        frame[0] = (byte)keep;

        Assert.Equal(ProtocolErrorKind.Bounds, FailureOf(type, frame, vector.Dialect));
    }

    /// <summary>A <c>Tfsync</c> that is neither 11 nor 15 bytes is malformed like anything else.</summary>
    [Fact]
    public void ATfsyncOfAnyOtherLengthIsRejected()
    {
        WireVector longForm = WireVectors.All.First(
            v => v.Type == MessageType.Tfsync && v.Name != ShortVectorName);

        byte[] frame = longForm.ToBytes()[..(LongFrameSize - 1)];
        frame[0] = LongFrameSize - 1;

        Assert.False(MessageCodec.TryDecode(
            frame, Dialect.P9_2000_L, out Tfsync _, out ProtocolErrorKind failure));
        Assert.Equal(ProtocolErrorKind.Bounds, failure);
    }

    private static ProtocolErrorKind FailureOf(MessageType type, byte[] frame, Dialect dialect)
    {
        switch (type)
        {
            case MessageType.Tclunk:
                Assert.False(MessageCodec.TryDecode(frame, dialect, out Tclunk _, out ProtocolErrorKind clunk));
                return clunk;
            case MessageType.Tlopen:
                Assert.False(MessageCodec.TryDecode(frame, dialect, out Tlopen _, out ProtocolErrorKind open));
                return open;
            case MessageType.Treadlink:
                Assert.False(MessageCodec.TryDecode(frame, dialect, out Treadlink _, out ProtocolErrorKind link));
                return link;
            default:
                throw new InvalidOperationException("no short-frame case for " + type);
        }
    }
}
