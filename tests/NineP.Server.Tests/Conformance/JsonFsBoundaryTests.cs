using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
#if NET10_0_OR_GREATER
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NineP.Client;
using NineP.JsonFs;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

[Trait("Category", "Conformance")]
public sealed class JsonFsBoundaryTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);
    public static TheoryData<Dialect> Dialects => BoundaryTests.Dialects;

    [Theory, MemberData(nameof(Dialects))]
    public async Task F8_MutationBetweenPagesDoesNotSkipUntouchedEntries(Dialect dialect)
    {
        const int Count = 5000;
        string document = "{\"d\":{" + string.Join(',', Enumerable.Range(0, Count).Select(i => $"\"e{i:D7}\":\"x\"")) + "},\"gone\":{\"leaf\":\"x\"}}";
        JsonTree tree = Parse(document);
        await using ServerHarness h = await ServerHarness.StartAsync(filesystem: new JsonFilesystem(tree, writable: true));
        await using NinePSession a = await h.ConnectAsync(dialect, o => o with { Msize = 4096 });
        await using NinePSession b = await h.ConnectAsync(dialect);
        await using NinePFid directory = await a.OpenFileAsync("d", OpenMode.Read, OpenFlags.None, Ct);
        bool[] seen = new bool[Count];
        ulong offset = 0;
        int pages = 0;
        while (true)
        {
            byte[] bytes = await BoundaryTests.DirectoryPage(a, directory, offset, 4096 - Constants.IOHDRSZ);
            if (bytes.Length == 0)
            {
                break;
            }

            pages++;
            Assert.True(pages < 2000, "directory did not terminate");
            IReadOnlyList<DirEntry> entries = Decode(bytes, dialect);
            Assert.NotEmpty(entries);
            foreach (DirEntry entry in entries)
            {
                int index = int.Parse(entry.Name.AsSpan(1), CultureInfo.InvariantCulture);
                Assert.False(seen[index], "duplicate " + entry.Name);
                seen[index] = true;
            }
            offset = dialect == Dialect.P9_2000_L ? entries[^1].Cursor : offset + (ulong)bytes.Length;
            if (pages <= 32)
            {
                await b.MkdirAsync("d/temp", 0x1ED, Ct);
                await b.RemoveAsync("d/temp", Ct);
                await b.RemoveAsync("d/e" + (pages - 1).ToString("D7", CultureInfo.InvariantCulture), Ct);
            }
        }
        Assert.True(seen.Skip(32).All(value => value));

        await using NinePFid gone = await a.OpenFileAsync("gone", OpenMode.Read, OpenFlags.None, Ct);
        byte[] first = await BoundaryTests.DirectoryPage(a, gone, 0, 1000);
        Assert.NotEmpty(first);
        ulong resume = dialect == Dialect.P9_2000_L ? Decode(first, dialect)[^1].Cursor : (ulong)first.Length;
        await b.RemoveAsync("gone/leaf", Ct);
        await b.RemoveAsync("gone", Ct);
        Assert.Empty(await BoundaryTests.DirectoryPage(a, gone, resume, 1000));
        await gone.DisposeAsync();
        Assert.Equal(FileKind.Directory, (await a.GetAttrAsync("/", Ct)).Kind);
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F12c_CapRejectsCreateWriteAndRenameWithoutChangingOpenObjects(Dialect dialect)
    {
        JsonTree tree = Parse("""{"a":"ok","keep":"safe"}""");
        byte[] before = Serialize(tree);
        tree.DocumentLimit = before.Length + 1;
        JsonFilesystem fs = new(tree, writable: true);
        await using ServerHarness h = await ServerHarness.StartAsync(filesystem: fs);
        await using NinePSession s = await h.ConnectAsync(dialect);
        await using NinePFid open = await s.OpenFileAsync("a", OpenMode.ReadWrite, OpenFlags.None, Ct);
        Attr original = await open.GetAttrAsync(Ct);
        await BoundaryTests.Error(Errno.ENOSPC, async () => await s.MkdirAsync("new", 0x1ED, Ct));
        await BoundaryTests.Error(Errno.ENOSPC, async () => await open.WriteAsync(0, "big"u8.ToArray(), Ct));
        await BoundaryTests.Error(Errno.ENOSPC, async () => await s.RenameAsync("a", "longer", Ct));
        // Quotes expand on serialization: a two-byte value can cost more than the old one.
        await BoundaryTests.Error(Errno.ENOSPC, async () => await open.WriteAsync(0, "\"\""u8.ToArray(), Ct));
        Assert.Equal(before, Serialize(tree));
        Assert.Equal(original.Qid, (await open.GetAttrAsync(Ct)).Qid);
        Assert.Equal("ok", Encoding.UTF8.GetString(await open.ReadAllAsync(Ct)));
        await s.RemoveAsync("keep", Ct);
        await s.MkdirAsync("new", 0x1ED, Ct);
        Assert.Contains(await s.ReadDirAsync("/", Ct), e => e.Name == "new");
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F12c_ExactSerializedThresholdAndConcurrentGrowth(Dialect dialect)
    {
        JsonTree tree = Parse("""{"a":""}""");
        tree.DocumentLimit = Serialize(tree).Length + 3;
        await using ServerHarness h = await ServerHarness.StartAsync(filesystem: new JsonFilesystem(tree, writable: true));
        await using NinePSession a = await h.ConnectAsync(dialect);
        await using NinePSession b = await h.ConnectAsync(dialect);
        await using NinePFid first = await a.OpenFileAsync("a", OpenMode.Write, OpenFlags.None, Ct);
        await using NinePFid second = await b.OpenFileAsync("a", OpenMode.Write, OpenFlags.None, Ct);
        Assert.Equal(2, await first.WriteAsync(0, "ab"u8.ToArray(), Ct));
        await BoundaryTests.Error(Errno.ENOSPC, async () => await first.WriteAsync(2, "c"u8.ToArray(), Ct));
        Assert.Equal("ab", Encoding.UTF8.GetString(await a.ReadFileAsync("a", Ct)));
        // A separate document has exactly enough room for either create, but not both.
        JsonTree capacity = Parse("{}");
        JsonTree one = Parse("""{"x":{}}""");
        capacity.DocumentLimit = Serialize(one).Length + 1;
        await using ServerHarness limited = await ServerHarness.StartAsync(filesystem: new JsonFilesystem(capacity, writable: true));
        await using NinePSession c = await limited.ConnectAsync(dialect);
        await using NinePSession d = await limited.ConnectAsync(dialect);
        async Task<int> Create(NinePSession client, string name)
        {
            try { await client.MkdirAsync(name, 0x1ED, Ct); return 0; }
            catch (NinePException e) { return e.Error.Errno; }
        }
        int[] outcomes = await Task.WhenAll(Create(c, "x"), Create(d, "y"));
        Assert.Equal(new[] { 0, Errno.ENOSPC }, outcomes.Order().ToArray());
        Assert.Single(await c.ReadDirAsync("/", Ct));
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F28_Utf8CharactersMayCrossWriteBoundariesAndInvalidBytesAreAtomic(Dialect dialect)
    {
        foreach (string value in new[] { "é", "世", "🚀" })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            for (int split = 1; split < bytes.Length; split++)
            {
                JsonTree tree = Parse("""{"a":""}""");
                await using ServerHarness h = await ServerHarness.StartAsync(filesystem: new JsonFilesystem(tree, writable: true));
                await using NinePSession s = await h.ConnectAsync(dialect);
                await using NinePFid file = await s.OpenFileAsync("a", OpenMode.ReadWrite, OpenFlags.None, Ct);
                Assert.Equal(split, await file.WriteAsync(0, bytes.AsMemory(0, split), Ct));
                using (JsonDocument validPrefix = JsonDocument.Parse(Serialize(tree)))
                {
                    Assert.Equal(JsonValueKind.Object, validPrefix.RootElement.ValueKind);
                }
                Assert.Equal(bytes.Length - split, await file.WriteAsync((ulong)split, bytes.AsMemory(split), Ct));
                Assert.Equal(bytes, await file.ReadAllAsync(Ct));
                byte[] before = Serialize(tree);
                await BoundaryTests.Error(Errno.EINVAL, async () => await file.WriteAsync(0, new byte[] { 0xFF }, Ct));
                Assert.Equal(before, Serialize(tree));
            }
        }
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F28_WholeFilePipelineSplitsUtf8AcrossWireChunks(Dialect dialect)
    {
        JsonTree tree = Parse("{} ");
        await using ServerHarness h = await ServerHarness.StartAsync(filesystem: new JsonFilesystem(tree, writable: true));
        await using NinePSession s = await h.ConnectAsync(dialect, o => o with { Msize = 4096 });
        byte[] text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("é世🚀", 3000)));
        await using (NinePFid file = await s.CreateFileAsync("data", 0x1A4, Ct))
        {
            await file.WriteAllAsync(text, Ct);
        }
        Assert.Equal(text, await s.ReadFileAsync("data", Ct));
        Assert.Equal(1, s.LiveFids);
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F28_IncompleteUtf8AtClunkIsAnErrorAndFidIsReleased(Dialect dialect)
    {
        JsonTree tree = Parse("""{"a":""}""");
        await using ServerHarness h = await ServerHarness.StartAsync(filesystem: new JsonFilesystem(tree, writable: true));
        await using NinePSession s = await h.ConnectAsync(dialect);
        NinePFid file = await s.OpenFileAsync("a", OpenMode.Write, OpenFlags.None, Ct);
        Assert.Equal(1, await file.WriteAsync(0, new byte[] { 0xF0 }, Ct));
        await BoundaryTests.Error(Errno.EINVAL, async () => await s.Messages.ClunkAsync(new Tclunk(0, file.Fid), Ct));
        await BoundaryTests.Error(Errno.EBADF, async () => await s.Messages.ClunkAsync(new Tclunk(0, file.Fid), Ct));
        await file.DisposeAsync();
        Assert.Equal(1, s.LiveFids);
        Assert.Empty(await s.ReadFileAsync("a", Ct));
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F29_CreateAndMoveCannotExceedReloadableDepth(Dialect dialect)
    {
        JsonTree tree = Parse("""{"a":{"b":{}},"move":{"leaf":{}}}""");
        tree.DepthLimit = 3;
        byte[] before = Serialize(tree);
        await using ServerHarness h = await ServerHarness.StartAsync(filesystem: new JsonFilesystem(tree, writable: true));
        await using NinePSession s = await h.ConnectAsync(dialect);
        await BoundaryTests.Error(Errno.ENOSPC, async () => await s.MkdirAsync("a/b/c", 0x1ED, Ct));
        if (dialect == Dialect.P9_2000_L)
        {
            await BoundaryTests.Error(Errno.ENOSPC, async () => await s.RenameAsync("move", "a/b/move", Ct));
        }
        Assert.Equal(before, Serialize(tree));
        await s.MkdirAsync("a/allowed", 0x1ED, Ct);
        JsonTree reloaded = Parse(Encoding.UTF8.GetString(Serialize(tree)));
        Assert.NotNull(reloaded.Root.Find("a"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F30_WriteBackFailureReportsErrorAndLeavesACompleteDocument(bool afterReplace)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ninep-persistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "doc.json");
        try
        {
            const string Before = """{"a":"before"}""";
            await File.WriteAllTextAsync(path, Before, Ct);
            JsonTree tree = Parse(Before);
            JsonFilesystem fs = new(tree, writable: true, writeBackPath: path);
            if (afterReplace)
            {
                fs.Persistence.AfterReplace = () => throw new IOException("injected directory sync failure");
            }
            else
            {
                fs.Persistence.BeforeReplace = () => throw new IOException("injected replacement failure");
            }

            await using ServerHarness h = await ServerHarness.StartAsync(filesystem: fs);
            await using NinePSession s = await h.ConnectAsync(Dialect.P9_2000_L);
            await using NinePFid file = await s.OpenFileAsync("a", OpenMode.Write, OpenFlags.None, Ct);
            await BoundaryTests.Error(Errno.EIO, async () => await file.WriteAsync(0, "after!"u8.ToArray(), Ct));
            using JsonDocument disk = JsonDocument.Parse(await File.ReadAllTextAsync(path, Ct));
            Assert.Equal(afterReplace ? "after!" : "before", disk.RootElement.GetProperty("a").GetString());
            Assert.Equal("after!", Encoding.UTF8.GetString(await s.ReadFileAsync("a", Ct)));
            Assert.Single(Directory.GetFiles(directory));
            fs.Persistence.BeforeReplace = null;
            fs.Persistence.AfterReplace = null;
            await file.WriteAsync(0, "retry!"u8.ToArray(), Ct);
            using JsonDocument retried = JsonDocument.Parse(await File.ReadAllTextAsync(path, Ct));
            Assert.Equal("retry!", retried.RootElement.GetProperty("a").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task E17_EncodedKeysRemainDistinctAfterWriteBackAndReload()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ninep-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "doc.json");
        try
        {
            await File.WriteAllTextAsync(path, "{}", Ct);
            await using (ServerHarness h = await ServerHarness.StartAsync(filesystem: new JsonFilesystem(JsonTree.Load(path), true, path)))
            await using (NinePSession s = await h.ConnectAsync(Dialect.P9_2000_L))
            {
                foreach (string name in new[] { "%", "%25", "%2F", "%2E", "%2E%2E", "%252F" })
                {
                    await using NinePFid file = await s.CreateFileAsync(name, 0x1A4, Ct);
                    await file.WriteAsync(0, Encoding.UTF8.GetBytes(name), Ct);
                }
            }
            JsonTree loaded = JsonTree.Load(path);
            Assert.Equal(6, loaded.Root.Children.Count);
            Assert.Equal(6, loaded.Root.Children.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count());
            foreach (JsonChild child in loaded.Root.Children)
            {
                Assert.Equal(child.Name, Assert.IsType<JsonScalarNode>(child.Node).Text);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task EmptyWritesPreserveContentAndSparseWritesSurviveReload(Dialect dialect)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ninep-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "doc.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"a\":\"abc\"}", Ct);
            await using (ServerHarness h = await ServerHarness.StartAsync(filesystem: new JsonFilesystem(JsonTree.Load(path), true, path)))
            await using (NinePSession s = await h.ConnectAsync(dialect))
            await using (NinePFid f = await s.OpenFileAsync("a", OpenMode.ReadWrite, OpenFlags.None, Ct))
            {
                foreach (ulong at in new ulong[] { 0, 1, 3, 8 })
                {
                    Assert.Equal(0u, (await s.Messages.WriteAsync(new Twrite(0, f.Fid, at, ReadOnlyMemory<byte>.Empty), Ct)).Count);
                    Assert.Equal("abc"u8.ToArray(), await s.ReadFileAsync("a", Ct));
                    Assert.Equal(3ul, (await f.GetAttrAsync(Ct)).Size);
                }
                Assert.Equal(1, await f.WriteAsync(6, "z"u8.ToArray(), Ct));
                Assert.Equal(new byte[] { 97, 98, 99, 0, 0, 0, 122 }, await s.ReadFileAsync("a", Ct));
                Assert.Equal(7ul, (await f.GetAttrAsync(Ct)).Size);
            }
            await using ServerHarness reloaded = await ServerHarness.StartAsync(filesystem: new JsonFilesystem(JsonTree.Load(path), true));
            await using NinePSession reader = await reloaded.ConnectAsync(dialect);
            Assert.Equal(new byte[] { 97, 98, 99, 0, 0, 0, 122 }, await reader.ReadFileAsync("a", Ct));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonTree Parse(string text)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(text));
        return JsonTree.Parse(stream, "test.json");
    }

    private static byte[] Serialize(JsonTree tree)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            tree.Write(writer);
        }
        return stream.ToArray();
    }

    private static IReadOnlyList<DirEntry> Decode(byte[] bytes, Dialect dialect)
    {
        if (dialect == Dialect.P9_2000_L)
        {
            Assert.True(DirEntryCodec.TryReadAll(bytes, out IReadOnlyList<DirEntry> entries, out _));
            return entries;
        }
        WireReader reader = new(bytes);
        List<DirEntry> records = [];
        while (reader.Remaining > 0)
        {
            StatRecord stat = StatCodec.ReadRecord(ref reader, dialect);
            Assert.False(reader.Failed);
            records.Add(new DirEntry(stat.Name, stat.Qid, FileKind.File, 0));
        }
        return records;
    }
}
#endif
