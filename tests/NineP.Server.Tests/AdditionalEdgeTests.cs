using System.Globalization;
using System.Text;
using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests;

public sealed class AdditionalEdgeTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);
    public static TheoryData<Dialect> Dialects => [Dialect.P9_2000, Dialect.P9_2000_u, Dialect.P9_2000_L];

    [Theory, MemberData(nameof(Dialects))]
    public async Task F1_UnopenedAndWrongModeFidsAreRefusedWithoutClosing(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession s = await h.ConnectAsync(dialect);
        await using NinePFid file = await s.WalkAsync("hello.txt", Ct);
        await Error(Errno.EINVAL, async () => await file.ReadAsync(0, new byte[1], Ct));
        if (dialect == Dialect.P9_2000_L)
        {
            await Error(Errno.EINVAL, async () => await s.Messages.ReaddirAsync(new Treaddir(0, s.Root.Fid, 0, 100), Ct));
        }
        await file.OpenAsync(OpenMode.Read, OpenFlags.None, Ct);
        await Error(Errno.EACCES, async () => await file.WriteAsync(0, new byte[1], Ct));
        await using NinePFid writer = await s.OpenFileAsync("hello.txt", OpenMode.Write, OpenFlags.None, Ct);
        await Error(Errno.EACCES, async () => await writer.ReadAsync(0, new byte[1], Ct));
        Assert.Equal(FileKind.Directory, (await s.GetAttrAsync("/", Ct)).Kind);
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F2_F3_EmptyDirectoriesAndZeroCounts(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession s = await h.ConnectAsync(dialect);
        await using NinePFid empty = await s.OpenFileAsync("sub", OpenMode.Read, OpenFlags.None, Ct);
        Assert.Empty(await DirectoryPage(s, empty, 0, 4096));
        Assert.Empty(await DirectoryPage(s, empty, 0, 4096));
        await using NinePFid root = await s.OpenFileAsync("/", OpenMode.Read, OpenFlags.None, Ct);
        Assert.Empty(await DirectoryPage(s, root, 0, 0));
        Assert.NotEmpty(await DirectoryPage(s, root, 0, 4096));
        await using NinePFid file = await s.OpenFileAsync("hello.txt", OpenMode.Read, OpenFlags.None, Ct);
        Assert.Equal(0, await file.ReadAsync(0, Memory<byte>.Empty, Ct));
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F4_F18_F19_HighOffsetsEofAndEmptyWritesUseBoundedBuffers(Dialect dialect)
    {
        const ulong Length = (1ul << 32) + 1048576;
        GeneratedFilesystem fs = new(Length);
        await using ServerHarness h = await ServerHarness.StartAsync(filesystem: fs);
        await using NinePSession s = await h.ConnectAsync(dialect);
        await using NinePFid file = await s.OpenFileAsync("data", OpenMode.ReadWrite, OpenFlags.None, Ct);
        byte[] buffer = new byte[33];
        foreach (ulong offset in new ulong[] { 0, (1ul << 31) - 8, 1ul << 31, (1ul << 32) - 8, 1ul << 32, Length - 1 })
        {
            int count = await file.ReadAsync(offset, buffer, Ct);
            Assert.Equal((int)Math.Min((ulong)buffer.Length, Length - offset), count);
            Assert.True(OffsetPattern.Matches(offset, buffer.AsSpan(0, count)), $"bad bytes at {offset}");
            OffsetPattern.Fill(offset, buffer);
            Assert.Equal(buffer.Length, await file.WriteAsync(offset, buffer, Ct));
            Assert.Equal(offset, fs.File.LastWriteOffset);
        }
        foreach (ulong offset in new[] { Length, Length + 1, ulong.MaxValue })
        {
            Assert.Equal(0, await file.ReadAsync(offset, buffer, Ct));
            Assert.Equal(0, await file.WriteAsync(offset, ReadOnlyMemory<byte>.Empty, Ct));
        }
        Assert.Equal(Length, (await file.GetAttrAsync(Ct)).Size);
        Assert.InRange(fs.File.LargestRead, 1, buffer.Length);
        Assert.True(OffsetPattern.Matches(1ul << 32, new byte[] { 0xA5, 0xB6, 0xC7, 0xF8, 0xE9, 0xFA, 0x0B, 0x1C }));
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F5_AttachFidCanBeClunkedAndRemoveFreesItEvenOnError(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using WireClient wire = await WireClient.ConnectAsync(h, dialect, cancellationToken: Ct);
        await wire.AttachAsync(1, Ct);
        await wire.SendAsync(new Tclunk(2, 1), Ct);
        await wire.ReceiveAsync<Rclunk>(Ct);
        await wire.AttachAsync(1, Ct);
        await wire.SendAsync(new Tremove(3, 1), Ct);
        // Every dialect recovers EPERM: plain 9P2000 carries the ename "Operation not permitted",
        // which is the string Linux maps to EPERM, so the collapse into EACCES that the shared
        // "permission denied" wording forced is gone (owner decision of 2026-09-10).
        Assert.Equal(Errno.EPERM, await WireError(wire));
        await wire.SendAsync(new Tclunk(4, 1), Ct);
        Assert.Equal(Errno.EBADF, await WireError(wire));
        await wire.AttachAsync(1, Ct);
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F6_F25_CreateCollisionsPreserveTheParentFid(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession s = await h.ConnectAsync(dialect);
        await using NinePFid parent = await s.WalkAsync("/", Ct);
        Qid original = parent.Qid;
        await Error(Errno.EEXIST, async () => await parent.CreateAsync("hello.txt", 0x1A4, OpenMode.Read, OpenFlags.None, Ct));
        await Error(Errno.EEXIST, async () => await s.MkdirAsync("hello.txt", 0x1ED, Ct));
        await Error(Errno.EEXIST, async () => await s.MkdirAsync("sub", 0x1ED, Ct));
        if (dialect == Dialect.P9_2000_L)
        {
            await Error(Errno.EEXIST, async () => await s.Messages.SymlinkAsync(new Tsymlink(0, parent.Fid, "hello.txt", "target", 1000), Ct));
            await Error(Errno.EEXIST, async () => await s.Messages.MknodAsync(new Tmknod(0, parent.Fid, "hello.txt", 0x11A4, 0, 0, 1000), Ct));
            await using NinePFid target = await s.WalkAsync("hello.txt", Ct);
            await Error(Errno.EEXIST, async () => await s.Messages.LinkAsync(new Tlink(0, parent.Fid, target.Fid, "hello.txt"), Ct));
        }
        Assert.Equal(original, parent.Qid);
        Assert.Equal(2, (await s.ReadDirAsync("/", Ct)).Count);
        await parent.CreateAsync("new", 0x1A4, OpenMode.Read, OpenFlags.None, Ct);
        Assert.NotEqual(original.Path, parent.Qid.Path);
        Assert.Equal(FileKind.File, (await parent.GetAttrAsync(Ct)).Kind);
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F7b_F7d_MaximumUtf8NamesAndNormalization(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession s = await h.ConnectAsync(dialect, o => o with { Msize = 4096 });
        foreach (string name in new[] { new string('x', 254), new string('x', 255), new string('é', 127) + "x", string.Concat(Enumerable.Repeat("🚀", 63)) + "xyz" })
        {
            await using (NinePFid file = await s.CreateFileAsync(name, 0x1A4, Ct))
            {
                Assert.Equal(1, await file.WriteAsync(0, "x"u8.ToArray(), Ct));
            }
            Assert.Equal(1ul, (await s.GetAttrAsync(name, Ct)).Size);
            Assert.Contains(await s.ReadDirAsync("/", Ct), e => e.Name == name);
            Assert.Equal("x", Encoding.UTF8.GetString(await s.ReadFileAsync(name, Ct)));
            await s.RemoveAsync(name, Ct);
        }
        await s.MkdirAsync("é", 0x1ED, Ct);
        await Error(Errno.ENOENT, async () => await s.GetAttrAsync("e\u0301", Ct));
        Assert.Single(await s.ReadDirAsync("/", Ct), e => e.Name == "é");
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F9_StaleHandleNeverReadsOrWritesAReplacement(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession a = await h.ConnectAsync(dialect);
        await using NinePSession b = await h.ConnectAsync(dialect);
        await using NinePFid stale = await a.OpenFileAsync("hello.txt", OpenMode.ReadWrite, OpenFlags.None, Ct);
        await using NinePFid unopened = await a.WalkAsync("hello.txt", Ct);
        await b.RemoveAsync("hello.txt", Ct);
        await using (NinePFid replacement = await b.CreateFileAsync("hello.txt", 0x1B6, Ct))
        {
            await replacement.WriteAsync(0, "replacement"u8.ToArray(), Ct);
            Assert.NotEqual(stale.Qid.Path, replacement.Qid.Path);
        }
        Assert.Equal("hello, 9P\n", Encoding.UTF8.GetString(await stale.ReadAllAsync(Ct)));
        Assert.Equal(stale.Qid.Path, (await unopened.GetAttrAsync(Ct)).Qid.Path);
        await unopened.OpenAsync(OpenMode.Read, OpenFlags.None, Ct);
        Assert.Equal(3, await stale.WriteAsync(0, "old"u8.ToArray(), Ct));
        Assert.Equal("replacement", Encoding.UTF8.GetString(await b.ReadFileAsync("hello.txt", Ct)));
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F10b_F16_F17_ReadAllCapsKnownAndUnderreportedSizes(Dialect dialect)
    {
        foreach (int maximum in new[] { 0, 17, 8192 })
        {
            foreach (int length in new[] { Math.Max(0, maximum - 1), maximum, maximum + 1 })
            {
                foreach (bool unknown in new[] { false, true })
                {
                    GeneratedFilesystem fs = new((ulong)length);
                    if (unknown)
                    {
                        fs.File.ReportedSize = 0;
                    }

                    await using ServerHarness h = await ServerHarness.StartAsync(filesystem: fs);
                    await using NinePSession s = await h.ConnectAsync(dialect, o => o with { MaxReadAll = maximum });
                    if (length > maximum)
                    {
                        await Error(Errno.EFBIG, async () => await s.ReadFileAsync("data", Ct));
                        if (!unknown)
                        {
                            Assert.Equal(0, fs.File.ReadCalls);
                        }
                    }
                    else
                    {
                        byte[] data = await s.ReadFileAsync("data", Ct);
                        Assert.Equal(length, data.Length);
                        Assert.True(OffsetPattern.Matches(0, data));
                    }
                    Assert.Equal(0, fs.File.Opens);
                    Assert.Equal(1, s.LiveFids);
                }
            }
        }
        GeneratedFilesystem huge = new((1ul << 32) + 1048576);
        await using ServerHarness big = await ServerHarness.StartAsync(filesystem: huge);
        await using NinePSession client = await big.ConnectAsync(dialect);
        await Error(Errno.EFBIG, async () => await client.ReadFileAsync("data", Ct));
        Assert.Equal(0, huge.File.ReadCalls);
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F13_F14_LongWalksSplitByCountAndEncodedSizeAndReleaseFailures(Dialect dialect)
    {
        foreach (int length in new[] { 15, 16, 17, 32, 33 })
        {
            MemoryFilesystem tree = new();
            MemoryDirectory parent = tree.Root;
            string component = new('x', 255);
            for (int i = 0; i < length; i++)
            {
                parent = parent.Add(tree.NewDirectory(component, 0x1FF));
            }
            parent.Add(tree.NewFile("leaf", 0x1A4));
            await using ServerHarness h = await ServerHarness.StartAsync(tree: tree);
            await using NinePSession s = await h.ConnectAsync(dialect, o => o with { Msize = 4096 });
            string path = string.Join('/', Enumerable.Repeat(component, length));
            Assert.Equal(parent.Qid.Path, (await s.GetAttrAsync(path, Ct)).Qid.Path);
            await Error(Errno.ENOENT, async () => await s.GetAttrAsync(path + "/missing", Ct));
            await Error(Errno.ENOTDIR, async () => await s.GetAttrAsync(path + "/leaf/x", Ct));
            Assert.Equal(1, s.LiveFids);
            Assert.Equal(tree.Root.Qid.Path, s.Root.Qid.Path);
        }
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F15_ChunkBoundariesAndRepeatedShortReads(Dialect dialect)
    {
        const int Chunk = 4096 - Constants.IOHDRSZ;
        foreach (int length in new[] { 0, 1, Chunk - 1, Chunk, Chunk + 1, Chunk * 2, Chunk * 2 + 3 })
        {
            GeneratedFilesystem fs = new((ulong)length);
            fs.File.MaxChunk = 97;
            await using ServerHarness h = await ServerHarness.StartAsync(filesystem: fs);
            await using NinePSession s = await h.ConnectAsync(dialect, o => o with { Msize = 4096 });
            byte[] data = await s.ReadFileAsync("data", Ct);
            Assert.Equal(length, data.Length);
            Assert.True(OffsetPattern.Matches(0, data));
        }
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F11b_F21_F22_PagedLongNamesRewindAndIndependentFids(Dialect dialect)
    {
        GeneratedFilesystem fs = new(entries: 10000, nameBytes: 255);
        await using ServerHarness h = await ServerHarness.StartAsync(filesystem: fs);
        await using NinePSession s = await h.ConnectAsync(dialect, o => o with { Msize = 4096 });
        await using NinePFid a = await s.OpenFileAsync("/", OpenMode.Read, OpenFlags.None, Ct);
        await using NinePFid b = await s.OpenFileAsync("/", OpenMode.Read, OpenFlags.None, Ct);
        byte[] first = await DirectoryPage(s, a, 0, 1000);
        Assert.NotEmpty(first);
        Assert.Equal(first, await DirectoryPage(s, b, 0, 1000));
        Assert.Empty(await DirectoryPage(s, a, 0, 0));
        Assert.Equal(first, await DirectoryPage(s, a, 0, 1000));
        await Error(Errno.ERANGE, async () => await DirectoryPage(s, a, 0, 1));
        Assert.Equal(first, await DirectoryPage(s, a, 0, 1000));
        int count = 0;
        await foreach (DirEntry entry in a.ReadDirAsync(Ct))
        {
            Assert.Equal(fs.Root.NameAt(count), entry.Name);
            count++;
            Assert.True(count <= 10000);
        }
        Assert.Equal(10000, count);
        Assert.InRange(fs.Root.LargestPage, 1, 64);
        Assert.Equal(first, await DirectoryPage(s, a, 0, 1000));
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F21_F22_ExactUtf8RecordBudgetsAndZeroCountResume(Dialect dialect)
    {
        MemoryFilesystem tree = new();
        string name = string.Concat(Enumerable.Repeat("🚀", 63)) + "abc";
        tree.Root.Add(tree.NewFile(name, 0x1A4));
        tree.Root.Add(tree.NewFile("second", 0x1A4));
        await using ServerHarness h = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession s = await h.ConnectAsync(dialect);
        await using NinePFid fid = await s.OpenFileAsync("/", OpenMode.Read, OpenFlags.None, Ct);
        byte[] all = await DirectoryPage(s, fid, 0, 4096);
        int size = dialect == Dialect.P9_2000_L
            ? DirEntryCodec.GetEncodedSize(new DirEntry(name, default, FileKind.File, 1))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(all) + 2;
        await Error(Errno.ERANGE, async () => await DirectoryPage(s, fid, 0, (uint)(size - 1)));
        foreach (uint budget in new[] { (uint)size, (uint)(size + 1) })
        {
            byte[] first = await DirectoryPage(s, fid, 0, budget);
            Assert.Equal(all[..size], first);
            ulong resume = (ulong)size;
            if (dialect == Dialect.P9_2000_L)
            {
                Assert.True(DirEntryCodec.TryReadAll(first, out IReadOnlyList<DirEntry> entries, out _));
                resume = Assert.Single(entries).Cursor;
            }
            // In legacy dialects an accidental rewind here would make resume a bad offset.
            Assert.Empty(await DirectoryPage(s, fid, 0, 0));
            Assert.Empty(await DirectoryPage(s, fid, resume, 0));
            Assert.Equal(all[size..], await DirectoryPage(s, fid, resume, 4096));
        }
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F12b_F25_EnospcReleasesCapacityAndPreservesFid(Dialect dialect)
    {
        MemoryFilesystem tree = new();
        tree.Root.Capacity = 1;
        await using ServerHarness h = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession s = await h.ConnectAsync(dialect);
        await s.MkdirAsync("one", 0x1ED, Ct);
        await using NinePFid parent = await s.WalkAsync("/", Ct);
        await Error(Errno.ENOSPC, async () => await parent.CreateAsync("two", 0x1A4, OpenMode.Read, OpenFlags.None, Ct));
        Assert.Equal(FileKind.Directory, (await parent.GetAttrAsync(Ct)).Kind);
        await s.RemoveAsync("one", Ct);
        await parent.CreateAsync("two", 0x1A4, OpenMode.Read, OpenFlags.None, Ct);
        Assert.Equal("two", Assert.Single(await s.ReadDirAsync("/", Ct)).Name);
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F26_ConcurrentCreatesHaveExactlyOneWinner(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync(tree: new MemoryFilesystem());
        await using NinePSession a = await h.ConnectAsync(dialect);
        await using NinePSession b = await h.ConnectAsync(dialect);
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> Create(NinePSession s)
        {
            await start.Task;
            try { await s.MkdirAsync("same", 0x1ED, Ct); return 0; }
            catch (NinePException error) { return error.Error.Errno; }
        }
        Task<int> first = Create(a);
        Task<int> second = Create(b);
        start.SetResult();
        Assert.Equal(new[] { 0, Errno.EEXIST }, (await Task.WhenAll(first, second)).Order().ToArray());
        Assert.Single(await a.ReadDirAsync("/", Ct));
    }

    internal static async Task Error(int errno, Func<Task> action)
    {
        NinePException error = await Assert.ThrowsAsync<NinePException>(action);
        Assert.Equal(errno, error.Error.Errno);
    }

    internal static async Task<byte[]> DirectoryPage(NinePSession s, NinePFid fid, ulong offset, uint count) =>
        s.Dialect == Dialect.P9_2000_L
            ? (await s.Messages.ReaddirAsync(new Treaddir(0, fid.Fid, offset, count), Ct)).Data.ToArray()
            : (await s.Messages.ReadAsync(new Tread(0, fid.Fid, offset, count), Ct)).Data.ToArray();

    internal static async Task<int> WireError(WireClient wire)
    {
        if (wire.Dialect == Dialect.P9_2000_L)
        {
            return (await wire.ReceiveAsync<Rlerror>(Ct)).Ecode;
        }
        Rerror error = await wire.ReceiveAsync<Rerror>(Ct);
        return ErrorProjector.FromRerror(in error).Errno;
    }
}
