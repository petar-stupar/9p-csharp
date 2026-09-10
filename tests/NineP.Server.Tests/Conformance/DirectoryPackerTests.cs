using System.Buffers.Binary;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Server.Internal;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// The packer itself (§6.7), driven directly so that the budget can be set to values a client
/// could never ask for: exactly one record, one byte less, and a whole page.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class DirectoryPackerTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 7: an <c>Rreaddir</c> entry is <c>qid[13] offset[8] type[1] name[s]</c> and is never
    /// split. Every payload the packer produces reads back as a whole number of records, whatever
    /// the budget was.
    /// </summary>
    [Fact]
    public async Task EntriesAreNeverSplit()
    {
        MemoryFilesystem tree = Populated();
        int whole = (await Dirents(tree, 0, 4096)).Length;

        for (int budget = whole; budget >= 32; budget--)
        {
            byte[] payload = await Dirents(tree, 0, budget);

            Assert.True(payload.Length <= budget);
            Assert.True(
                DirEntryCodec.TryReadAll(payload, out IReadOnlyList<DirEntry> entries, out _),
                "the packer produced a partial record");
            Assert.NotEmpty(entries);
        }
    }

    /// <summary>Rule 8: neither format ever carries a dot entry, however the handler lists them.</summary>
    [Fact]
    public async Task NoDotOrDotDotEntries()
    {
        MemoryFilesystem tree = Populated();
        tree.Root.Add(tree.NewFile(".", 0x1A4));
        tree.Root.Add(tree.NewFile("..", 0x1A4));

        byte[] payload = await Dirents(tree, 0, 4096);
        Assert.True(DirEntryCodec.TryReadAll(payload, out IReadOnlyList<DirEntry> entries, out _));
        Assert.DoesNotContain(entries, entry => entry.Name.Length > 0 && entry.Name.All(c => c == '.'));

        await using FidEntry fid = new(1, tree.Root, Identity.Anonymous("glenda"), string.Empty);
        byte[] records = await DirectoryPacker
            .PackStatRecordsAsync(tree.Root, fid, Dialect.P9_2000, 0, 4096, Ct);

        List<string> names = ReadNames(records, Dialect.P9_2000);
        Assert.DoesNotContain(names, name => name.Length > 0 && name.All(c => c == '.'));
        Assert.Equal(3, names.Count);
    }

    /// <summary>
    /// read(5) and reference §4.2: a 9P2000 directory read is a run of <b>bare</b> stat records,
    /// each carrying its <c>size[2]</c> exactly <b>once</b>. The doubled <c>stat[n]</c> framing of
    /// <c>Rstat</c> belongs to <c>Rstat</c> alone; writing it here is what made plan9port's
    /// <c>9p ls</c> answer "malformed directory contents" while our own client, using the same
    /// wrong reader, was perfectly happy (docs/interop.md).
    /// <b>Mutation:</b> put <c>StatCodec.Write</c> back in the packer and every assertion below
    /// fails on the first record.
    /// </summary>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    public async Task StatRecordsCarryTheirSizeOnce(Dialect dialect)
    {
        MemoryFilesystem tree = Populated();
        await using FidEntry fid = new(1, tree.Root, Identity.Anonymous("glenda"), string.Empty);

        byte[] payload = await DirectoryPacker
            .PackStatRecordsAsync(tree.Root, fid, dialect, 0, 4096, Ct);

        // Walk the run by the length each record declares; if a second length were on the wire the
        // stride would be wrong and the last record would not land exactly on the end.
        int at = 0;
        int records = 0;
        while (at < payload.Length)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(at));
            Assert.True(at + 2 + size <= payload.Length, "a stat record ran past the end of the reply");

            // The first field after the length is type[2], which our projector leaves 0 — where
            // the second length would have been, it would be the record's own size.
            Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(at + 2)));

            at += 2 + size;
            records++;
        }

        Assert.Equal(payload.Length, at);
        Assert.Equal(3, records);
        Assert.Equal(3, ReadNames(payload, dialect).Count);
    }

    /// <summary>A budget too small for even one record is <c>ERANGE</c>, never an empty reply.</summary>
    [Fact]
    public async Task ABudgetUnderOneRecordIsErange()
    {
        MemoryFilesystem tree = Populated();

        await Assert.ThrowsAsync<NinePException>(async () => await Dirents(tree, 0, 8));
    }

    private static MemoryFilesystem Populated()
    {
        MemoryFilesystem tree = new();
        tree.Root.Add(tree.NewFile("alpha", 0x1A4));
        tree.Root.Add(tree.NewFile("bravo", 0x1A4));
        tree.Root.Add(tree.NewDirectory("charlie", 0x1ED));
        return tree;
    }

    private static ValueTask<byte[]> Dirents(MemoryFilesystem tree, ulong cookie, int budget) =>
        DirectoryPacker.PackDirentsAsync(tree.Root, cookie, budget, Ct);

    private static List<string> ReadNames(byte[] payload, Dialect dialect)
    {
        List<string> names = [];
        WireReader reader = new(payload);

        while (reader.Remaining > 0)
        {
            names.Add(StatCodec.ReadRecord(ref reader, dialect).Name);
            Assert.False(reader.Failed);
        }

        return names;
    }
}
