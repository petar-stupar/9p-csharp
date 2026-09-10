using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

[Trait("Category", "Conformance")]
public sealed class ContentBoundaryTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);
    public static TheoryData<Dialect> Dialects => BoundaryTests.Dialects;

    [Fact]
    public async Task TheMemoryHandlerRejectsUnrepresentableUpdatesBeforeMutation()
    {
        MemoryFile file = new("seed", 0x1B6, 1) { Data = "seed"u8.ToArray() };
        await using IOpenFile handle = await file.OpenAsync(OpenMode.ReadWrite, OpenFlags.None, Ct);
        foreach (ulong offset in new ulong[] { int.MaxValue, (ulong)int.MaxValue + 1, ulong.MaxValue })
        {
            await BoundaryTests.Error(Errno.EFBIG, async () => await handle.WriteAsync(offset, new byte[2], Ct));
        }
        await BoundaryTests.Error(Errno.EFBIG, async () =>
            await file.SetAttrAsync(new SetAttr { Size = (ulong)int.MaxValue + 1, Perm = 0 }, Ct));
        Assert.Equal(0x1B6u, file.Perm);
        Assert.Equal("seed"u8.ToArray(), file.Data);
        Assert.Null(file.LastUpdate);
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task AZeroByteWriteLeavesANonEmptyFileUntouched(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession s = await h.ConnectAsync(dialect);
        byte[] before = await s.ReadFileAsync("hello.txt", Ct);
        await using NinePFid f = await s.OpenFileAsync("hello.txt", OpenMode.ReadWrite, OpenFlags.None, Ct);
        foreach (ulong at in new ulong[] { 0, 4, (ulong)before.Length, 100, ulong.MaxValue })
        {
            Rwrite reply = await s.Messages.WriteAsync(new Twrite(0, f.Fid, at, ReadOnlyMemory<byte>.Empty), Ct);
            Assert.Equal(0u, reply.Count);
            Assert.Equal((ulong)before.Length, (await f.GetAttrAsync(Ct)).Size);
            Assert.Equal(before, await s.ReadFileAsync("hello.txt", Ct));
        }
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task ATruncatingOpenEmptiesAPlainFile(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession s = await h.ConnectAsync(dialect);
        for (int i = 0; i < 2; i++)
        {
            await using NinePFid f = await s.OpenFileAsync("hello.txt", OpenMode.ReadWrite, OpenFlags.Truncate, Ct);
            Assert.Equal(0ul, (await f.GetAttrAsync(Ct)).Size);
            Assert.Empty((await s.Messages.ReadAsync(new Tread(0, f.Fid, 0, 64), Ct)).Data.ToArray());
        }
        await s.WriteFileAsync("hello.txt", "hi"u8.ToArray(), Ct);
        Assert.Equal("hi"u8.ToArray(), await s.ReadFileAsync("hello.txt", Ct));
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task TruncateNeedsWritePermissionEvenForAReadOpen(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        MemoryFile file = (MemoryFile)h.Tree.Root.Children["hello.txt"];
        file.Perm = 0x124;
        await using NinePSession s = await h.ConnectAsync(dialect);
        await BoundaryTests.Error(Errno.EACCES, async () => await s.OpenFileAsync("hello.txt", OpenMode.Read, OpenFlags.Truncate, Ct));
        Assert.NotEmpty(file.Data);
        file.Perm = 0x1B6;
        await using NinePFid f = await s.OpenFileAsync("hello.txt", OpenMode.Read, OpenFlags.Truncate, Ct);
        Assert.Empty((await s.Messages.ReadAsync(new Tread(0, f.Fid, 0, 64), Ct)).Data.ToArray());
        await BoundaryTests.Error(Errno.EACCES, async () => await f.WriteAsync(0, new byte[1], Ct));
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task AWritePastTheEndZeroFillsTheHole(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession s = await h.ConnectAsync(dialect);
        await s.WriteFileAsync("hello.txt", "abc"u8.ToArray(), Ct);
        await using NinePFid f = await s.OpenFileAsync("hello.txt", OpenMode.ReadWrite, OpenFlags.None, Ct);
        Assert.Equal(1, await f.WriteAsync(6, "z"u8.ToArray(), Ct));
        Assert.Equal(7ul, (await f.GetAttrAsync(Ct)).Size);
        Assert.Equal(new byte[] { 97, 98, 99, 0, 0, 0, 122 }, await s.ReadFileAsync("hello.txt", Ct));
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task AMidFileOverwritePreservesPrefixAndSuffix(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession s = await h.ConnectAsync(dialect);
        await s.WriteFileAsync("hello.txt", "abcdef"u8.ToArray(), Ct);
        await using NinePFid f = await s.OpenFileAsync("hello.txt", OpenMode.ReadWrite, OpenFlags.None, Ct);
        Assert.Equal(2, await f.WriteAsync(2, "XY"u8.ToArray(), Ct));
        Assert.Equal("abXYef"u8.ToArray(), await s.ReadFileAsync("hello.txt", Ct));
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task AWriteToAnOpenedDirectoryIsRefused(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession s = await h.ConnectAsync(dialect);
        await using NinePFid f = await s.OpenFileAsync("/", OpenMode.Read, OpenFlags.None, Ct);
        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await s.Messages.WriteAsync(new Twrite(0, f.Fid, 0, "x"u8.ToArray()), Ct));
        Assert.Equal(Errno.EACCES, refusal.Error.Errno);
        Assert.NotEmpty(await s.ReadFileAsync("hello.txt", Ct));
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task AnEmptyFileReportsSizeZeroAndReadsNothing(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession s = await h.ConnectAsync(dialect);
        await using (NinePFid created = await s.CreateFileAsync("empty", 0x1B6, Ct))
        {
            Assert.Equal(0ul, (await created.GetAttrAsync(Ct)).Size);
        }
        await using NinePFid f = await s.OpenFileAsync("empty", OpenMode.Read, OpenFlags.None, Ct);
        Assert.Empty((await s.Messages.ReadAsync(new Tread(0, f.Fid, 0, 64), Ct)).Data.ToArray());
        Assert.Equal(0ul, (await s.GetAttrAsync("/", Ct)).Size);
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task RemovingAnOpenFidRemovesTheFileAndFreesTheFid(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession s = await h.ConnectAsync(dialect);
        await using NinePFid f = await s.OpenFileAsync("hello.txt", OpenMode.Read, OpenFlags.None, Ct);
        uint id = f.Fid;
        await f.RemoveAsync(Ct);
        await BoundaryTests.Error(Errno.ENOENT, async () => await s.GetAttrAsync("hello.txt", Ct));
        await BoundaryTests.Error(Errno.EBADF, async () => await s.Messages.ReadAsync(new Tread(0, id, 0, 1), Ct));
        Assert.Empty((await s.Messages.WalkAsync(new Twalk(0, s.Root.Fid, id, []), Ct)).Wqids);
        await s.Messages.ClunkAsync(new Tclunk(0, id), Ct);
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task LengthZeroAndExtensionChangeOnlyTheRequestedBytes(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession s = await h.ConnectAsync(dialect);
        await using NinePFid f = await s.WalkAsync("hello.txt", Ct);
        await f.SetAttrAsync(new SetAttr { Size = 0 }, Ct);
        Assert.Empty(await s.ReadFileAsync("hello.txt", Ct));
        await f.SetAttrAsync(new SetAttr { Size = 6 }, Ct);
        Assert.Equal(new byte[6], await s.ReadFileAsync("hello.txt", Ct));
        Assert.Equal(6ul, (await f.GetAttrAsync(Ct)).Size);
        if (dialect != Dialect.P9_2000_L)
        {
            await s.Messages.WstatAsync(new Twstat(0, f.Fid, StatRecord.DontTouch with { Name = "", Length = 2 }), Ct);
            Assert.Equal(2ul, (await s.GetAttrAsync("hello.txt", Ct)).Size);
        }
    }

    [Fact]
    public async Task ARequestMaskOfZeroMarksNothingValidButTheQid()
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using WireClient w = await WireClient.ConnectAsync(h, Dialect.P9_2000_L, cancellationToken: Ct);
        await w.AttachAsync(1, Ct);
        await w.SendAsync(new Tgetattr(2, 1, GetAttrMask.None), Ct);
        byte[] bytes = await w.ReceiveFrameAsync(Ct);
        Assert.Equal(160, bytes.Length);
        Rgetattr reply = MessageCodec.Decode<Rgetattr>(bytes, Dialect.P9_2000_L);
        Assert.Equal(GetAttrMask.None, reply.Valid);
        Assert.Equal(h.Tree.Root.Qid, reply.Qid);
        Assert.All(bytes.Skip(28), b => Assert.Equal(0, b));
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task CreateRequiresAnUnopenedDirectoryFid(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession s = await h.ConnectAsync(dialect);
        await using NinePFid file = await s.WalkAsync("hello.txt", Ct);
        await BoundaryTests.Error(Errno.ENOTDIR, async () => await Create(s, file.Fid));
        await using NinePFid directory = await s.OpenFileAsync("/", OpenMode.Read, OpenFlags.None, Ct);
        NinePException error = await Assert.ThrowsAsync<NinePException>(async () => await Create(s, directory.Fid));
        Assert.Equal(NinePError.FromEname("bad open mode").Errno, error.Error.Errno);
        Assert.NotEmpty(await s.ReadDirAsync("/", Ct));
    }

    private static async Task Create(NinePSession session, uint fid)
    {
        if (session.Dialect == Dialect.P9_2000_L)
        {
            await session.Messages.LcreateAsync(new Tlcreate(0, fid, "new", 0, 0x1A4, 1000), Ct);
        }
        else
        {
            await session.Messages.CreateAsync(new Tcreate(0, fid, "new", 0x1A4, 0, ""), Ct);
        }
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task SixteenWalkElementsSucceedAndNofidIsRefused(Dialect dialect)
    {
        MemoryFilesystem tree = new();
        MemoryDirectory at = tree.Root;
        for (int i = 0; i < 16; i++)
        {
            MemoryDirectory next = tree.NewDirectory("d", 0x1FF);
            at.Add(next);
            at = next;
        }
        await using ServerHarness h = await ServerHarness.StartAsync(tree: tree);
        await using WireClient w = await WireClient.ConnectAsync(h, dialect, cancellationToken: Ct);
        await w.AttachAsync(1, Ct);
        Assert.Equal(16, (await w.WalkAsync(2, 1, 2, Enumerable.Repeat("d", 16).ToArray(), Ct)).Wqids.Count);
        await w.SendAsync(new Twalk(3, 1, Constants.NOFID, []), Ct);
        Assert.NotEqual(0, await BoundaryTests.WireError(w));
        Assert.Empty((await w.WalkAsync(4, 1, 3, [], Ct)).Wqids);
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task AFlushNamingItselfIsAnswered(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using WireClient w = await WireClient.ConnectAsync(h, dialect, cancellationToken: Ct);
        await w.SendAsync(new Tflush(20, 20), Ct);
        Assert.Equal((ushort)20, (await w.ReceiveAsync<Rflush>(Ct)).Tag);
    }
}
