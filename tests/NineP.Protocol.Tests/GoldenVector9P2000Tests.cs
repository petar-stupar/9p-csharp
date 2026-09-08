using System.Buffers;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>
/// The 9P2000 half of the golden vectors of reference §9, decoded field by field. "It did not
/// throw" is not the assertion: every field the fixture records is compared with what came out of
/// the decoder.
/// </summary>
public sealed class GoldenVector9P2000Tests
{
    /// <summary>The vectors a 9P2000 session would carry, one test case each.</summary>
    /// <returns>One row per vector.</returns>
    public static TheoryData<string, int> Vectors()
    {
        TheoryData<string, int> data = [];
        for (int i = 0; i < WireVectors.All.Count; i++)
        {
            WireVector vector = WireVectors.All[i];
            if (vector.Dialect == Dialect.P9_2000)
            {
                data.Add(vector.Name, i);
            }
        }

        return data;
    }

    /// <summary>Every 9P2000 vector decodes, and every field it records comes back unchanged.</summary>
    /// <param name="name">The vector's name, so a failure says which one broke.</param>
    /// <param name="index">The vector's position in the fixture.</param>
    [Theory]
    [MemberData(nameof(Vectors))]
    public void Every9P2000VectorDecodesToItsFields(string name, int index)
    {
        WireVector vector = WireVectors.All[index];

        Assert.Equal(vector.Size, vector.Frame.Length);
        Assert.Equal((uint)vector.Size, MessageCodec.PeekSize(vector.Frame.Span));
        Assert.Equal(vector.Type, MessageCodec.PeekType(vector.Frame.Span));
        Assert.Equal((ushort)vector.Num("tag"), MessageCodec.PeekTag(vector.Frame.Span));

        AssertFields(name, vector);
    }

    /// <summary>The fixture covers every type a 9P2000 session can carry except Rlerror's peers.</summary>
    [Fact]
    public void FixtureCoversTheWholeOf9P2000()
    {
        HashSet<MessageType> covered = [.. WireVectors.All
            .Where(v => v.Dialect == Dialect.P9_2000)
            .Select(v => v.Type)];

        foreach (MessageType type in Enum.GetValues<MessageType>())
        {
            if (MessageTypes.IsLegal(type, Dialect.P9_2000))
            {
                Assert.Contains(type, covered);
            }
        }
    }

    /// <summary>
    /// A frame whose type byte is not the record the caller asked for is a Type failure, never a
    /// misread of one message as another.
    /// </summary>
    [Fact]
    public void DecodingAsTheWrongRecordIsATypeFailure()
    {
        WireVector tclunk = WireVectors.All.First(v => v.Type == MessageType.Tclunk);

        Assert.False(MessageCodec.TryDecode(tclunk.Frame, Dialect.P9_2000, out Tremove _, out ProtocolErrorKind failure));
        Assert.Equal(ProtocolErrorKind.Type, failure);
    }

    /// <summary>
    /// A 9P2000-only message is not legal in a 9P2000.L session, and the decoder refuses it on the
    /// type check before it parses a single field.
    /// </summary>
    [Fact]
    public void A9P2000OnlyMessageIsIllegalInDotL()
    {
        WireVector tstat = WireVectors.All.First(v => v.Type == MessageType.Tstat);

        Assert.False(MessageCodec.TryDecode(tstat.Frame, Dialect.P9_2000_L, out Tstat _, out ProtocolErrorKind failure));
        Assert.Equal(ProtocolErrorKind.Type, failure);
    }

    /// <summary>
    /// Every 9P2000 vector re-encodes to exactly the bytes it came from, and the size the encoder
    /// predicts is the size it writes.
    /// </summary>
    /// <param name="name">The vector's name, so a failure says which one broke.</param>
    /// <param name="index">The vector's position in the fixture.</param>
    [Theory]
    [MemberData(nameof(Vectors))]
    public void Every9P2000VectorReEncodesByteIdentically(string name, int index)
    {
        WireVector vector = WireVectors.All[index];

        Assert.Equal(vector.ToBytes(), ReEncode(name, vector));
    }

    private static byte[] ReEncode(string name, WireVector vector) => vector.Type switch
    {
        MessageType.Tversion => RoundTrip<Tversion>(vector),
        MessageType.Rversion => RoundTrip<Rversion>(vector),
        MessageType.Tauth => RoundTrip<Tauth>(vector),
        MessageType.Rauth => RoundTrip<Rauth>(vector),
        MessageType.Tattach => RoundTrip<Tattach>(vector),
        MessageType.Rattach => RoundTrip<Rattach>(vector),
        MessageType.Rerror => RoundTrip<Rerror>(vector),
        MessageType.Tflush => RoundTrip<Tflush>(vector),
        MessageType.Rflush => RoundTrip<Rflush>(vector),
        MessageType.Twalk => RoundTrip<Twalk>(vector),
        MessageType.Rwalk => RoundTrip<Rwalk>(vector),
        MessageType.Tread => RoundTrip<Tread>(vector),
        MessageType.Rread => RoundTrip<Rread>(vector),
        MessageType.Twrite => RoundTrip<Twrite>(vector),
        MessageType.Rwrite => RoundTrip<Rwrite>(vector),
        MessageType.Tclunk => RoundTrip<Tclunk>(vector),
        MessageType.Rclunk => RoundTrip<Rclunk>(vector),
        MessageType.Tremove => RoundTrip<Tremove>(vector),
        MessageType.Rremove => RoundTrip<Rremove>(vector),
        MessageType.Topen => RoundTrip<Topen>(vector),
        MessageType.Ropen => RoundTrip<Ropen>(vector),
        MessageType.Tcreate => RoundTrip<Tcreate>(vector),
        MessageType.Rcreate => RoundTrip<Rcreate>(vector),
        MessageType.Tstat => RoundTrip<Tstat>(vector),
        MessageType.Rstat => RoundTrip<Rstat>(vector),
        MessageType.Twstat => RoundTrip<Twstat>(vector),
        MessageType.Rwstat => RoundTrip<Rwstat>(vector),
        _ => throw new InvalidOperationException("vector " + name + " has no round trip"),
    };

    private static byte[] RoundTrip<TMessage>(WireVector vector)
        where TMessage : struct, IMessage
    {
        TMessage message = MessageCodec.Decode<TMessage>(vector.Frame, Dialect.P9_2000);

        ArrayBufferWriter<byte> buffer = new();
        int written = MessageCodec.Encode(buffer, in message, Dialect.P9_2000);

        Assert.Equal(vector.Size, MessageCodec.GetEncodedSize(in message, Dialect.P9_2000));
        Assert.Equal(vector.Size, written);
        return buffer.WrittenSpan.ToArray();
    }

    private static void AssertFields(string name, WireVector vector)
    {
        switch (vector.Type)
        {
            case MessageType.Tversion:
                {
                    Tversion message = Decode<Tversion>(vector);
                    AssertVersion(message.Msize, message.Version, vector);
                    break;
                }

            case MessageType.Rversion:
                {
                    Rversion message = Decode<Rversion>(vector);
                    AssertVersion(message.Msize, message.Version, vector);
                    break;
                }

            case MessageType.Tauth:
                AssertTauth(Decode<Tauth>(vector), vector);
                break;
            case MessageType.Rauth:
                Assert.Equal(vector.QidOf("aqid"), Decode<Rauth>(vector).Aqid);
                break;
            case MessageType.Tattach:
                AssertTattach(Decode<Tattach>(vector), vector);
                break;
            case MessageType.Rattach:
                Assert.Equal(vector.QidOf("qid"), Decode<Rattach>(vector).Qid);
                break;
            case MessageType.Rerror:
                AssertRerror(Decode<Rerror>(vector), vector);
                break;
            case MessageType.Tflush:
                Assert.Equal((ushort)vector.Num("oldtag"), Decode<Tflush>(vector).OldTag);
                break;
            case MessageType.Rflush:
                Assert.Equal((ushort)vector.Num("tag"), Decode<Rflush>(vector).Tag);
                break;
            case MessageType.Twalk:
                AssertTwalk(Decode<Twalk>(vector), vector);
                break;
            case MessageType.Rwalk:
                AssertRwalk(Decode<Rwalk>(vector), vector);
                break;
            case MessageType.Topen:
                AssertTopen(Decode<Topen>(vector), vector);
                break;
            case MessageType.Ropen:
                {
                    Ropen message = Decode<Ropen>(vector);
                    AssertOpened(message.Qid, message.Iounit, vector);
                    break;
                }

            case MessageType.Tcreate:
                AssertTcreate(Decode<Tcreate>(vector), vector);
                break;
            case MessageType.Rcreate:
                {
                    Rcreate message = Decode<Rcreate>(vector);
                    AssertOpened(message.Qid, message.Iounit, vector);
                    break;
                }

            case MessageType.Tread:
                AssertTread(Decode<Tread>(vector), vector);
                break;
            case MessageType.Rread:
                AssertPayload(Decode<Rread>(vector).Data, vector, Constants.RreadHeaderSize);
                break;
            case MessageType.Twrite:
                AssertTwrite(Decode<Twrite>(vector), vector);
                break;
            case MessageType.Rwrite:
                Assert.Equal((uint)vector.Num("count"), Decode<Rwrite>(vector).Count);
                break;
            case MessageType.Tclunk:
                Assert.Equal((uint)vector.Num("fid"), Decode<Tclunk>(vector).Fid);
                break;
            case MessageType.Rclunk:
                Assert.Equal((ushort)vector.Num("tag"), Decode<Rclunk>(vector).Tag);
                break;
            case MessageType.Tremove:
                Assert.Equal((uint)vector.Num("fid"), Decode<Tremove>(vector).Fid);
                break;
            case MessageType.Rremove:
                Assert.Equal((ushort)vector.Num("tag"), Decode<Rremove>(vector).Tag);
                break;
            case MessageType.Tstat:
                Assert.Equal((uint)vector.Num("fid"), Decode<Tstat>(vector).Fid);
                break;
            case MessageType.Rstat:
                AssertRstat(Decode<Rstat>(vector), vector);
                break;
            case MessageType.Twstat:
                AssertTwstat(Decode<Twstat>(vector), vector);
                break;
            case MessageType.Rwstat:
                Assert.Equal((ushort)vector.Num("tag"), Decode<Rwstat>(vector).Tag);
                break;
            default:
                Assert.Fail($"vector {name} has no field assertions");
                break;
        }
    }

    private static TMessage Decode<TMessage>(WireVector vector)
        where TMessage : struct, IMessage =>
        MessageCodec.Decode<TMessage>(vector.Frame, Dialect.P9_2000);

    private static void AssertVersion(uint msize, string version, WireVector vector)
    {
        Assert.Equal((uint)vector.Num("msize"), msize);
        Assert.Equal(vector.Str("version"), version);
    }

    private static void AssertTauth(Tauth message, WireVector vector)
    {
        Assert.Equal((uint)vector.Num("afid"), message.Afid);
        Assert.Equal(vector.Str("uname"), message.Uname);
        Assert.Equal(vector.Str("aname"), message.Aname);
        Assert.Equal(Constants.NONUNAME, message.NUname);
    }

    private static void AssertTattach(Tattach message, WireVector vector)
    {
        Assert.Equal((uint)vector.Num("fid"), message.Fid);
        Assert.Equal((uint)vector.Num("afid"), message.Afid);
        Assert.Equal(vector.Str("uname"), message.Uname);
        Assert.Equal(vector.Str("aname"), message.Aname);
        Assert.Equal(Constants.NONUNAME, message.NUname);
    }

    private static void AssertRerror(Rerror message, WireVector vector)
    {
        Assert.Equal(vector.Str("ename"), message.Ename);
        Assert.Equal(0, message.Errno);
    }

    private static void AssertTwalk(Twalk message, WireVector vector)
    {
        Assert.Equal((uint)vector.Num("fid"), message.Fid);
        Assert.Equal((uint)vector.Num("newfid"), message.NewFid);

        string[] expected = [.. vector.Fields.GetProperty("wname").EnumerateArray().Select(e => e.GetString()!)];
        Assert.Equal(expected, message.Wnames);
    }

    private static void AssertRwalk(Rwalk message, WireVector vector)
    {
        Qid[] expected = [.. vector.Fields.GetProperty("wqid").EnumerateArray().Select(WireVector.QidFrom)];
        Assert.Equal(expected, message.Wqids);
    }

    private static void AssertTopen(Topen message, WireVector vector)
    {
        Assert.Equal((uint)vector.Num("fid"), message.Fid);
        Assert.Equal((byte)vector.Num("mode"), message.Mode);
    }

    private static void AssertOpened(Qid qid, uint iounit, WireVector vector)
    {
        Assert.Equal(vector.QidOf("qid"), qid);
        Assert.Equal((uint)vector.Num("iounit"), iounit);
    }

    private static void AssertTcreate(Tcreate message, WireVector vector)
    {
        Assert.Equal((uint)vector.Num("fid"), message.Fid);
        Assert.Equal(vector.Str("name"), message.Name);
        Assert.Equal((uint)vector.Num("perm"), message.Perm);
        Assert.Equal((byte)vector.Num("mode"), message.Mode);
        Assert.Null(message.Extension);
    }

    private static void AssertTread(Tread message, WireVector vector)
    {
        Assert.Equal((uint)vector.Num("fid"), message.Fid);
        Assert.Equal(vector.Num("offset"), message.Offset);
        Assert.Equal((uint)vector.Num("count"), message.Count);
    }

    private static void AssertTwrite(Twrite message, WireVector vector)
    {
        Assert.Equal((uint)vector.Num("fid"), message.Fid);
        Assert.Equal(vector.Num("offset"), message.Offset);
        AssertPayload(message.Data, vector, Constants.TwriteHeaderSize);
    }

    private static void AssertPayload(ReadOnlyMemory<byte> data, WireVector vector, int headerSize)
    {
        // The fixture writes the payload as display text, so the bytes are taken from the frame
        // itself: everything after the message's own header is the payload.
        Assert.Equal(vector.Frame[headerSize..].ToArray(), data.ToArray());
    }

    private static void AssertRstat(Rstat message, WireVector vector)
    {
        JsonAssertStat(message.Stat, vector.Fields.GetProperty("stat"));
        Assert.Null(message.Stat.Extension);
        Assert.Equal(Constants.NONUNAME, message.Stat.NUid);
        Assert.Equal(Constants.NONUNAME, message.Stat.NGid);
        Assert.Equal(Constants.NONUNAME, message.Stat.NMuid);
    }

    private static void AssertTwstat(Twstat message, WireVector vector)
    {
        Assert.Equal((uint)vector.Num("fid"), message.Fid);

        // This vector is the "all don't touch" wstat, which reference §4.2 defines as a request to
        // commit the file to stable storage rather than to change anything.
        Assert.True(message.Stat.IsAllDontTouch);
    }

    private static void JsonAssertStat(StatRecord stat, System.Text.Json.JsonElement expected)
    {
        Assert.Equal((ushort)WireVector.AsNumber(expected.GetProperty("type")), stat.Type);
        Assert.Equal((uint)WireVector.AsNumber(expected.GetProperty("dev")), stat.Dev);
        Assert.Equal(WireVector.QidFrom(expected.GetProperty("qid")), stat.Qid);
        Assert.Equal((uint)WireVector.AsNumber(expected.GetProperty("mode")), stat.Mode);
        Assert.Equal((uint)WireVector.AsNumber(expected.GetProperty("atime")), stat.ATime);
        Assert.Equal((uint)WireVector.AsNumber(expected.GetProperty("mtime")), stat.MTime);
        Assert.Equal(WireVector.AsNumber(expected.GetProperty("length")), stat.Length);
        Assert.Equal(expected.GetProperty("name").GetString(), stat.Name);
        Assert.Equal(expected.GetProperty("uid").GetString(), stat.Uid);
        Assert.Equal(expected.GetProperty("gid").GetString(), stat.Gid);
        Assert.Equal(expected.GetProperty("muid").GetString(), stat.Muid);
    }
}
