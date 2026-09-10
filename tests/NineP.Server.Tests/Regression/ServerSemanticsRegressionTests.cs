using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Regression;

/// <summary>Regressions in walk ancestry, version reset, permission classes, append-only metadata and clunk timing.</summary>
[Trait("Category", "Regression")]
public sealed class ServerSemanticsRegressionTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>Separate walks must retain the complete ancestry of the fid.</summary>
    [Fact]
    public async Task SeparateDotDotWalksReachTheRealRoot()
    {
        MemoryFilesystem tree = new();
        MemoryDirectory a = tree.NewDirectory("a", 0x1FF);
        MemoryDirectory b = tree.NewDirectory("b", 0x1FF);
        tree.Root.Add(a);
        a.Add(b);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);
        await session.Messages.WalkAsync(new Twalk(0, session.Root.Fid, 60, ["a", "b"]), Ct);
        await session.Messages.WalkAsync(new Twalk(0, 60, 60, [".."]), Ct);
        Rwalk result = await session.Messages.WalkAsync(new Twalk(0, 60, 60, [".."]), Ct);
        Assert.Equal(tree.Root.Qid, Assert.Single(result.Wqids));
    }

    /// <summary>A non-directory after a successful element still yields a partial walk.</summary>
    [Fact]
    public async Task NonDirectoryAfterFirstElementReturnsPartialWalk()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);
        Rwalk result = await session.Messages.WalkAsync(
            new Twalk(0, session.Root.Fid, 61, ["hello.txt", "child"]), Ct);
        Assert.Single(result.Wqids);
    }

    /// <summary>Dot-dot must check search permission just like every other walk element.</summary>
    [Fact]
    public async Task DotDotRequiresSearchPermission()
    {
        MemoryFilesystem tree = new();
        MemoryDirectory locked = tree.NewDirectory("locked", 0);
        tree.Root.Add(locked);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);
        await session.Messages.WalkAsync(new Twalk(0, session.Root.Fid, 62, ["locked"]), Ct);
        NinePException error = await Assert.ThrowsAsync<NinePException>(async () =>
            await session.Messages.WalkAsync(new Twalk(0, 62, 63, [".."]), Ct));
        Assert.Equal(Errno.EACCES, error.Error.Errno);
    }

    /// <summary>A version reset performs remove-on-close cleanup of its implicit clunks.</summary>
    [Fact]
    public async Task VersionResetRemovesOrcloseFiles()
    {
        MemoryFilesystem tree = new();
        tree.Root.Add(tree.NewFile("scratch", 0x1B6));
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000, 8192, Ct);
        await client.AttachAsync(1, Ct);
        await client.WalkAsync(2, 1, 2, ["scratch"], Ct);
        await client.SendAsync(new Topen(3, 2, 0x40), Ct);
        await client.ReceiveAsync<Ropen>(Ct);
        await client.SendAsync(new Tversion(Constants.NOTAG, 8192, "9P2000"), Ct);
        await client.ReceiveAsync<Rversion>(Ct);
        Assert.False(tree.Root.Children.ContainsKey("scratch"));
    }

    /// <summary>A version reset releases a globally held exclusive open.</summary>
    [Fact]
    public async Task VersionResetReleasesExclusiveOpen()
    {
        MemoryFilesystem tree = new();
        MemoryFile file = tree.NewFile("exclusive", 0x1B6);
        file.Exclusive = true;
        tree.Root.Add(file);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000, 8192, Ct);
        await client.AttachAsync(1, Ct);
        await client.WalkAsync(2, 1, 2, ["exclusive"], Ct);
        await client.SendAsync(new Topen(3, 2, 0), Ct);
        await client.ReceiveAsync<Ropen>(Ct);
        await client.SendAsync(new Tversion(Constants.NOTAG, 8192, "9P2000"), Ct);
        await client.ReceiveAsync<Rversion>(Ct);
        await using NinePSession other = await harness.ConnectAsync(Dialect.P9_2000);
        await using NinePFid reopened = await other.OpenFileAsync("exclusive", OpenMode.Read, OpenFlags.None, Ct);
        Assert.Equal(file.Qid, reopened.Qid);
    }

    /// <summary>Plan 9 permits the owner to use permission granted in the other class.</summary>
    [Fact]
    public async Task PlainPlan9OwnerCanUseOtherReadPermission()
    {
        MemoryFilesystem tree = new();
        tree.Root.Add(tree.NewFile("readable", 4));
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);
        await using NinePFid opened = await session.OpenFileAsync("readable", OpenMode.Read, OpenFlags.None, Ct);
        Assert.NotEqual(0u, opened.Fid);
    }

    /// <summary>Append-only metadata forces append even without an append bit in the open request.</summary>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    [InlineData(Dialect.P9_2000_L)]
    public async Task DmappendIgnoresTheWriteOffset(Dialect dialect)
    {
        MemoryFilesystem tree = new();
        AppendFile file = new(90);
        tree.Root.Add(file);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(dialect);
        await using NinePFid opened = await session.OpenFileAsync("append", OpenMode.Write, OpenFlags.None, Ct);
        await session.Messages.WriteAsync(new Twrite(0, opened.Fid, 0, "X"u8.ToArray()), Ct);
        Assert.Equal("dataX"u8.ToArray(), file.Backing.Data);
    }

    /// <summary>Truncation must be ignored for a file whose metadata says append-only.</summary>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    [InlineData(Dialect.P9_2000_L)]
    public async Task DmappendIgnoresTruncation(Dialect dialect)
    {
        MemoryFilesystem tree = new();
        AppendFile file = new(90);
        tree.Root.Add(file);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(dialect);
        await using NinePFid opened = await session.OpenFileAsync("append", OpenMode.Write, OpenFlags.Truncate, Ct);
        Assert.Equal("data"u8.ToArray(), file.Backing.Data);
    }

    /// <summary>A clunk must not dispose an open instance while its read is still running.</summary>
    [Fact]
    public async Task ClunkDoesNotDisposeAnInFlightRead()
    {
        MemoryFilesystem tree = new();
        MemoryFile file = tree.NewFile("gated", 0x1B6);
        file.Data = "data"u8.ToArray();
        file.ReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.Root.Add(file);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
        await client.AttachAsync(1, Ct);
        await client.WalkAsync(2, 1, 2, ["gated"], Ct);
        await client.SendAsync(new Tlopen(3, 2, 0), Ct);
        await client.ReceiveAsync<Rlopen>(Ct);
        await client.SendAsync(new Tread(4, 2, 0, 4), Ct);
        while (file.ReadsStarted == 0)
        {
            await Task.Delay(1, Ct);
        }

        try
        {
            await client.SendAsync(new Tclunk(5, 2), Ct);
            // A second-fid reply is a progress barrier while the first fid's read remains held.
            await client.SendAsync(new Tgetattr(6, 1, GetAttrMask.Basic), Ct);
            byte[] response;
            do
            {
                response = await client.ReceiveFrameAsync(Ct);
            }
            while (NineP.Protocol.Codec.MessageCodec.PeekTag(response) != 6);

            Assert.Equal(1, file.Opens);
        }
        finally
        {
            file.ReadGate.TrySetResult();
        }
    }

    private sealed class AppendFile(ulong path)
        : MemoryNode("append", FileKind.File, 0x1B6, path), IFileHandler
    {
        public MemoryFile Backing { get; } = new("append", 0x1B6, path) { Data = "data"u8.ToArray() };

        Qid IHandler.Qid => Backing.Qid with { Type = QidType.QTAPPEND };

        async ValueTask<Attr> IHandler.GetAttrAsync(CancellationToken cancellationToken)
        {
            Attr attr = await Backing.GetAttrAsync(cancellationToken);
            return attr with { Flags = FileFlags.Append, Qid = attr.Qid with { Type = QidType.QTAPPEND } };
        }

        public ValueTask<IOpenFile> OpenAsync(
            OpenMode mode, OpenFlags flags, CancellationToken cancellationToken = default) =>
            Backing.OpenAsync(mode, flags, cancellationToken);
    }
}
