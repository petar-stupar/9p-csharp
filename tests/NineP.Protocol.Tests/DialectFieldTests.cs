using System.Buffers;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>
/// The dialect deltas of reference §3: <c>n_uname</c> on Tauth and Tattach (.u and .L),
/// <c>extension</c> on Tcreate, <c>errno</c> on Rerror and the four .u fields of the stat record.
/// Which of them is on the wire is decided by the session dialect and never by sniffing bytes,
/// so the same frame read in the wrong dialect is malformed rather than differently interpreted.
/// </summary>
public sealed class DialectFieldTests
{
    /// <summary>The vectors a 9P2000.u session would carry, one test case each.</summary>
    /// <returns>One row per vector.</returns>
    public static TheoryData<string, int> UnixVectors()
    {
        TheoryData<string, int> data = [];
        for (int i = 0; i < WireVectors.All.Count; i++)
        {
            if (WireVectors.All[i].Dialect == Dialect.P9_2000_u)
            {
                data.Add(WireVectors.All[i].Name, i);
            }
        }

        return data;
    }

    /// <summary>Every .u vector decodes and re-encodes to exactly the bytes it came from.</summary>
    /// <param name="name">The vector's name, so a failure says which one broke.</param>
    /// <param name="index">The vector's position in the fixture.</param>
    [Theory]
    [MemberData(nameof(UnixVectors))]
    public void EveryUnixVectorRoundTripsByteExactly(string name, int index)
    {
        WireVector vector = WireVectors.All[index];

        Assert.Equal(vector.ToBytes(), vector.Type switch
        {
            MessageType.Tauth => RoundTrip<Tauth>(vector),
            MessageType.Tattach => RoundTrip<Tattach>(vector),
            MessageType.Rerror => RoundTrip<Rerror>(vector),
            MessageType.Tcreate => RoundTrip<Tcreate>(vector),
            MessageType.Rstat => RoundTrip<Rstat>(vector),
            MessageType.Twstat => RoundTrip<Twstat>(vector),
            _ => throw new InvalidOperationException("vector " + name + " has no round trip"),
        });
    }

    /// <summary>The .u fields come back with the values the fixture recorded.</summary>
    [Fact]
    public void UnixFieldsDecodeIntoTheirRecords()
    {
        Assert.Equal(1000u, Decode<Tauth>(Unix(MessageType.Tauth)).NUname);
        Assert.Equal(1000u, Decode<Tattach>(Unix(MessageType.Tattach)).NUname);
        Assert.Equal(2, Decode<Rerror>(Unix(MessageType.Rerror)).Errno);
        Assert.Equal("/target", Decode<Tcreate>(Unix(MessageType.Tcreate)).Extension);

        StatRecord stat = Decode<Rstat>(Unix(MessageType.Rstat)).Stat;
        Assert.Equal("/target", stat.Extension);
        Assert.Equal(1000u, stat.NUid);
        Assert.Equal(100u, stat.NGid);
        Assert.Equal(1000u, stat.NMuid);
    }

    /// <summary>
    /// <c>n_uname</c> is on the wire in .L as well as in .u: the .u draft's §7.3 defines the field
    /// and its §2.2 synopsis omits it, and §7.3 is what Linux and diod implement (reference §3.1).
    /// </summary>
    [Fact]
    public void NUnameIsOnTheWireInDotLToo()
    {
        WireVector vector = WireVectors.All.First(
            v => v.Type == MessageType.Tattach && v.Dialect == Dialect.P9_2000_L);

        Tattach attach = MessageCodec.Decode<Tattach>(vector.Frame, Dialect.P9_2000_L);

        Assert.Equal(1000u, attach.NUname);
        Assert.Equal(Constants.NOFID, attach.Afid);

        ArrayBufferWriter<byte> buffer = new();
        MessageCodec.Encode(buffer, in attach, Dialect.P9_2000_L);
        Assert.Equal(vector.ToBytes(), buffer.WrittenSpan.ToArray());
    }

    /// <summary>A .u frame read in a 9P2000 session leaves its extra field over as trailing bytes.</summary>
    /// <param name="type">The message type whose .u vector is being read.</param>
    /// <param name="expected">The failure the decoder must report.</param>
    [Theory]
    [InlineData(MessageType.Tauth, ProtocolErrorKind.Trailing)]
    [InlineData(MessageType.Tattach, ProtocolErrorKind.Trailing)]
    [InlineData(MessageType.Rerror, ProtocolErrorKind.Trailing)]
    [InlineData(MessageType.Tcreate, ProtocolErrorKind.Trailing)]
    [InlineData(MessageType.Rstat, ProtocolErrorKind.Stat)]
    public void UnixFrameInBaseSessionIsRejected(MessageType type, ProtocolErrorKind expected) =>
        Assert.Equal(expected, FailureOf(Unix(type), Dialect.P9_2000));

    /// <summary>A 9P2000 frame read in a .u session runs out of bytes where the .u field would be.</summary>
    /// <param name="type">The message type whose 9P2000 vector is being read.</param>
    [Theory]
    [InlineData(MessageType.Tauth)]
    [InlineData(MessageType.Tattach)]
    [InlineData(MessageType.Rerror)]
    [InlineData(MessageType.Tcreate)]
    [InlineData(MessageType.Rstat)]
    public void BaseFrameInUnixSessionIsRejected(MessageType type) =>
        Assert.Equal(ProtocolErrorKind.Bounds, FailureOf(Base(type), Dialect.P9_2000_u));

    private static WireVector Unix(MessageType type) =>
        WireVectors.All.First(v => v.Type == type && v.Dialect == Dialect.P9_2000_u);

    private static WireVector Base(MessageType type) =>
        WireVectors.All.First(v => v.Type == type && v.Dialect == Dialect.P9_2000);

    private static TMessage Decode<TMessage>(WireVector vector)
        where TMessage : struct, IMessage =>
        MessageCodec.Decode<TMessage>(vector.Frame, Dialect.P9_2000_u);

    private static ProtocolErrorKind FailureOf(WireVector vector, Dialect dialect) => vector.Type switch
    {
        MessageType.Tauth => Failure<Tauth>(vector, dialect),
        MessageType.Tattach => Failure<Tattach>(vector, dialect),
        MessageType.Rerror => Failure<Rerror>(vector, dialect),
        MessageType.Tcreate => Failure<Tcreate>(vector, dialect),
        MessageType.Rstat => Failure<Rstat>(vector, dialect),
        _ => throw new InvalidOperationException("no cross-dialect case for " + vector.Name),
    };

    private static ProtocolErrorKind Failure<TMessage>(WireVector vector, Dialect dialect)
        where TMessage : struct, IMessage
    {
        Assert.False(MessageCodec.TryDecode(vector.Frame, dialect, out TMessage _, out ProtocolErrorKind failure));
        return failure;
    }

    private static byte[] RoundTrip<TMessage>(WireVector vector)
        where TMessage : struct, IMessage
    {
        TMessage message = MessageCodec.Decode<TMessage>(vector.Frame, Dialect.P9_2000_u);

        ArrayBufferWriter<byte> buffer = new();
        int written = MessageCodec.Encode(buffer, in message, Dialect.P9_2000_u);

        Assert.Equal(vector.Size, MessageCodec.GetEncodedSize(in message, Dialect.P9_2000_u));
        Assert.Equal(vector.Size, written);
        return buffer.WrittenSpan.ToArray();
    }
}
