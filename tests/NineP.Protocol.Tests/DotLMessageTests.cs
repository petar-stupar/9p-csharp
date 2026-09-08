using System.Buffers;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>
/// The 9P2000.L half of the golden vectors of reference §9, decoded field by field and re-encoded.
/// The short <c>Tfsync</c> form is not here: it is the one legal short frame and task 11 delivers
/// it, so this suite covers the 15-byte form the encoder always writes.
/// </summary>
public sealed class DotLMessageTests
{
    private const string ShortFsync = "Tfsync (no datasync)";

    /// <summary>The vectors a 9P2000.L session would carry, one test case each.</summary>
    /// <returns>One row per vector.</returns>
    public static TheoryData<string, int> DotLVectors()
    {
        TheoryData<string, int> data = [];
        for (int i = 0; i < WireVectors.All.Count; i++)
        {
            WireVector vector = WireVectors.All[i];
            if (vector.Dialect == Dialect.P9_2000_L && vector.Name != ShortFsync)
            {
                data.Add(vector.Name, i);
            }
        }

        return data;
    }

    /// <summary>Every .L vector decodes, and every field it records comes back unchanged.</summary>
    /// <param name="name">The vector's name, so a failure says which one broke.</param>
    /// <param name="index">The vector's position in the fixture.</param>
    [Theory]
    [MemberData(nameof(DotLVectors))]
    public void EveryDotLVectorDecodesToItsFields(string name, int index)
    {
        WireVector vector = WireVectors.All[index];

        Assert.Equal((uint)vector.Size, MessageCodec.PeekSize(vector.Frame.Span));
        Assert.Equal(vector.Type, MessageCodec.PeekType(vector.Frame.Span));
        Assert.Equal((ushort)vector.Num("tag"), MessageCodec.PeekTag(vector.Frame.Span));

        AssertFields(name, vector);
    }

    /// <summary>Every .L vector re-encodes to exactly the bytes it came from.</summary>
    /// <param name="name">The vector's name, so a failure says which one broke.</param>
    /// <param name="index">The vector's position in the fixture.</param>
    [Theory]
    [MemberData(nameof(DotLVectors))]
    public void EveryDotLVectorReEncodesByteIdentically(string name, int index)
    {
        WireVector vector = WireVectors.All[index];

        Assert.Equal(vector.ToBytes(), ReEncode(name, vector));
    }

    /// <summary>
    /// The fixture covers every type that only a 9P2000.L session can carry. The types the
    /// dialects share are covered once, under 9P2000, which is why they are excluded here.
    /// </summary>
    [Fact]
    public void FixtureCoversEveryDotLOnlyType()
    {
        HashSet<MessageType> covered = [.. WireVectors.All
            .Where(v => v.Dialect == Dialect.P9_2000_L)
            .Select(v => v.Type)];

        foreach (MessageType type in Enum.GetValues<MessageType>())
        {
            if (MessageTypes.IsLegal(type, Dialect.P9_2000_L) && !MessageTypes.IsLegal(type, Dialect.P9_2000))
            {
                Assert.Contains(type, covered);
            }
        }
    }

    /// <summary>A .L-only message is refused on the type check in a 9P2000 session.</summary>
    [Fact]
    public void ADotLOnlyMessageIsIllegalInBase()
    {
        WireVector getattr = WireVectors.All.First(v => v.Type == MessageType.Tgetattr);

        Assert.False(MessageCodec.TryDecode(
            getattr.Frame, Dialect.P9_2000, out Tgetattr _, out ProtocolErrorKind failure));
        Assert.Equal(ProtocolErrorKind.Type, failure);
    }

    /// <summary>A lock whose start plus length wraps past 2^64 is rejected (reference §8 rule 5).</summary>
    [Fact]
    public void LockRangeOverflowIsRejected()
    {
        WireVector vector = WireVectors.All.First(v => v.Type == MessageType.Tlock);
        Tlock message = Decode<Tlock>(vector);

        Tlock wrapped = message with
        {
            Request = message.Request with { Start = ulong.MaxValue, Length = 2 },
        };

        ArrayBufferWriter<byte> buffer = new();
        MessageCodec.Encode(buffer, in wrapped, Dialect.P9_2000_L);

        Assert.False(MessageCodec.TryDecode(
            buffer.WrittenMemory, Dialect.P9_2000_L, out Tlock _, out ProtocolErrorKind failure));
        Assert.Equal(ProtocolErrorKind.Overflow, failure);
    }

    private static byte[] ReEncode(string name, WireVector vector) => vector.Type switch
    {
        MessageType.Rlerror => RoundTrip<Rlerror>(vector),
        MessageType.Tattach => RoundTrip<Tattach>(vector),
        MessageType.Tversion => RoundTrip<Tversion>(vector),
        MessageType.Tstatfs => RoundTrip<Tstatfs>(vector),
        MessageType.Rstatfs => RoundTrip<Rstatfs>(vector),
        MessageType.Tlopen => RoundTrip<Tlopen>(vector),
        MessageType.Rlopen => RoundTrip<Rlopen>(vector),
        MessageType.Tlcreate => RoundTrip<Tlcreate>(vector),
        MessageType.Rlcreate => RoundTrip<Rlcreate>(vector),
        MessageType.Tsymlink => RoundTrip<Tsymlink>(vector),
        MessageType.Rsymlink => RoundTrip<Rsymlink>(vector),
        MessageType.Tmknod => RoundTrip<Tmknod>(vector),
        MessageType.Rmknod => RoundTrip<Rmknod>(vector),
        MessageType.Trename => RoundTrip<Trename>(vector),
        MessageType.Rrename => RoundTrip<Rrename>(vector),
        MessageType.Treadlink => RoundTrip<Treadlink>(vector),
        MessageType.Rreadlink => RoundTrip<Rreadlink>(vector),
        MessageType.Tgetattr => RoundTrip<Tgetattr>(vector),
        MessageType.Rgetattr => RoundTrip<Rgetattr>(vector),
        MessageType.Tsetattr => RoundTrip<Tsetattr>(vector),
        MessageType.Rsetattr => RoundTrip<Rsetattr>(vector),
        MessageType.Txattrwalk => RoundTrip<Txattrwalk>(vector),
        MessageType.Rxattrwalk => RoundTrip<Rxattrwalk>(vector),
        MessageType.Txattrcreate => RoundTrip<Txattrcreate>(vector),
        MessageType.Rxattrcreate => RoundTrip<Rxattrcreate>(vector),
        MessageType.Treaddir => RoundTrip<Treaddir>(vector),
        MessageType.Rreaddir => RoundTrip<Rreaddir>(vector),
        MessageType.Tfsync => RoundTrip<Tfsync>(vector),
        MessageType.Rfsync => RoundTrip<Rfsync>(vector),
        MessageType.Tlock => RoundTrip<Tlock>(vector),
        MessageType.Rlock => RoundTrip<Rlock>(vector),
        MessageType.Tgetlock => RoundTrip<Tgetlock>(vector),
        MessageType.Rgetlock => RoundTrip<Rgetlock>(vector),
        MessageType.Tlink => RoundTrip<Tlink>(vector),
        MessageType.Rlink => RoundTrip<Rlink>(vector),
        MessageType.Tmkdir => RoundTrip<Tmkdir>(vector),
        MessageType.Rmkdir => RoundTrip<Rmkdir>(vector),
        MessageType.Trenameat => RoundTrip<Trenameat>(vector),
        MessageType.Rrenameat => RoundTrip<Rrenameat>(vector),
        MessageType.Tunlinkat => RoundTrip<Tunlinkat>(vector),
        MessageType.Runlinkat => RoundTrip<Runlinkat>(vector),
        _ => throw new InvalidOperationException("vector " + name + " has no round trip"),
    };

    private static byte[] RoundTrip<TMessage>(WireVector vector)
        where TMessage : struct, IMessage
    {
        TMessage message = Decode<TMessage>(vector);

        ArrayBufferWriter<byte> buffer = new();
        int written = MessageCodec.Encode(buffer, in message, Dialect.P9_2000_L);

        Assert.Equal(vector.Size, MessageCodec.GetEncodedSize(in message, Dialect.P9_2000_L));
        Assert.Equal(vector.Size, written);
        return buffer.WrittenSpan.ToArray();
    }

    private static TMessage Decode<TMessage>(WireVector vector)
        where TMessage : struct, IMessage =>
        MessageCodec.Decode<TMessage>(vector.Frame, Dialect.P9_2000_L);

    private static void AssertFields(string name, WireVector vector)
    {
        if (AssertSessionFields(vector) || AssertOpenFields(vector) || AssertPathFields(vector)
            || AssertAttrFields(vector) || AssertIoFields(vector))
        {
            return;
        }

        Assert.Fail($"vector {name} has no field assertions");
    }

    private static bool AssertSessionFields(WireVector vector)
    {
        switch (vector.Type)
        {
            case MessageType.Tversion:
                Assert.Equal(vector.Str("version"), Decode<Tversion>(vector).Version);
                return true;
            case MessageType.Tattach:
                Assert.Equal((uint)vector.Num("n_uname"), Decode<Tattach>(vector).NUname);
                return true;
            case MessageType.Rlerror:
                Assert.Equal((int)vector.Num("ecode"), Decode<Rlerror>(vector).Ecode);
                return true;
            case MessageType.Tstatfs:
                Assert.Equal((uint)vector.Num("fid"), Decode<Tstatfs>(vector).Fid);
                return true;
            case MessageType.Rstatfs:
                AssertStatFs(Decode<Rstatfs>(vector).Stat, vector);
                return true;
            default:
                return false;
        }
    }

    private static bool AssertOpenFields(WireVector vector)
    {
        switch (vector.Type)
        {
            case MessageType.Tlopen:
                {
                    Tlopen message = Decode<Tlopen>(vector);
                    Assert.Equal((uint)vector.Num("fid"), message.Fid);
                    Assert.Equal((uint)vector.Num("flags"), message.Flags);
                    return true;
                }

            case MessageType.Rlopen:
                {
                    Rlopen message = Decode<Rlopen>(vector);
                    Assert.Equal(vector.QidOf("qid"), message.Qid);
                    Assert.Equal((uint)vector.Num("iounit"), message.Iounit);
                    return true;
                }

            case MessageType.Tlcreate:
                {
                    Tlcreate message = Decode<Tlcreate>(vector);
                    Assert.Equal(vector.Str("name"), message.Name);
                    Assert.Equal((uint)vector.Num("flags"), message.Flags);
                    Assert.Equal((uint)vector.Num("mode"), message.Mode);
                    Assert.Equal((uint)vector.Num("gid"), message.Gid);
                    return true;
                }

            case MessageType.Rlcreate:
                Assert.Equal(vector.QidOf("qid"), Decode<Rlcreate>(vector).Qid);
                return true;
            case MessageType.Tsymlink:
                {
                    Tsymlink message = Decode<Tsymlink>(vector);
                    Assert.Equal(vector.Str("name"), message.Name);
                    Assert.Equal(vector.Str("symtgt"), message.Symtgt);
                    Assert.Equal((uint)vector.Num("gid"), message.Gid);
                    return true;
                }

            case MessageType.Rsymlink:
                Assert.Equal(vector.QidOf("qid"), Decode<Rsymlink>(vector).Qid);
                return true;
            case MessageType.Tmknod:
                {
                    Tmknod message = Decode<Tmknod>(vector);
                    Assert.Equal(vector.Str("name"), message.Name);
                    Assert.Equal((uint)vector.Num("mode"), message.Mode);
                    Assert.Equal((uint)vector.Num("major"), message.Major);
                    Assert.Equal((uint)vector.Num("minor"), message.Minor);
                    return true;
                }

            case MessageType.Rmknod:
                Assert.Equal(vector.QidOf("qid"), Decode<Rmknod>(vector).Qid);
                return true;
            default:
                return false;
        }
    }

    private static bool AssertPathFields(WireVector vector)
    {
        switch (vector.Type)
        {
            case MessageType.Trename:
                {
                    Trename message = Decode<Trename>(vector);
                    Assert.Equal((uint)vector.Num("dfid"), message.Dfid);
                    Assert.Equal(vector.Str("name"), message.Name);
                    return true;
                }

            case MessageType.Treadlink:
                Assert.Equal((uint)vector.Num("fid"), Decode<Treadlink>(vector).Fid);
                return true;
            case MessageType.Rreadlink:
                Assert.Equal(vector.Str("target"), Decode<Rreadlink>(vector).Target);
                return true;
            case MessageType.Tlink:
                {
                    Tlink message = Decode<Tlink>(vector);
                    Assert.Equal((uint)vector.Num("dfid"), message.Dfid);
                    Assert.Equal(vector.Str("name"), message.Name);
                    return true;
                }

            case MessageType.Tmkdir:
                {
                    Tmkdir message = Decode<Tmkdir>(vector);
                    Assert.Equal(vector.Str("name"), message.Name);
                    Assert.Equal((uint)vector.Num("mode"), message.Mode);
                    Assert.Equal((uint)vector.Num("gid"), message.Gid);
                    return true;
                }

            case MessageType.Rmkdir:
                Assert.Equal(vector.QidOf("qid"), Decode<Rmkdir>(vector).Qid);
                return true;
            case MessageType.Trenameat:
                {
                    Trenameat message = Decode<Trenameat>(vector);
                    Assert.Equal(vector.Str("oldname"), message.OldName);
                    Assert.Equal(vector.Str("newname"), message.NewName);
                    return true;
                }

            case MessageType.Tunlinkat:
                {
                    Tunlinkat message = Decode<Tunlinkat>(vector);
                    Assert.Equal((uint)vector.Num("dirfd"), message.DirFid);
                    Assert.Equal(vector.Str("name"), message.Name);
                    Assert.Equal((uint)vector.Num("flags"), message.Flags);
                    return true;
                }

            case MessageType.Rrename:
            case MessageType.Rlink:
            case MessageType.Rrenameat:
            case MessageType.Runlinkat:
                Assert.Equal((ushort)vector.Num("tag"), TagOfEmptyReply(vector));
                return true;
            default:
                return false;
        }
    }

    private static bool AssertAttrFields(WireVector vector)
    {
        switch (vector.Type)
        {
            case MessageType.Tgetattr:
                Assert.Equal(
                    (GetAttrMask)vector.Num("request_mask"), Decode<Tgetattr>(vector).RequestMask);
                return true;
            case MessageType.Rgetattr:
                AssertRgetattr(Decode<Rgetattr>(vector), vector);
                return true;
            case MessageType.Tsetattr:
                AssertTsetattr(Decode<Tsetattr>(vector), vector);
                return true;
            case MessageType.Txattrwalk:
                {
                    Txattrwalk message = Decode<Txattrwalk>(vector);
                    Assert.Equal((uint)vector.Num("newfid"), message.NewFid);
                    Assert.Equal(vector.Str("name"), message.Name);
                    return true;
                }

            case MessageType.Rxattrwalk:
                Assert.Equal(vector.Num("size"), Decode<Rxattrwalk>(vector).Size);
                return true;
            case MessageType.Txattrcreate:
                {
                    Txattrcreate message = Decode<Txattrcreate>(vector);
                    Assert.Equal(vector.Str("name"), message.Name);
                    Assert.Equal(vector.Num("attr_size"), message.AttrSize);
                    Assert.Equal((XattrFlags)vector.Num("flags"), message.Flags);
                    return true;
                }

            case MessageType.Rsetattr:
            case MessageType.Rxattrcreate:
                Assert.Equal((ushort)vector.Num("tag"), TagOfEmptyReply(vector));
                return true;
            default:
                return false;
        }
    }

    private static bool AssertIoFields(WireVector vector)
    {
        switch (vector.Type)
        {
            case MessageType.Treaddir:
                {
                    Treaddir message = Decode<Treaddir>(vector);
                    Assert.Equal(vector.Num("offset"), message.Offset);
                    Assert.Equal((uint)vector.Num("count"), message.Count);
                    return true;
                }

            case MessageType.Rreaddir:
                AssertRreaddir(Decode<Rreaddir>(vector), vector);
                return true;
            case MessageType.Tfsync:
                {
                    Tfsync message = Decode<Tfsync>(vector);
                    Assert.Equal((uint)vector.Num("fid"), message.Fid);
                    Assert.Equal((uint)vector.Num("datasync"), message.Datasync);
                    return true;
                }

            case MessageType.Tlock:
                AssertTlock(Decode<Tlock>(vector), vector);
                return true;
            case MessageType.Rlock:
                Assert.Equal((LockStatus)vector.Num("status"), Decode<Rlock>(vector).Status);
                return true;
            case MessageType.Tgetlock:
                AssertTgetlock(Decode<Tgetlock>(vector), vector);
                return true;
            case MessageType.Rgetlock:
                AssertRgetlock(Decode<Rgetlock>(vector).Result, vector);
                return true;
            case MessageType.Rfsync:
                Assert.Equal((ushort)vector.Num("tag"), TagOfEmptyReply(vector));
                return true;
            default:
                return false;
        }
    }

    private static ushort TagOfEmptyReply(WireVector vector) => vector.Type switch
    {
        MessageType.Rrename => Decode<Rrename>(vector).Tag,
        MessageType.Rlink => Decode<Rlink>(vector).Tag,
        MessageType.Rrenameat => Decode<Rrenameat>(vector).Tag,
        MessageType.Runlinkat => Decode<Runlinkat>(vector).Tag,
        MessageType.Rsetattr => Decode<Rsetattr>(vector).Tag,
        MessageType.Rxattrcreate => Decode<Rxattrcreate>(vector).Tag,
        MessageType.Rfsync => Decode<Rfsync>(vector).Tag,
        _ => throw new InvalidOperationException("not an empty reply: " + vector.Name),
    };

    private static void AssertStatFs(StatFs stat, WireVector vector)
    {
        Assert.Equal((uint)vector.Num("type"), stat.Type);
        Assert.Equal((uint)vector.Num("bsize"), stat.BlockSize);
        Assert.Equal(vector.Num("blocks"), stat.Blocks);
        Assert.Equal(vector.Num("bfree"), stat.BlocksFree);
        Assert.Equal(vector.Num("bavail"), stat.BlocksAvailable);
        Assert.Equal(vector.Num("files"), stat.Files);
        Assert.Equal(vector.Num("ffree"), stat.FilesFree);
        Assert.Equal(vector.Num("fsid"), stat.FsId);
        Assert.Equal((uint)vector.Num("namelen"), stat.NameLength);
    }

    private static void AssertRgetattr(Rgetattr message, WireVector vector)
    {
        Assert.Equal((GetAttrMask)vector.Num("valid"), message.Valid);
        Assert.Equal(vector.QidOf("qid"), message.Qid);
        Assert.Equal((uint)vector.Num("mode"), message.Mode);
        Assert.Equal((uint)vector.Num("uid"), message.Uid);
        Assert.Equal((uint)vector.Num("gid"), message.Gid);
        Assert.Equal(vector.Num("nlink"), message.NLink);
        Assert.Equal(vector.Num("rdev"), message.Rdev);
        Assert.Equal(vector.Num("size"), message.Size);
        Assert.Equal(vector.Num("blksize"), message.BlkSize);
        Assert.Equal(vector.Num("blocks"), message.Blocks);
        AssertTime(vector, "atime", message.ATime);
        AssertTime(vector, "mtime", message.MTime);
        AssertTime(vector, "ctime", message.CTime);
        AssertTime(vector, "btime", message.BTime);
        Assert.Equal(vector.Num("gen"), message.Gen);
        Assert.Equal(vector.Num("data_version"), message.DataVersion);
    }

    private static void AssertTsetattr(Tsetattr message, WireVector vector)
    {
        Assert.Equal((SetAttrMask)vector.Num("valid"), message.Valid);
        Assert.Equal((uint)vector.Num("mode"), message.Mode);
        Assert.Equal((uint)vector.Num("uid"), message.Uid);
        Assert.Equal((uint)vector.Num("gid"), message.Gid);
        Assert.Equal(vector.Num("size"), message.Size);
        AssertTime(vector, "atime", message.ATime);
        AssertTime(vector, "mtime", message.MTime);
    }

    private static void AssertTime(WireVector vector, string prefix, TimeSpec actual)
    {
        Assert.Equal((long)vector.Num(prefix + "_sec"), actual.Seconds);
        Assert.Equal((uint)vector.Num(prefix + "_nsec"), actual.Nanoseconds);
    }

    private static void AssertTlock(Tlock message, WireVector vector)
    {
        Assert.Equal((uint)vector.Num("fid"), message.Fid);
        Assert.Equal((LockType)vector.Num("type"), message.Request.Type);
        Assert.Equal((LockFlags)vector.Num("flags"), message.Request.Flags);
        Assert.Equal(vector.Num("start"), message.Request.Start);
        Assert.Equal(vector.Num("length"), message.Request.Length);
        Assert.Equal((uint)vector.Num("proc_id"), message.Request.ProcId);
        Assert.Equal(vector.Str("client_id"), message.Request.ClientId);
    }

    private static void AssertTgetlock(Tgetlock message, WireVector vector)
    {
        Assert.Equal((uint)vector.Num("fid"), message.Fid);
        Assert.Equal((LockType)vector.Num("type"), message.Type);
        Assert.Equal(vector.Num("start"), message.Start);
        Assert.Equal(vector.Num("length"), message.Length);
        Assert.Equal((uint)vector.Num("proc_id"), message.ProcId);
        Assert.Equal(vector.Str("client_id"), message.ClientId);
    }

    private static void AssertRgetlock(LockQueryResult result, WireVector vector)
    {
        Assert.Equal((LockType)vector.Num("type"), result.Type);
        Assert.Equal(vector.Num("start"), result.Start);
        Assert.Equal(vector.Num("length"), result.Length);
        Assert.Equal((uint)vector.Num("proc_id"), result.ProcId);
        Assert.Equal(vector.Str("client_id"), result.ClientId);
    }

    private static void AssertRreaddir(Rreaddir message, WireVector vector)
    {
        Assert.True(DirEntryCodec.TryReadAll(
            message.Data, out IReadOnlyList<DirEntry> entries, out ProtocolErrorKind failure));
        Assert.Equal(default, failure);

        System.Text.Json.JsonElement expected = vector.Fields.GetProperty("entries");
        Assert.Equal(expected.GetArrayLength(), entries.Count);

        int index = 0;
        foreach (System.Text.Json.JsonElement element in expected.EnumerateArray())
        {
            DirEntry entry = entries[index++];
            Assert.Equal(WireVector.QidFrom(element.GetProperty("qid")), entry.Qid);
            Assert.Equal(WireVector.AsNumber(element.GetProperty("offset")), entry.Cursor);
            Assert.Equal((byte)WireVector.AsNumber(element.GetProperty("type")), entry.DirentType);
            Assert.Equal(element.GetProperty("name").GetString(), entry.Name);
        }
    }
}
