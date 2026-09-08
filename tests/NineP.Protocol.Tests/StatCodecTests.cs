using System.Buffers.Binary;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>The stat record's double size (reference §3.3 and stat(5) BUGS).</summary>
public sealed class StatCodecTests
{
    /// <summary>
    /// A stat record carries its length twice: once as the stat[n] count and once as the record's
    /// own leading size[2], which must be n - 2. A frame where the two disagree is malformed, and
    /// it is refused as a Stat failure rather than read as a shorter or longer record.
    /// </summary>
    [Fact]
    public void InnerSizeEqualsOuterMinusTwo()
    {
        WireVector vector = WireVectors.All.First(v => v.Type == MessageType.Rstat && v.Dialect == Dialect.P9_2000);

        ushort outer = BinaryPrimitives.ReadUInt16LittleEndian(vector.Frame.Span.Slice(7, 2));
        ushort inner = BinaryPrimitives.ReadUInt16LittleEndian(vector.Frame.Span.Slice(9, 2));
        Assert.Equal(outer - 2, inner);

        Rstat clean = MessageCodec.Decode<Rstat>(vector.Frame, Dialect.P9_2000);
        Assert.Equal("label", clean.Stat.Name);

        foreach (int delta in new[] { -1, 1 })
        {
            byte[] mutated = vector.ToBytes();
            BinaryPrimitives.WriteUInt16LittleEndian(mutated.AsSpan(9, 2), (ushort)(inner + delta));

            Assert.False(MessageCodec.TryDecode(mutated, Dialect.P9_2000, out Rstat _, out ProtocolErrorKind failure));
            Assert.Equal(ProtocolErrorKind.Stat, failure);
        }
    }

    /// <summary>
    /// A record whose fields sum past 65535 bytes is refused rather than written with a wrapped
    /// length. The build has no <c>CheckForOverflowUnderflow</c>, so the unchecked cast used to
    /// truncate <c>n[2]</c> and the record's own <c>size[2]</c> while the frame's outer
    /// <c>size[4]</c> stayed correct - a frame every peer rejects, produced silently.
    /// <c>WireWriter.WriteString</c> catches a single over-long string; nothing caught the total.
    /// </summary>
    [Fact]
    public void ARecordTooLongForItsOwnLengthFieldIsRefused()
    {
        StatRecord huge = new()
        {
            Name = "label",
            Uid = new string('u', 30000),
            Gid = new string('g', 30000),
            Muid = new string('m', 30000),
        };

        Assert.True(huge.GetEncodedSize(Dialect.P9_2000) > ushort.MaxValue);

        byte[] buffer = new byte[128];

        NinePException outer = Assert.Throws<NinePException>(() =>
        {
            WireWriter writer = new(buffer);
            StatCodec.Write(ref writer, in huge, Dialect.P9_2000);
        });

        NinePException bare = Assert.Throws<NinePException>(() =>
        {
            WireWriter writer = new(buffer);
            StatCodec.WriteRecord(ref writer, in huge, Dialect.P9_2000);
        });

        Assert.Equal(Errno.EOVERFLOW, outer.Error.Errno);
        Assert.Equal(Errno.EOVERFLOW, bare.Error.Errno);
    }

    /// <summary>
    /// A stat record whose fields stop short of the outer count leaves bytes inside the record,
    /// which is a Stat failure and not a Trailing one: the record itself is the thing that lies.
    /// </summary>
    [Fact]
    public void RecordShorterThanItsOuterCountIsRejected()
    {
        WireVector vector = WireVectors.All.First(v => v.Type == MessageType.Rstat && v.Dialect == Dialect.P9_2000);

        byte[] mutated = [.. vector.ToBytes(), 0];
        BinaryPrimitives.WriteUInt32LittleEndian(mutated, (uint)mutated.Length);
        ushort outer = BinaryPrimitives.ReadUInt16LittleEndian(mutated.AsSpan(7, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(mutated.AsSpan(7, 2), (ushort)(outer + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(mutated.AsSpan(9, 2), (ushort)(outer - 1));

        Assert.False(MessageCodec.TryDecode(mutated, Dialect.P9_2000, out Rstat _, out ProtocolErrorKind failure));
        Assert.Equal(ProtocolErrorKind.Stat, failure);
    }

    /// <summary>
    /// The all-don't-touch Twstat of the fixture decodes to a record that reports itself as an
    /// fsync request, which is what reference §4.2 says that shape means.
    /// </summary>
    [Fact]
    public void AllDontTouchWstatDecodesAsAnFsyncRequest()
    {
        WireVector vector = WireVectors.All.First(v => v.Type == MessageType.Twstat && v.Dialect == Dialect.P9_2000);

        Twstat message = MessageCodec.Decode<Twstat>(vector.Frame, Dialect.P9_2000);

        Assert.True(message.Stat.IsAllDontTouch);
        Assert.Equal(StatRecord.DontTouch with { }, message.Stat);
    }
}
