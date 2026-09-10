using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

/// <summary>
/// The 9P2000.L directory record of reference §4.3: <c>qid[13] offset[8] type[1] name[s]</c>,
/// packed whole or not at all.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class DirEntryCodecTests
{
    private static readonly DirEntry[] Sample =
    [
        new("users", new Qid(QidType.QTDIR, 0, 1), FileKind.Directory, 1),
        new("auth", new Qid(QidType.QTFILE, 3, 42), FileKind.File, 2),
    ];

    /// <summary>The golden Rreaddir payload packs back to exactly the bytes it came from.</summary>
    [Fact]
    public void GoldenEntriesRoundTrip()
    {
        WireVector vector = WireVectors.All.First(v => v.Type == MessageType.Rreaddir);
        Rreaddir message = MessageCodec.Decode<Rreaddir>(vector.Frame, Dialect.P9_2000_L);

        Assert.True(DirEntryCodec.TryReadAll(
            message.Data, out IReadOnlyList<DirEntry> entries, out ProtocolErrorKind failure));
        Assert.Equal(default, failure);

        byte[] packed = new byte[message.Data.Length];
        int written = DirEntryCodec.Pack(packed, entries, out int count);

        Assert.Equal(entries.Count, count);
        Assert.Equal(message.Data.Length, written);
        Assert.Equal(message.Data.ToArray(), packed);
    }

    /// <summary>
    /// A budget that cannot hold the next whole record stops the packer: an entry is never split
    /// across a reply (reference §4.3).
    /// </summary>
    [Fact]
    public void EntriesAreNeverSplit()
    {
        int first = DirEntryCodec.GetEncodedSize(Sample[0]);
        int second = DirEntryCodec.GetEncodedSize(Sample[1]);

        for (int budget = first; budget < first + second; budget++)
        {
            byte[] destination = new byte[budget];
            int written = DirEntryCodec.Pack(destination, Sample, out int packed);

            Assert.Equal(1, packed);
            Assert.Equal(first, written);
        }

        byte[] whole = new byte[first + second];
        Assert.Equal(first + second, DirEntryCodec.Pack(whole, Sample, out int all));
        Assert.Equal(2, all);
    }

    /// <summary>A budget too small for even the first record packs nothing at all.</summary>
    [Fact]
    public void ABudgetBelowTheFirstRecordPacksNothing()
    {
        byte[] destination = new byte[DirEntryCodec.GetEncodedSize(Sample[0]) - 1];

        Assert.Equal(0, DirEntryCodec.Pack(destination, Sample, out int packed));
        Assert.Equal(0, packed);
    }

    /// <summary>A payload whose last record is cut short is malformed (reference §8 rule 13).</summary>
    [Fact]
    public void ASplitTrailingRecordIsRejected()
    {
        byte[] packed = new byte[DirEntryCodec.GetEncodedSize(Sample[0])];
        DirEntryCodec.Pack(packed, [Sample[0]], out _);

        Assert.False(DirEntryCodec.TryReadAll(
            packed.AsMemory(0, packed.Length - 1), out IReadOnlyList<DirEntry> entries, out ProtocolErrorKind failure));
        Assert.Empty(entries);
        Assert.Equal(ProtocolErrorKind.Bounds, failure);
    }

    /// <summary>The d_type byte and the file kind are the same fact read two ways.</summary>
    /// <param name="kind">The dialect-neutral kind.</param>
    /// <param name="direntType">The POSIX d_type byte it maps to.</param>
    [Theory]
    [InlineData(FileKind.Directory, 4)]
    [InlineData(FileKind.File, 8)]
    [InlineData(FileKind.Symlink, 10)]
    [InlineData(FileKind.Fifo, 1)]
    [InlineData(FileKind.Socket, 12)]
    [InlineData(FileKind.CharDevice, 2)]
    [InlineData(FileKind.BlockDevice, 6)]
    public void DirentTypeMirrorsFileKind(FileKind kind, byte direntType)
    {
        Assert.Equal(direntType, new DirEntry("x", default, kind, 0).DirentType);
        Assert.Equal(kind, DirEntry.KindOf(direntType));
    }

    /// <summary>
    /// A listing from a real .L server may carry "." and "..", which diod's own trace shows, so
    /// the reader accepts them even though our servers never write them.
    /// </summary>
    [Fact]
    public void DotAndDotDotAreReadable()
    {
        DirEntry[] entries =
        [
            new(".", new Qid(QidType.QTDIR, 0, 1), FileKind.Directory, 1),
            new("..", new Qid(QidType.QTDIR, 0, 2), FileKind.Directory, 2),
        ];

        byte[] packed = new byte[entries.Sum(e => DirEntryCodec.GetEncodedSize(e))];
        DirEntryCodec.Pack(packed, entries, out _);

        Assert.True(DirEntryCodec.TryReadAll(packed, out IReadOnlyList<DirEntry> read, out _));
        Assert.Equal([".", ".."], read.Select(e => e.Name));
    }
}
