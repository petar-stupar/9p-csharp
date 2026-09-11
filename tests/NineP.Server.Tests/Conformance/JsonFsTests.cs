using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
#if NET10_0_OR_GREATER
using System.Globalization;
using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using NineP.Client;
using NineP.JsonFs;
using NineP.Protocol;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using Xunit;
using NineP.TestSupport;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// The jsonfs mapping of the workspace architecture §7: what a JSON value becomes, what a key
/// becomes, what a write may do to a value, and the two documents jsonfs refuses to serve at all.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class JsonFsTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// E-10: number text is rendered through <see cref="CultureInfo.InvariantCulture"/>, so a
    /// server running under a comma-decimal locale writes the same bytes as one under a
    /// point-decimal locale. The ambient culture differs per target framework on this machine, so
    /// it is checked alongside the two that disagree about the decimal separator.
    /// </summary>
    [Fact]
    public void NumberFormattingIsInvariant()
    {
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            foreach (string name in new[] { "de-DE", "en-US", "fr-FR", original.Name })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

                Assert.Equal("2.5", Format("2.5"));
                Assert.Equal("-3", Format("-3"));
                Assert.Equal("1234567890", Format("1234567890"));
                Assert.Equal("1", Format("1"));
                Assert.Equal("0.0000015", Format("1.5e-6"));
                Assert.Equal("1e300", Format("1e300"));
                Assert.Equal("-9223372036854775808", Format("-9223372036854775808"));
                Assert.Equal("18446744073709551615", Format("18446744073709551615"));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// S-28: the key encoding must be injective, or two distinct keys would name one file. The
    /// property is checked over generated key sets rather than over chosen ones.
    /// </summary>
    [Fact]
    public void KeyEncodingIsInjective() =>
        Prop.ForAll(KeySets(), generated =>
        {
            HashSet<string> keys = new(generated, StringComparer.Ordinal);
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                string name = JsonKey.Encode(key);
                if (!names.Add(name) || !string.Equals(JsonKey.Decode(name), key, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return names.Count == keys.Count;
        }).QuickCheckThrowOnFailure();

    /// <summary>Step 4 of the encoding: the empty key is the one name that is a bare "%".</summary>
    [Fact]
    public void EmptyKeyIsPercent()
    {
        Assert.Equal("%", JsonKey.Encode(string.Empty));
        Assert.Equal(string.Empty, JsonKey.Decode("%"));

        // A literal "%" went through step 1 first, so it cannot collide with the empty key.
        Assert.Equal("%25", JsonKey.Encode("%"));
        Assert.Equal("%", JsonKey.Decode("%25"));
    }

    /// <summary>Step 3: the two names a 9P walk may never carry are escaped.</summary>
    [Fact]
    public void DotAndDotDotKeysAreEncoded()
    {
        Assert.Equal("%2E", JsonKey.Encode("."));
        Assert.Equal("%2E%2E", JsonKey.Encode(".."));
        Assert.Equal(".", JsonKey.Decode("%2E"));
        Assert.Equal("..", JsonKey.Decode("%2E%2E"));

        // The literal keys "%2E" and "a/b" stay distinct from what the escapes stand for.
        Assert.Equal("%252E", JsonKey.Encode("%2E"));
        Assert.Equal("a%2Fb", JsonKey.Encode("a/b"));
        Assert.Equal("a%252Fb", JsonKey.Encode("a%2Fb"));
        Assert.Equal("a/b", JsonKey.Decode("a%2Fb"));
        Assert.Equal("a%2Fb", JsonKey.Decode("a%252Fb"));
    }

    /// <summary>A document of 64 MiB or more is refused before any of it is mapped.</summary>
    [Fact]
    public void RefusesOversizeDocument()
    {
        string path = Path.Combine(Path.GetTempPath(), "jsonfs-oversize-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            using (FileStream stream = File.Create(path))
            {
                // A sparse file: the refusal is on the length, which is checked before the parse,
                // so the test does not have to write 64 MiB of anything.
                stream.SetLength(JsonTree.MaxDocumentBytes);
            }

            JsonFsStartupException refusal = Assert.Throws<JsonFsStartupException>(() => JsonTree.Load(path));
            Assert.Contains(JsonTree.MaxDocumentBytes.ToString(CultureInfo.InvariantCulture), refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A document nested deeper than 256 levels is refused, and the message says so.</summary>
    [Fact]
    public void RefusesDeepDocument()
    {
        string document = new string('[', JsonTree.MaxDepth + 4) + new string(']', JsonTree.MaxDepth + 4);
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(document));

        JsonFsStartupException refusal =
            Assert.Throws<JsonFsStartupException>(() => JsonTree.Parse(stream, "deep.json"));

        Assert.Contains("256", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Conformance Part B step 6: in an array only the next index may be created, so
    /// <c>/list/6</c> appends and <c>/list/9</c> is refused.
    /// </summary>
    [Fact]
    public async Task ArrayAppendOnlyAtNextIndex()
    {
        using JsonFilesystem filesystem = new(Parse("""{"list":["a","b"]}"""), writable: true);
        await using ServerHarness harness = await ServerHarness.StartAsync(filesystem: filesystem);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        await using (NinePFid appended = await session.CreateFileAsync("list/2", cancellationToken: Ct))
        {
            await appended.WriteAsync(0, "c"u8.ToArray(), Ct);
        }

        Assert.Equal("c", await ReadAsync(session, "list/2"));

        NinePException refused = await Assert.ThrowsAsync<NinePException>(
            async () => await session.CreateFileAsync("list/9", cancellationToken: Ct));
        Assert.Equal(Errno.EINVAL, refused.Error.Errno);
    }

    /// <summary>
    /// <c>--write-back</c> rewrites the document through a temp file and a rename, so a reader
    /// never sees a half-written document and the temp file does not survive the write.
    /// </summary>
    [Fact]
    public async Task WriteBackIsAtomic()
    {
        string directory = Path.Combine(Path.GetTempPath(), "jsonfs-wb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "doc.json");

        try
        {
            await File.WriteAllTextAsync(path, """{"name":"before"}""", Ct);

            JsonTree tree = JsonTree.Load(path);
            using JsonFilesystem filesystem = new(tree, writable: true, writeBackPath: path);

            await using (ServerHarness harness = await ServerHarness.StartAsync(filesystem: filesystem))
            await using (NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L))
            {
                await using NinePFid file = await session.OpenFileAsync(
                    "name", OpenMode.Write, OpenFlags.Truncate, Ct);
                await file.WriteAsync(0, "after"u8.ToArray(), Ct);
            }

            using JsonDocument written = JsonDocument.Parse(await File.ReadAllTextAsync(path, Ct));
            Assert.Equal("after", written.RootElement.GetProperty("name").GetString());
            Assert.Equal([path], Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The directory fsync after the write-back rename goes through <c>open(2)</c> /
    /// <c>fsync(2)</c> / <c>close(2)</c> on the directory itself, because .NET will not open a
    /// directory. This drives that wiring on a real directory, which must not throw, and on a
    /// missing one, which must fail loudly: a durability step that fails in silence is the very
    /// thing the step exists to prevent. <c>WriteBackIsAtomic</c> covers the same call end to end.
    /// <b>Mutation:</b> return from <c>DirectorySync.Flush</c> when <c>open(2)</c> fails and the
    /// missing-directory case below passes for the wrong reason.
    /// </summary>
    [Fact]
    public void DirectorySyncFsyncsARealDirectoryAndFailsLoudlyOnAMissingOne()
    {
        string directory = Path.Combine(Path.GetTempPath(), "jsonfs-dsync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            DirectorySync.Flush(directory);
        }
        finally
        {
            Directory.Delete(directory);
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => DirectorySync.Flush(Path.Combine(directory, "missing")));
        }
    }

    /// <summary>
    /// A written-back document is indented all the way through, numbers included. They used to be
    /// emitted with <c>Utf8JsonWriter.WriteRawValue</c>, which writes neither the newline nor the
    /// indent it owes, so a document round-tripped through <c>--write-back</c> came back with its
    /// numbers appended to the previous element's line and diffed noisily against the original.
    /// The token itself is still the one jsonfs holds, exponent and all.
    /// <b>Mutation:</b> go back to <c>WriteRawValue</c> and the line assertions below fail while
    /// the JSON stays valid — which is exactly why this went unnoticed.
    /// </summary>
    [Fact]
    public async Task WriteBackIndentsNumbers()
    {
        string directory = Path.Combine(Path.GetTempPath(), "jsonfs-fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "doc.json");

        try
        {
            await File.WriteAllTextAsync(
                path, """{"list":["zero",1,false],"big":1e300,"name":"before"}""", Ct);

            JsonTree tree = JsonTree.Load(path);
            using JsonFilesystem filesystem = new(tree, writable: true, writeBackPath: path);

            await using (ServerHarness harness = await ServerHarness.StartAsync(filesystem: filesystem))
            await using (NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L))
            {
                await using NinePFid file = await session.OpenFileAsync(
                    "name", OpenMode.Write, OpenFlags.Truncate, Ct);
                await file.WriteAsync(0, "after"u8.ToArray(), Ct);
            }

            string[] lines = (await File.ReadAllTextAsync(path, Ct))
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .ToArray();

            // Every value is on a line of its own, and a token jsonfs does not re-render — one
            // outside the range it writes as a plain decimal — is still the token it read.
            Assert.Contains("    \"zero\",", lines);
            Assert.Contains("    1,", lines);
            Assert.Contains("    false", lines);
            Assert.Contains("  \"big\": 1e300,", lines);
            Assert.DoesNotContain(lines, line => line.Contains("\",1", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Conformance Part B step 7: a write keeps the value's JSON type only when the text still
    /// parses as that type; anything else demotes the value to a string, which is documented
    /// behaviour rather than a silent reinterpretation.
    /// </summary>
    [Fact]
    public async Task ScalarTypeDemotionIsDocumented()
    {
        JsonTree document = Parse("""{"enabled":true,"count":1,"nothing":null}""");
        using JsonFilesystem filesystem = new(document, writable: true);
        await using ServerHarness harness = await ServerHarness.StartAsync(filesystem: filesystem);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        await WriteAsync(session, "enabled", "yes");
        await WriteAsync(session, "count", "42");
        await WriteAsync(session, "nothing", "something");

        Assert.Equal("yes", await ReadAsync(session, "enabled"));
        Assert.Equal("42", await ReadAsync(session, "count"));
        Assert.Equal("something", await ReadAsync(session, "nothing"));

        // The document a --write-back would produce shows the demotion for what it is: a string
        // where a boolean and a null used to be, a number that is still a number.
        Assert.Equal(JsonScalarKind.Text, KindOf(document, "enabled"));
        Assert.Equal(JsonScalarKind.Number, KindOf(document, "count"));
        Assert.Equal(JsonScalarKind.Text, KindOf(document, "nothing"));

        // "Documented" is part of the rule: a demotion a reader cannot look up is a surprise.
        string documentation = await File.ReadAllTextAsync(
            Path.Combine(RepoRoot(), "docs", "examples.md"), Ct);
        Assert.Contains(
            "keeps the value's JSON type only", documentation, StringComparison.Ordinal);
    }

    /// <summary>
    /// A write is bounded by <see cref="JsonTree.MaxScalarBytes"/> before anything is allocated.
    /// The buffer a write needs is <c>offset + count</c> long, so an unbounded offset is an
    /// unbounded allocation: a fifteen-byte <c>Twrite</c> at a one-gigabyte offset drove the
    /// server's RSS from 64 MB to 1.1 GB before it answered <c>EIO</c>. Beyond the bound the
    /// answer is <c>EFBIG</c> and the allocation never happens.
    /// </summary>
    /// <param name="offset">The offset the client writes at.</param>
    /// <returns>A task that completes when the refusal has been observed.</returns>
    [Theory]
    [InlineData(1UL << 30)]
    [InlineData((ulong)JsonTree.MaxScalarBytes)]
    [InlineData(ulong.MaxValue - 8)]
    public async Task AWriteBeyondTheScalarBoundIsRefusedBeforeItAllocates(ulong offset)
    {
        using JsonFilesystem filesystem = new(Parse("""{"greeting":"hello"}"""), writable: true);
        await using ServerHarness harness = await ServerHarness.StartAsync(filesystem: filesystem);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        await using NinePFid file = await session.OpenFileAsync(
            "greeting", OpenMode.Write, OpenFlags.None, Ct);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await file.WriteAsync(offset, "x"u8.ToArray(), Ct));

        Assert.Equal(Errno.EFBIG, refusal.Error.Errno);

        // The value is untouched, and the server is still serving.
        Assert.Equal("hello", await ReadAsync(session, "greeting"));
    }

    /// <summary>
    /// A container cannot be moved into itself or into anything under it. Accepting it produced a
    /// node no walk from the root could reach, and a <c>--write-back</c> document then lost the
    /// whole moved subtree, because serialisation walks only what the root still reaches.
    /// </summary>
    /// <param name="destination">Where the test tries to move <c>/dir</c>.</param>
    /// <returns>A task that completes when the refusal has been observed.</returns>
    [Theory]
    [InlineData("/dir/self")]
    [InlineData("/dir/sub/self")]
    public async Task AMoveIntoOwnSubtreeIsRefused(string destination)
    {
        JsonTree document = Parse("""{"dir":{"sub":{"leaf":"kept"}},"other":"kept"}""");
        using JsonFilesystem filesystem = new(document, writable: true);
        await using ServerHarness harness = await ServerHarness.StartAsync(filesystem: filesystem);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await session.RenameAsync("/dir", destination, Ct));

        Assert.Equal(Errno.EINVAL, refusal.Error.Errno);

        // Nothing moved, and the subtree is still reachable from the root.
        Assert.Equal(
            ["dir", "other"],
            (await session.ReadDirAsync("/", Ct)).Select(entry => entry.Name).Order(StringComparer.Ordinal));
        Assert.Equal("kept", await ReadAsync(session, "dir/sub/leaf"));
    }

    /// <summary>
    /// Write-back keeps the document's text as text. The default <c>Utf8JsonWriter</c> encoder
    /// re-encodes every non-ASCII rune as <c>\uXXXX</c>, so a hand-written file came back with
    /// its accents, dashes and CJK turned into escapes: valid JSON, and a diff against the source
    /// for nothing. A rune above U+FFFF is still escaped, because the relaxed encoder's allowed
    /// set is the BMP; that half is asserted here too so it is a documented limit rather than a
    /// surprise.
    /// </summary>
    [Fact]
    public async Task WriteBackKeepsNonAsciiAsItself()
    {
        string directory = Path.Combine(Path.GetTempPath(), "jsonfs-utf8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "doc.json");

        try
        {
            await File.WriteAllTextAsync(
                path, """{"unicode":"héllo — 世界 🚀","name":"before"}""", Ct);

            JsonTree tree = JsonTree.Load(path);
            using JsonFilesystem filesystem = new(tree, writable: true, writeBackPath: path);

            await using (ServerHarness harness = await ServerHarness.StartAsync(filesystem: filesystem))
            await using (NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L))
            {
                await using NinePFid file = await session.OpenFileAsync(
                    "name", OpenMode.Write, OpenFlags.Truncate, Ct);
                await file.WriteAsync(0, "après"u8.ToArray(), Ct);
            }

            string written = await File.ReadAllTextAsync(path, Ct);

            Assert.Contains("héllo — 世界", written, StringComparison.Ordinal);
            Assert.Contains("après", written, StringComparison.Ordinal);

            // The relaxed encoder's allowed set is UnicodeRanges.All, which is the BMP: a rune
            // above U+FFFF is still written as its surrogate pair. That is valid JSON and it
            // round-trips, and it is the one part of the source text write-back does not keep.
            Assert.Contains("\\uD83D\\uDE80", written, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Ticket 015 E1: <c>--max-entries</c> bounds the entries — every object key and every array
    /// element, anywhere in the document — jsonfs will hold. The create or <c>mkdir</c> that
    /// would go past it is refused with <c>ENOSPC</c> and changes nothing (reference §8 rule 27,
    /// alongside the byte and depth caps); a rename costs no entry; a remove frees one, so the
    /// next create succeeds; and a document already past the cap is refused at startup naming
    /// the cap, exactly as the byte and depth caps are.
    /// <b>Mutation:</b> drop the entry check from <c>JsonTree.ValidateGrowth</c> and the first
    /// refusal below is answered with success; drop <c>CountEntry</c> from the load and the
    /// startup refusal never fires.
    /// </summary>
    [Fact]
    public async Task CreateBeyondMaxEntriesIsEnospc()
    {
        // Four entries: two keys at the root, two elements in the array.
        const string Document = """{"a":"x","list":[1,2]}""";
        JsonTree document = Parse(Document);
        document.EntryLimit = 4;
        using JsonFilesystem filesystem = new(document, writable: true);
        await using ServerHarness harness = await ServerHarness.StartAsync(filesystem: filesystem);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePException create = await Assert.ThrowsAsync<NinePException>(
            async () => await session.CreateFileAsync("b", cancellationToken: Ct));
        Assert.Equal(Errno.ENOSPC, create.Error.Errno);

        NinePException mkdir = await Assert.ThrowsAsync<NinePException>(
            async () => await session.MkdirAsync("d", Perms.P0755, Ct));
        Assert.Equal(Errno.ENOSPC, mkdir.Error.Errno);

        NinePException append = await Assert.ThrowsAsync<NinePException>(
            async () => await session.CreateFileAsync("list/2", cancellationToken: Ct));
        Assert.Equal(Errno.ENOSPC, append.Error.Errno);

        // Whole or nothing: the listings are as they were.
        Assert.Equal(["a", "list"], await NamesAsync(session, "/"));
        Assert.Equal(["0", "1"], await NamesAsync(session, "list"));

        // A rename moves an entry, it does not add one.
        await session.RenameAsync("a", "z", Ct);
        Assert.Equal(["list", "z"], await NamesAsync(session, "/"));

        // A remove frees the count, and the create after it succeeds — once.
        await session.RemoveAsync("z", Ct);
        await using (NinePFid created = await session.CreateFileAsync("b", cancellationToken: Ct))
        {
            Assert.Equal(QidType.QTFILE, created.Qid.Type);
        }

        Assert.Equal(["b", "list"], await NamesAsync(session, "/"));
        NinePException again = await Assert.ThrowsAsync<NinePException>(
            async () => await session.MkdirAsync("d", Perms.P0755, Ct));
        Assert.Equal(Errno.ENOSPC, again.Error.Errno);

        // Startup: the flag sets the cap, and a document already past it is refused before it is
        // served, naming the cap; one exactly at the cap loads.
        JsonFsOptions options = JsonFsOptions.Parse(
            ["--listen", "tcp://127.0.0.1:0", "--file", "d.json", "--max-entries", "3"]);
        Assert.Equal(3, options.MaxEntries);
        Assert.Equal(
            JsonTree.DefaultMaxEntries,
            JsonFsOptions.Parse(["--listen", "tcp://127.0.0.1:0", "--file", "d.json"]).MaxEntries);
        Assert.Throws<JsonFsUsageException>(
            () => JsonFsOptions.Parse(["--listen", "tcp://127.0.0.1:0", "--file", "d.json", "--max-entries", "0"]));

        using (MemoryStream tooMany = new(Encoding.UTF8.GetBytes(Document)))
        {
            JsonFsStartupException refusal = Assert.Throws<JsonFsStartupException>(
                () => JsonTree.Parse(tooMany, "many.json", options.MaxEntries));
            Assert.Contains("more than 3 entries", refusal.Message, StringComparison.Ordinal);
        }

        using (MemoryStream exact = new(Encoding.UTF8.GetBytes(Document)))
        {
            Assert.Equal(2, JsonTree.Parse(exact, "exact.json", maxEntries: 4).Root.Children.Count);
        }
    }

    /// <summary>
    /// Ticket 015 E1: <c>--write-back-delay</c> coalesces every mutation inside one window into
    /// one rewrite, measured on the injected clock; a rewrite the window cannot make keeps the
    /// old document whole, the document dirty and the window armed for a retry (the failure
    /// semantics of F30, with no request to answer); a graceful shutdown — the filesystem's
    /// dispose — writes back what the open window still owes, so conformance Part B step 8
    /// holds; and a delay of zero keeps today's one rewrite per mutation. Rewrites are counted
    /// at the rename over the original, which is the one step every rewrite performs.
    /// <b>Mutation:</b> rewrite inside <c>JsonFsMutator.Mutate</c> whatever the delay and the
    /// coalescing count fails; drop the rewrite from <c>Dispose</c> and the "last" write below
    /// never reaches the disk.
    /// </summary>
    [Fact]
    public async Task WriteBackIsCoalescedAndFlushedOnShutdown()
    {
        string directory = Path.Combine(Path.GetTempPath(), "jsonfs-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "doc.json");

        try
        {
            await File.WriteAllTextAsync(path, """{"name":"before"}""", Ct);
            ManualClock clock = new();
            TimeSpan window = TimeSpan.FromMilliseconds(250);
            int rewrites = 0;

            using (JsonFilesystem filesystem = new(
                JsonTree.Load(path), writable: true, writeBackPath: path, clock: clock, writeBackDelay: window))
            {
                filesystem.Persistence.AfterReplace = () => rewrites++;
                await using ServerHarness harness = await ServerHarness.StartAsync(filesystem: filesystem);
                await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

                // Several mutations inside one window: one window open, nothing on disk yet.
                await WriteAsync(session, "name", "after");
                await using (await session.CreateFileAsync("added", Perms.P0644, Ct))
                {
                }

                await session.MkdirAsync("dir", Perms.P0755, Ct);

                Assert.Equal(0, rewrites);
                Assert.Equal(1, clock.Arms);
                Assert.Equal(window, clock.DueTime);
                Assert.True(filesystem.Persistence.IsDirty);
                Assert.Equal("before", await NameOnDiskAsync(path));

                // The window closes: one rewrite, carrying all of them.
                clock.Fire();

                Assert.Equal(1, rewrites);
                Assert.False(filesystem.Persistence.IsDirty);
                using (JsonDocument written = JsonDocument.Parse(await File.ReadAllTextAsync(path, Ct)))
                {
                    Assert.Equal("after", written.RootElement.GetProperty("name").GetString());
                    Assert.Equal(JsonValueKind.String, written.RootElement.GetProperty("added").ValueKind);
                    Assert.Equal(JsonValueKind.Object, written.RootElement.GetProperty("dir").ValueKind);
                }

                // A rewrite the window cannot make: the old document stays whole, the temp file
                // is gone, the document stays dirty and the window is armed again for a retry.
                filesystem.Persistence.BeforeReplace = () => throw new IOException("injected replacement failure");
                await session.MkdirAsync("late", Perms.P0755, Ct);
                Assert.Equal(2, clock.Arms);
                clock.Fire();

                Assert.Equal(1, rewrites);
                Assert.True(filesystem.Persistence.IsDirty);
                Assert.Equal(3, clock.Arms);
                Assert.Equal([path], Directory.GetFiles(directory));

                filesystem.Persistence.BeforeReplace = null;
                clock.Fire();
                Assert.Equal(2, rewrites);
                Assert.False(filesystem.Persistence.IsDirty);

                // The next mutation opens a new window rather than rewriting on the spot.
                await WriteAsync(session, "name", "last");
                Assert.Equal(2, rewrites);
                Assert.Equal(4, clock.Arms);
                Assert.Equal("after", await NameOnDiskAsync(path));
            }

            // The harness stopped the server, then the dispose flushed what the window owed.
            Assert.Equal(3, rewrites);
            Assert.Equal("last", await NameOnDiskAsync(path));
            Assert.True(clock.TimerDisposed);
            Assert.Equal([path], Directory.GetFiles(directory));

            // A timer that fires late does nothing to a disposed filesystem.
            clock.Fire();
            Assert.Equal(3, rewrites);

            // Delay zero is today's behaviour: one rewrite per mutation, nothing owed at the end,
            // and no window ever opened.
            rewrites = 0;
            using (JsonFilesystem immediate = new(JsonTree.Load(path), writable: true, writeBackPath: path, clock: clock))
            {
                immediate.Persistence.AfterReplace = () => rewrites++;
                await using ServerHarness harness = await ServerHarness.StartAsync(filesystem: immediate);
                await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

                await using (await session.CreateFileAsync("one", Perms.P0644, Ct))
                {
                }

                Assert.Equal(1, rewrites);
                await session.MkdirAsync("two", Perms.P0755, Ct);
                Assert.Equal(2, rewrites);
                await session.RemoveAsync("one", Ct);
                Assert.Equal(3, rewrites);
                Assert.False(immediate.Persistence.IsDirty);
            }

            Assert.Equal(3, rewrites);
            Assert.Equal(4, clock.Arms);
            using (JsonDocument written = JsonDocument.Parse(await File.ReadAllTextAsync(path, Ct)))
            {
                Assert.False(written.RootElement.TryGetProperty("one", out _));
                Assert.Equal(JsonValueKind.Object, written.RootElement.GetProperty("two").ValueKind);
            }

            // The flag that sets the window turns write-back, and with it writing, on.
            JsonFsOptions options = JsonFsOptions.Parse(
                ["--listen", "tcp://127.0.0.1:0", "--file", path, "--write-back-delay", "250"]);
            Assert.Equal(window, options.WriteBackDelay);
            Assert.True(options.WriteBack);
            Assert.True(options.Writable);
            Assert.Equal(
                TimeSpan.Zero,
                JsonFsOptions.Parse(["--listen", "tcp://127.0.0.1:0", "--file", path, "--write-back"]).WriteBackDelay);
            Assert.Contains("--write-back-delay implies --write-back", JsonFsOptions.Usage, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The names in a directory, sorted, as the client lists them.</summary>
    private static async Task<string[]> NamesAsync(NinePSession session, string path) =>
        [.. (await session.ReadDirAsync(path, Ct)).Select(entry => entry.Name).Order(StringComparer.Ordinal)];

    /// <summary>The <c>name</c> value of the document on disk.</summary>
    private static async Task<string?> NameOnDiskAsync(string path)
    {
        using JsonDocument written = JsonDocument.Parse(await File.ReadAllTextAsync(path, Ct));
        return written.RootElement.GetProperty("name").GetString();
    }

    /// <summary>
    /// A clock whose one timer fires when the test says so, so a write-back window is measured
    /// in assertions rather than in wall-clock sleeps.
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        /// <summary>How many times the window was opened: the timer's creation plus every re-arm.</summary>
        public int Arms { get; private set; }

        /// <summary>The due time of the last arm.</summary>
        public TimeSpan DueTime { get; private set; }

        /// <summary>True once the timer has been disposed.</summary>
        public bool TimerDisposed { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            Arms++;
            return new Timer(this);
        }

        /// <summary>Fires the timer's callback on the calling thread.</summary>
        public void Fire() => _callback?.Invoke(_state);

        private sealed class Timer(ManualClock owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner.DueTime = dueTime;
                owner.Arms++;
                return true;
            }

            public void Dispose() => owner.TimerDisposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>The repository root, found by walking up from the test assembly.</summary>
    /// <returns>The absolute path of the repository root.</returns>
    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "docs", "examples.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }

    private static JsonScalarKind KindOf(JsonTree tree, string name) =>
        tree.Root.Find(name)?.Node is JsonScalarNode scalar
            ? scalar.Kind
            : throw new InvalidOperationException(name + " is not a scalar");

    private static string Format(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return JsonNumber.Format(document.RootElement);
    }

    private static Arbitrary<string[]> KeySets()
    {
        // The alphabet is every fragment the encoding has an opinion about, so a generated key is
        // far more likely to collide than a random string would be.
        string[] alphabet = ["a", "%", "/", ".", "..", "2F", "25", "2E", "\0", "é", string.Empty];

        Gen<string> key = Gen.Elements(alphabet)
            .ArrayOf(4)
            .Select(parts => string.Concat(parts));

        return key.ArrayOf().ToArbitrary();
    }

    /// <summary>
    /// Reference §8 rule 27: an update is applied whole or refused whole. A <c>Twstat</c> carrying
    /// a length of zero <b>and</b> a name performs both, where the handler used to return after
    /// the first field it honoured and drop the rename on the floor.
    /// <b>Mutation:</b> return from <c>JsonNodeHandler.SetAttrAsync</c> as soon as the truncation
    /// has been applied and the name assertion below fails.
    /// </summary>
    [Fact]
    public async Task SetAttrAppliesATruncationAndARenameTogether()
    {
        JsonTree document = Parse("""{"greeting":"hello","other":"kept"}""");
        using JsonFilesystem filesystem = new(document, writable: true);
        await using ServerHarness harness = await ServerHarness.StartAsync(filesystem: filesystem);

        // The name only reaches a handler through a Twstat: Tsetattr has no name field at all.
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        await session.SetAttrAsync("greeting", new SetAttr { Size = 0, Name = "salutation" }, Ct);

        Assert.Equal(
            ["other", "salutation"],
            (await session.ReadDirAsync("/", Ct)).Select(entry => entry.Name).Order(StringComparer.Ordinal));
        Assert.Empty(await ReadAsync(session, "salutation"));
    }

    /// <summary>
    /// Reference §8 rule 27: a field jsonfs cannot honour refuses the whole update, so the
    /// truncation and the rename that rode along with it change nothing. The permission bits are
    /// derived from the server's options, and a <c>wstat</c> that appeared to set them used to be
    /// answered with success as long as a length or a name was set beside them.
    /// <b>Mutation:</b> drop the derived-field check from <c>JsonNodeHandler.SetAttrAsync</c> and
    /// both "nothing changed" assertions below fail.
    /// </summary>
    /// <param name="update">An update naming one field jsonfs cannot honour, beside two it can.</param>
    /// <returns>A task that completes when the refusal has been checked.</returns>
    [Theory]
    [MemberData(nameof(UnsupportedUpdates))]
    public async Task SetAttrWithAnUnsupportedFieldChangesNothing(SetAttr update)
    {
        JsonTree document = Parse("""{"greeting":"hello","other":"kept"}""");
        using JsonFilesystem filesystem = new(document, writable: true);
        await using ServerHarness harness = await ServerHarness.StartAsync(filesystem: filesystem);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await session.SetAttrAsync("greeting", update, Ct));

        Assert.Equal(Errno.EOPNOTSUPP, refusal.Error.Errno);

        Assert.Equal(
            ["greeting", "other"],
            (await session.ReadDirAsync("/", Ct)).Select(entry => entry.Name).Order(StringComparer.Ordinal));
        Assert.Equal("hello", await ReadAsync(session, "greeting"));
    }

    /// <summary>
    /// Rule 19: JSON has nowhere to keep an append-only, exclusive or temporary flag, so a
    /// <c>Tcreate</c> asking for one is refused whole and no member is added — where the mode a
    /// create carries is dropped, the flags it carries are not.
    /// </summary>
    [Fact]
    public async Task ACreateAskingForAFlagIsRefused()
    {
        JsonTree document = Parse("""{"greeting":"hello"}""");
        using JsonFilesystem filesystem = new(document, writable: true);
        await using ServerHarness harness = await ServerHarness.StartAsync(filesystem: filesystem);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid root = await session.WalkAsync("/", Ct);
        await using (root.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.CreateAsync(
                    new Tcreate(0, root.Fid, "log", ModeBits.DMAPPEND | 0x1A4, 1, null), Ct));

            Assert.Equal(Errno.EOPNOTSUPP, refusal.Error.Errno);
        }

        Assert.Equal(
            ["greeting"],
            (await session.ReadDirAsync("/", Ct)).Select(entry => entry.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>Updates that name a truncation, a rename and one field jsonfs does not have.</summary>
    /// <returns>One update per row.</returns>
    public static TheoryData<SetAttr> UnsupportedUpdates() =>
    [
        new SetAttr { Size = 0, Name = "salutation", Perm = Perms.P0777, Flags = FileFlags.None },
        new SetAttr { Size = 0, Name = "salutation", Perm = Perms.P0644, Flags = FileFlags.Append },
        new SetAttr { Size = 0, Name = "salutation", GroupName = "wheel" },
        new SetAttr { Size = 0, Name = "salutation", MTime = new TimeSpec(1, 0) },
    ];

    /// <summary>
    /// Reference §8 rule 27: a truncation jsonfs cannot perform is refused rather than answered
    /// with success. A JSON scalar has no representation for "padded out to n bytes", and a
    /// directory's length is not a client's to set (stat(5)).
    /// </summary>
    [Fact]
    public async Task SetAttrRefusesALengthItCannotProduce()
    {
        JsonTree document = Parse("""{"greeting":"hello","dir":{"leaf":"kept"}}""");
        using JsonFilesystem filesystem = new(document, writable: true);
        await using ServerHarness harness = await ServerHarness.StartAsync(filesystem: filesystem);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePException grown = await Assert.ThrowsAsync<NinePException>(
            async () => await session.SetAttrAsync("greeting", new SetAttr { Size = 64 }, Ct));
        Assert.Equal(Errno.EOPNOTSUPP, grown.Error.Errno);

        NinePException directory = await Assert.ThrowsAsync<NinePException>(
            async () => await session.SetAttrAsync("dir", new SetAttr { Size = 8 }, Ct));
        Assert.Equal(Errno.EISDIR, directory.Error.Errno);

        Assert.Equal("hello", await ReadAsync(session, "greeting"));
    }

    private static JsonTree Parse(string document)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(document));
        return JsonTree.Parse(stream, "test.json");
    }

    private static async Task WriteAsync(NinePSession session, string path, string text)
    {
        await using NinePFid file = await session.OpenFileAsync(
            path, OpenMode.Write, OpenFlags.Truncate, Ct);
        await file.WriteAsync(0, Encoding.UTF8.GetBytes(text), Ct);
    }

    private static async Task<string> ReadAsync(NinePSession session, string path)
    {
        await using NinePFid file = await session.OpenFileAsync(path, OpenMode.Read, OpenFlags.None, Ct);
        return Encoding.UTF8.GetString(await file.ReadAllAsync(Ct));
    }
}
#endif
