using Microsoft.Extensions.Logging;
using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.Server.Internal;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Regression;

/// <summary>Dispatch-level regressions for fid lifetime, permissions and append serialization.</summary>
[Trait("Category", "Regression")]
public sealed class ServerLifecycleRegressionTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>Both the attribute names and the values require file-read permission before callbacks.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("user.secret")]
    public async Task XattrReadsCheckPermissionsBeforeCallingTheHandler(string name)
    {
        MemoryFilesystem tree = new();
        ObservedFile file = new(90) { Perm = 0 };
        tree.Root.Add(file);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);
        await using NinePFid fid = await session.WalkAsync("observed", Ct);
        NinePException error = await Assert.ThrowsAsync<NinePException>(async () =>
            await session.Messages.XattrwalkAsync(new Txattrwalk(0, fid.Fid, 80, name), Ct));
        Assert.Equal(Errno.EACCES, error.Error.Errno);
        Assert.Equal(0, file.XattrReads);
        file.Perm = Perms.P0644;
        await session.Messages.XattrwalkAsync(new Txattrwalk(0, fid.Fid, 80, name), Ct);
        Assert.Equal(1, file.XattrReads);
        await session.Messages.ClunkAsync(new Tclunk(0, 80), Ct);
    }

    /// <summary>All applicable Plan 9 permission classes are considered; Unix still selects one.</summary>
    [Theory]
    [InlineData(Dialect.P9_2000, "owner", FilePermissions.OtherRead, true)]
    [InlineData(Dialect.P9_2000, "owner", FilePermissions.GroupRead, true)]
    [InlineData(Dialect.P9_2000, "member", FilePermissions.OtherRead, true)]
    [InlineData(Dialect.P9_2000, "member", FilePermissions.GroupRead, true)]
    [InlineData(Dialect.P9_2000, "other", FilePermissions.GroupRead, false)]
    [InlineData(Dialect.P9_2000_u, "owner", FilePermissions.OtherRead, false)]
    [InlineData(Dialect.P9_2000_L, "owner", FilePermissions.OtherRead, false)]
    [InlineData(Dialect.P9_2000_L, "member", FilePermissions.OtherRead, false)]
    [InlineData(Dialect.P9_2000_L, "other", FilePermissions.OtherRead, true)]
    public void PermissionClassesFollowTheDialect(
        Dialect dialect, string user, FilePermissions perm, bool allowed)
    {
        Attr attr = new() { Qid = default, Kind = FileKind.File, Perm = perm, UserName = "owner", GroupName = "group" };
        Identity identity = new() { User = user, Groups = user == "member" ? ["group"] : [] };
        Assert.Equal(allowed, PermissionChecker.Allows(attr, identity, Access.Read, dialect));
    }

    /// <summary>A retired entry cannot be used after its number has been rebound.</summary>
    [Fact]
    public async Task QueuedOperationRejectsRetiredFidAfterNumberReuse()
    {
        MemoryFilesystem tree = new();
        await using FidTable fids = new(4);
#pragma warning disable CA2000 // The table owns each bound entry; the retired original is disposed below.
        FidEntry original = fids.Bind(new FidEntry(1, tree.Root, Identity.Anonymous("glenda"), ""));
        using FidLease first = await fids.AcquireAsync([1], Ct);
        Task<FidLease> waiting = fids.AcquireAsync([1], Ct).AsTask();
        Assert.False(waiting.IsCompleted);
        Assert.True(fids.Remove(1, out _));
        fids.Bind(new FidEntry(1, tree.Root, Identity.Anonymous("glenda"), ""));
#pragma warning restore CA2000
        await original.DisposeAsync();
        first.Dispose();
        NinePException error = await Assert.ThrowsAsync<NinePException>(async () => await waiting);
        Assert.Equal(Errno.EBADF, error.Error.Errno);
    }

    /// <summary>Clunk and remove cannot dispose a handle whose write has not returned.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingAFileWaitsForItsActiveWrite(bool remove)
    {
        MemoryFilesystem tree = new();
        ObservedFile file = new(90) { HoldWrite = true };
        tree.Root.Add(file);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
        await OpenAsync(client, 1, 2);
        await client.SendAsync(new Twrite(10, 2, 0, "X"u8.ToArray()), Ct);
        await file.WriteEntered.Task.WaitAsync(Ct);
        if (remove)
        {
            await client.SendAsync(new Tremove(11, 2), Ct);
        }
        else
        {
            await client.SendAsync(new Tclunk(11, 2), Ct);
        }

        try
        {
            await client.SendAsync(new Tgetattr(12, 1, GetAttrMask.Basic), Ct);
            byte[] barrier;
            do
            {
                barrier = await client.ReceiveFrameAsync(Ct);
            }
            while (NineP.Protocol.Codec.MessageCodec.PeekTag(barrier) != 12);
            Assert.Equal(0, file.Disposals);
        }
        finally
        {
            file.WriteGate.TrySetResult();
        }

        HashSet<ushort> replies = [];
        while (replies.Count < 2)
        {
            replies.Add(NineP.Protocol.Codec.MessageCodec.PeekTag(await client.ReceiveFrameAsync(Ct)));
        }

        Assert.Contains((ushort)10, replies);
        Assert.Contains((ushort)11, replies);
        Assert.Equal(1, file.Disposals);
    }

    /// <summary>Append's size selection and write are indivisible across connections.</summary>
    [Fact]
    public async Task AppendWritesAcrossConnectionsDoNotOverwriteEachOther()
    {
        MemoryFilesystem tree = new();
        ObservedFile file = new(90) { Append = true, HoldWrite = true };
        tree.Root.Add(file);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using WireClient first = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
        await using WireClient second = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
        await OpenAsync(first, 1, 2);
        await OpenAsync(second, 1, 2);
        await first.SendAsync(new Twrite(10, 2, 0, "X"u8.ToArray()), Ct);
        await file.WriteEntered.Task.WaitAsync(Ct);
        await second.SendAsync(new Twrite(11, 2, 0, "Y"u8.ToArray()), Ct);
        try
        {
            await second.SendAsync(new Tgetattr(12, 1, GetAttrMask.Basic), Ct);
            await second.ReceiveAsync<Rgetattr>(Ct);
            Assert.Equal(1, file.SizeReads);
        }
        finally
        {
            file.WriteGate.TrySetResult();
        }

        await first.ReceiveAsync<Rwrite>(Ct);
        await second.ReceiveAsync<Rwrite>(Ct);
        Assert.Equal("dataXY"u8.ToArray(), file.Backing.Data);
    }

    /// <summary>Bulk clunk commits a complete xattr sink and releases its fid.</summary>
    [Fact]
    public async Task VersionResetCommitsACompleteXattrSink()
    {
        MemoryFilesystem tree = new();
        MemoryFile file = tree.NewFile("observed", Perms.P0666);
        tree.Root.Add(file);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
        await client.AttachAsync(1, Ct);
        await client.WalkAsync(2, 1, 2, ["observed"], Ct);
        await client.SendAsync(new Txattrcreate(3, 2, "user.test", 1, XattrFlags.None), Ct);
        await client.ReceiveAsync<Rxattrcreate>(Ct);
        await client.SendAsync(new Twrite(4, 2, 0, "X"u8.ToArray()), Ct);
        await client.ReceiveAsync<Rwrite>(Ct);
        await client.SendAsync(new Tversion(Constants.NOTAG, 8192, "9P2000.L"), Ct);
        await client.ReceiveAsync<Rversion>(Ct);
        Assert.Equal("X"u8.ToArray(), (await file.GetXattrAsync("user.test", Ct)).ToArray());
    }

    /// <summary>Transport loss performs the same ORCLOSE and exclusive-lock cleanup as explicit clunk.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectFinalizesOpenState(bool removeOnClose)
    {
        MemoryFilesystem tree = new();
        MemoryFile file = tree.NewFile("observed", Perms.P0666);
        file.Exclusive = true;
        tree.Root.Add(file);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using (WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000, 8192, Ct))
        {
            await client.AttachAsync(1, Ct);
            await client.WalkAsync(2, 1, 2, ["observed"], Ct);
            await client.SendAsync(new Topen(3, 2, removeOnClose ? (byte)0x40 : (byte)0), Ct);
            await client.ReceiveAsync<Ropen>(Ct);
        }

        while (file.Opens != 0)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(0, file.Opens);
        Assert.Equal(!removeOnClose, tree.Root.Children.ContainsKey("observed"));
        if (!removeOnClose)
        {
            await using NinePSession next = await harness.ConnectAsync(Dialect.P9_2000);
            await using NinePFid reopened = await next.OpenFileAsync("observed", OpenMode.Read, OpenFlags.None, Ct);
            Assert.Equal(file.Qid, reopened.Qid);
        }
    }

    /// <summary>Disconnected sessions disappear from the active task registry instead of accumulating.</summary>
    [Fact]
    public async Task ListenerRetainsOnlyActiveSessions()
    {
        MemoryTransport transport = new();
        NinePAddress address = new(NinePScheme.Memory, "lifecycle-" + Guid.NewGuid().ToString("N"), 0, "");
        INinePListener listener = await transport.ListenAsync(address, Ct);
        await using ListenerContext context = new(
            listener,
            new ServerOptions { Listen = [address] },
            new ServerMetrics(),
            new OpenState(),
            new AuthThrottle(0, TimeSpan.Zero, TimeProvider.System));
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task accepting = context.AcceptAsync(new MemoryFilesystem(), stopping.Token);
        try
        {
            for (int iteration = 0; iteration < 50; iteration++)
            {
                await using NinePSession client = await NinePClient.ConnectAsync(transport, address,
                    new ClientOptions { Dialects = [Dialect.P9_2000_L], Msize = 8192 }, Ct);
            }

            while (context.ActiveSessionCount != 0)
            {
                await Task.Delay(10, Ct);
            }

            Assert.Equal(0, context.ActiveSessionCount);
        }
        finally
        {
            await stopping.CancelAsync();
            await accepting;
        }
    }

    /// <summary>Forced disposal leaves an uncooperative read's resources alive until it returns.</summary>
    [Fact]
    public async Task ShutdownDefersDisposalUntilAnUncooperativeReadReturns()
    {
        MemoryFilesystem tree = new();
        MemoryFile file = tree.NewFile("observed", Perms.P0666);
        file.Data = "data"u8.ToArray();
        file.DeafReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.Root.Add(file);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
        await client.AttachAsync(1, Ct);
        await client.WalkAsync(2, 1, 2, ["observed"], Ct);
        await client.SendAsync(new Tlopen(3, 2, 0), Ct);
        await client.ReceiveAsync<Rlopen>(Ct);
        await client.SendAsync(new Tread(4, 2, 0, 4), Ct);
        while (file.ReadsStarted == 0)
        {
            await Task.Delay(1, Ct);
        }

        try
        {
            await harness.Server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), Ct);
            Assert.Equal(1, file.Opens);
        }
        finally
        {
            file.DeafReadGate.TrySetResult();
        }

        while (file.Opens != 0)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(0, file.Opens);
    }

    /// <summary>Paths longer than one frame retain ancestry when cloned and ascended in separate requests.</summary>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    [InlineData(Dialect.P9_2000_L)]
    public async Task DeepClonedWalkPreservesAllAncestors(Dialect dialect)
    {
        MemoryFilesystem tree = new();
        MemoryDirectory current = tree.Root;
        for (int depth = 0; depth < 32; depth++)
        {
            MemoryDirectory child = tree.NewDirectory("next", Perms.P0777);
            current.Add(child);
            current = child;
        }

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(dialect);
        string[] segment = Enumerable.Repeat("next", 16).ToArray();
        await session.Messages.WalkAsync(new Twalk(0, session.Root.Fid, 60, segment), Ct);
        await session.Messages.WalkAsync(new Twalk(0, 60, 60, segment), Ct);
        await session.Messages.WalkAsync(new Twalk(0, 60, 61, []), Ct);
        Rwalk result = default;
        for (int depth = 0; depth < 32; depth++)
        {
            result = await session.Messages.WalkAsync(new Twalk(0, 61, 61, [".."]), Ct);
        }

        Assert.Equal(tree.Root.Qid, Assert.Single(result.Wqids));
        Assert.Equal(current.Qid, (await session.Messages.WalkAsync(new Twalk(0, 60, 62, ["..", "next"]), Ct)).Wqids[1]);
    }

    /// <summary>Failure after a successful element returns a prefix without changing either binding.</summary>
    [Theory]
    [InlineData(Dialect.P9_2000, false)]
    [InlineData(Dialect.P9_2000, true)]
    [InlineData(Dialect.P9_2000_u, false)]
    [InlineData(Dialect.P9_2000_u, true)]
    [InlineData(Dialect.P9_2000_L, false)]
    [InlineData(Dialect.P9_2000_L, true)]
    public async Task PartialPermissionFailureDoesNotChangeFids(Dialect dialect, bool inPlace)
    {
        MemoryFilesystem tree = new();
        MemoryDirectory locked = tree.NewDirectory("locked", 0);
        tree.Root.Add(locked);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(dialect);
        await session.Messages.WalkAsync(new Twalk(0, session.Root.Fid, 60, []), Ct);
        Rwalk partial = await session.Messages.WalkAsync(new Twalk(0, 60, inPlace ? 60u : 61u, ["locked", ".."]), Ct);
        Assert.Equal(locked.Qid, Assert.Single(partial.Wqids));
        Assert.Equal(tree.Root.Qid, Assert.Single((await session.Messages.WalkAsync(new Twalk(0, 60, 62, [".."]), Ct)).Wqids));
        if (!inPlace)
        {
            NinePException missing = await Assert.ThrowsAsync<NinePException>(async () =>
                await session.Messages.ClunkAsync(new Tclunk(0, 61), Ct));
            Assert.Equal(Errno.EBADF, missing.Error.Errno);
        }
    }

    /// <summary>Dot-dot cannot start at a regular file, even though the fid remembers its parent.</summary>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    [InlineData(Dialect.P9_2000_L)]
    public async Task DotDotFromAFileIsRejected(Dialect dialect)
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(dialect);
        await using NinePFid file = await session.WalkAsync("hello.txt", Ct);
        NinePException error = await Assert.ThrowsAsync<NinePException>(async () =>
            await session.Messages.WalkAsync(new Twalk(0, file.Fid, 60, [".."]), Ct));
        Assert.Equal(Errno.ENOTDIR, error.Error.Errno);
    }

    /// <summary>A renamed directory keeps the destination's full ancestry for subsequent parent walks.</summary>
    [Fact]
    public async Task RenamedDirectoryAscendsThroughItsNewParents()
    {
        MemoryFilesystem tree = new();
        MemoryDirectory source = tree.NewDirectory("source", Perms.P0777);
        MemoryDirectory outer = tree.NewDirectory("outer", Perms.P0777);
        MemoryDirectory destination = tree.NewDirectory("destination", Perms.P0777);
        tree.Root.Add(source);
        tree.Root.Add(outer);
        outer.Add(destination);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);
        await session.Messages.WalkAsync(new Twalk(0, session.Root.Fid, 60, ["source"]), Ct);
        await session.Messages.WalkAsync(new Twalk(0, session.Root.Fid, 61, ["outer", "destination"]), Ct);
        await session.Messages.RenameAsync(new Trename(0, 60, 61, "moved"), Ct);
        foreach (Qid expected in new[] { destination.Qid, outer.Qid, tree.Root.Qid })
        {
            Rwalk parent = await session.Messages.WalkAsync(new Twalk(0, 60, 60, [".."]), Ct);
            Assert.Equal(expected, Assert.Single(parent.Wqids));
        }
    }

    /// <summary>Renames rebase independent aliases and descendant fids on other connections.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenamedAncestorRebasesExistingAliasesAcrossConnections(bool renameAt)
    {
        MemoryFilesystem tree = new();
        MemoryDirectory source = tree.NewDirectory("source", Perms.P0777);
        MemoryDirectory child = tree.NewDirectory("child", Perms.P0777);
        MemoryDirectory destination = tree.NewDirectory("destination", Perms.P0777);
        tree.Root.Add(source);
        tree.Root.Add(destination);
        source.Add(child);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession aliases = await harness.ConnectAsync(Dialect.P9_2000_L);
        await using NinePSession mover = await harness.ConnectAsync(Dialect.P9_2000_L);
        await aliases.Messages.WalkAsync(new Twalk(0, aliases.Root.Fid, 60, ["source"]), Ct);
        await aliases.Messages.WalkAsync(new Twalk(0, 60, 61, []), Ct);
        await aliases.Messages.WalkAsync(new Twalk(0, aliases.Root.Fid, 62, ["source"]), Ct);
        await aliases.Messages.WalkAsync(new Twalk(0, 60, 63, ["child"]), Ct);
        await aliases.Messages.ClunkAsync(new Tclunk(0, 60), Ct);
        await mover.Messages.WalkAsync(new Twalk(0, mover.Root.Fid, 60, ["source"]), Ct);
        await mover.Messages.WalkAsync(new Twalk(0, mover.Root.Fid, 61, ["destination"]), Ct);
        if (renameAt)
        {
            await mover.Messages.RenameatAsync(new Trenameat(0, mover.Root.Fid, "source", 61, "moved"), Ct);
        }
        else
        {
            await mover.Messages.RenameAsync(new Trename(0, 60, 61, "moved"), Ct);
        }

        foreach (uint fid in new uint[] { 61, 62, 63 })
        {
            if (fid == 63)
            {
                Rwalk sourceParent = await aliases.Messages.WalkAsync(new Twalk(0, fid, fid, [".."]), Ct);
                Assert.Equal(source.Qid, Assert.Single(sourceParent.Wqids));
            }

            Rwalk parent = await aliases.Messages.WalkAsync(new Twalk(0, fid, fid, [".."]), Ct);
            Assert.Equal(destination.Qid, Assert.Single(parent.Wqids));
            Rwalk root = await aliases.Messages.WalkAsync(new Twalk(0, fid, fid, [".."]), Ct);
            Assert.Equal(tree.Root.Qid, Assert.Single(root.Wqids));
        }
    }

    /// <summary>A rename cannot grant a restricted attach access to ancestors outside its root.</summary>
    [Fact]
    public async Task RenameOutsideRestrictedAttachClampsParentWalkAndKeepsRemovalLocation()
    {
        MemoryFilesystem tree = new();
        MemoryDirectory jail = tree.NewDirectory("jail", Perms.P0777);
        MemoryDirectory source = tree.NewDirectory("source", Perms.P0777);
        MemoryDirectory destination = tree.NewDirectory("destination", Perms.P0777);
        tree.Root.Add(jail);
        tree.Root.Add(destination);
        jail.Add(source);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree,
            filesystem: new ScopedFilesystem(tree.Root, jail));
        await using NinePSession restricted = await harness.ConnectAsync(Dialect.P9_2000_L,
            options => options with { Aname = "restricted" });
        await using NinePSession mover = await harness.ConnectAsync(Dialect.P9_2000_L);
        await restricted.Messages.WalkAsync(new Twalk(0, restricted.Root.Fid, 60, ["source"]), Ct);
        await restricted.Messages.WalkAsync(new Twalk(0, 60, 61, []), Ct);
        await mover.Messages.WalkAsync(new Twalk(0, mover.Root.Fid, 60, ["jail"]), Ct);
        await mover.Messages.WalkAsync(new Twalk(0, mover.Root.Fid, 61, ["destination"]), Ct);
        await mover.Messages.RenameatAsync(new Trenameat(0, 60, "source", 61, "moved"), Ct);
        Rwalk parent = await restricted.Messages.WalkAsync(new Twalk(0, 60, 60, ["..", ".."]), Ct);
        Assert.All(parent.Wqids, qid => Assert.Equal(jail.Qid, qid));
        await restricted.Messages.RemoveAsync(new Tremove(0, 61), Ct);
        Assert.Null(await destination.LookupAsync("moved", Ct));
    }

    /// <summary>Unrelated walks remain available and a raced lookup retries after rename.</summary>
    [Fact]
    public async Task SlowLookupDoesNotBlockOtherClientsAndRevalidatesAfterRename()
    {
        MemoryFilesystem tree = new();
        MemoryDirectory backing = tree.NewDirectory("slow", Perms.P0777);
        MemoryDirectory child = tree.NewDirectory("child", Perms.P0777);
        MemoryDirectory destination = tree.NewDirectory("destination", Perms.P0777);
        SlowDirectory slow = new(backing);
        tree.Root.Add(slow);
        tree.Root.Add(destination);
        backing.Add(child);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession waiting = await harness.ConnectAsync(Dialect.P9_2000_L);
        await using NinePSession mover = await harness.ConnectAsync(Dialect.P9_2000_L);
        Task<Rwalk> lookup = waiting.Messages.WalkAsync(new Twalk(0, waiting.Root.Fid, 60, ["slow", "child"]), Ct).AsTask();
        try
        {
            await slow.Entered.Task.WaitAsync(Ct);
            await mover.Messages.WalkAsync(new Twalk(0, mover.Root.Fid, 60, ["slow"]), Ct)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(1), Ct);
            await mover.Messages.WalkAsync(new Twalk(0, mover.Root.Fid, 61, ["destination"]), Ct)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(1), Ct);
            await mover.Messages.RenameatAsync(new Trenameat(0, 60, "child", 61, "moved"), Ct)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(1), Ct);
        }
        finally
        {
            slow.Release.TrySetResult();
        }

        Rwalk partial = await lookup;
        Assert.Equal(slow.Qid, Assert.Single(partial.Wqids));
        await waiting.Messages.WalkAsync(new Twalk(0, waiting.Root.Fid, 60, ["destination", "moved"]), Ct);
        Assert.True(slow.LookupCalls >= 3);
    }

    /// <summary>Thrown lookup errors have the same partial-walk semantics as a missing child.</summary>
    [Theory]
    [InlineData(Dialect.P9_2000, false)]
    [InlineData(Dialect.P9_2000, true)]
    [InlineData(Dialect.P9_2000_u, false)]
    [InlineData(Dialect.P9_2000_u, true)]
    [InlineData(Dialect.P9_2000_L, false)]
    [InlineData(Dialect.P9_2000_L, true)]
    public async Task ThrownLookupErrorsPreserveWalkBindings(Dialect dialect, bool inPlace)
    {
        MemoryFilesystem tree = new();
        FailingDirectory failing = new(90);
        tree.Root.Add(failing);
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(dialect);
        await session.Messages.WalkAsync(new Twalk(0, session.Root.Fid, 60, []), Ct);
        Rwalk partial = await session.Messages.WalkAsync(new Twalk(0, 60, inPlace ? 60u : 61u, ["failing", "child"]), Ct);
        Assert.Equal(failing.Qid, Assert.Single(partial.Wqids));
        Rwalk unchanged = await session.Messages.WalkAsync(new Twalk(0, 60, 62, [".."]), Ct);
        Assert.Equal(tree.Root.Qid, Assert.Single(unchanged.Wqids));
        await session.Messages.WalkAsync(new Twalk(0, 60, 63, ["failing"]), Ct);
        NinePException firstStep = await Assert.ThrowsAsync<NinePException>(async () =>
            await session.Messages.WalkAsync(new Twalk(0, 63, 64, ["child"]), Ct));
        Assert.Equal(Errno.ENOENT, firstStep.Error.Errno);
        if (!inPlace)
        {
            NinePException absent = await Assert.ThrowsAsync<NinePException>(async () =>
                await session.Messages.ClunkAsync(new Tclunk(0, 61), Ct));
            Assert.Equal(Errno.EBADF, absent.Error.Errno);
        }
    }

    /// <summary>Incomplete xattr data is refused during reset without abandoning subsequent fids.</summary>
    [Fact]
    public async Task IncompleteXattrResetLogsFailureAndContinuesCleanup()
    {
        MemoryFilesystem tree = new();
        MemoryFile sink = tree.NewFile("sink", Perms.P0666);
        MemoryFile ordinary = tree.NewFile("ordinary", Perms.P0666);
        tree.Root.Add(sink);
        tree.Root.Add(ordinary);
        RecordingLogger logger = new();
        await using ServerHarness harness = await ServerHarness.StartAsync(options => options with { Logger = logger }, tree);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
        await client.AttachAsync(1, Ct);
        await client.WalkAsync(2, 1, 2, ["sink"], Ct);
        await client.WalkAsync(3, 1, 3, ["ordinary"], Ct);
        await client.SendAsync(new Txattrcreate(4, 2, "user.partial", 2, XattrFlags.None), Ct);
        await client.ReceiveAsync<Rxattrcreate>(Ct);
        await client.SendAsync(new Twrite(5, 2, 0, "X"u8.ToArray()), Ct);
        await client.ReceiveAsync<Rwrite>(Ct);
        await client.SendAsync(new Tversion(Constants.NOTAG, 8192, "9P2000.L"), Ct);
        await client.ReceiveAsync<Rversion>(Ct);
        Assert.True(sink.WasClunked);
        Assert.True(ordinary.WasClunked);
        Assert.Contains(logger.Warnings, text => text.Contains("finalized", StringComparison.Ordinal));
        NinePException absent = await Assert.ThrowsAsync<NinePException>(async () => await sink.GetXattrAsync("user.partial", Ct));
        Assert.Equal(Errno.ENODATA, absent.Error.Errno);
    }

    /// <summary>A failed logging sink cannot strand the remaining fids during detached cleanup.</summary>
    [Fact]
    public async Task CleanupContinuesWhenReportingAHandlerFailureAlsoThrows()
    {
        MemoryFilesystem tree = new();
        MemoryFile refusing = tree.NewFile("refusing", Perms.P0666);
        refusing.ClunkFailure = NinePError.FromErrno(Errno.EIO);
        MemoryFile ordinary = tree.NewFile("ordinary", Perms.P0666);
        tree.Root.Add(refusing);
        tree.Root.Add(ordinary);
        FailingCleanupLogger logger = new();
        await using ServerHarness harness = await ServerHarness.StartAsync(options => options with { Logger = logger }, tree);
        await using (WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct))
        {
            await client.AttachAsync(1, Ct);
            await client.WalkAsync(2, 1, 2, ["refusing"], Ct);
            await client.WalkAsync(3, 1, 3, ["ordinary"], Ct);
        }

        while (harness.Server.Counters.ConnectionsOpen != 0)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(refusing.WasClunked);
        Assert.True(ordinary.WasClunked);
        Assert.Equal(1, logger.Attempts);
        Assert.Equal(0, harness.Server.Counters.ConnectionsOpen);
    }

    private static async Task OpenAsync(WireClient client, uint root, uint fid)
    {
        await client.AttachAsync(root, Ct);
        await client.WalkAsync(2, root, fid, ["observed"], Ct);
        await client.SendAsync(new Tlopen(3, fid, 1), Ct);
        await client.ReceiveAsync<Rlopen>(Ct);
    }

    private sealed class ScopedFilesystem(IDirectoryHandler root, IDirectoryHandler restricted) : IFilesystem
    {
        public ValueTask<IDirectoryHandler> AttachAsync(Identity identity, string aname,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(aname == "restricted" ? restricted : root);
    }

    private sealed class SlowDirectory(MemoryDirectory backing)
        : MemoryNode(backing.Name, FileKind.Directory, Perms.P0777, backing.Qid.Path), IDirectoryHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LookupCalls;

        public async ValueTask<IHandler?> LookupAsync(string name, CancellationToken cancellationToken = default)
        {
            IHandler? child = await backing.LookupAsync(name, cancellationToken);
            if (Interlocked.Increment(ref LookupCalls) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return child;
        }

        public ValueTask<IHandler> CreateAsync(CreateRequest request, CancellationToken cancellationToken = default) =>
            backing.CreateAsync(request, cancellationToken);
        public ValueTask<DirectoryListing> ReadDirAsync(ulong cursor, int maxEntries, CancellationToken cancellationToken = default) =>
            backing.ReadDirAsync(cursor, maxEntries, cancellationToken);
        public ValueTask RemoveAsync(string name, FileKind kind, CancellationToken cancellationToken = default) =>
            backing.RemoveAsync(name, kind, cancellationToken);
        public ValueTask RenameAsync(string name, IDirectoryHandler newParent, string newName,
            CancellationToken cancellationToken = default) => backing.RenameAsync(name, newParent, newName, cancellationToken);
        public ValueTask LinkAsync(string name, IHandler target, CancellationToken cancellationToken = default) =>
            backing.LinkAsync(name, target, cancellationToken);
    }

    private sealed class ObservedFile(ulong path)
        : MemoryNode("observed", FileKind.File, Perms.P0666, path), IFileHandler, IXattrHandler
    {
        public MemoryFile Backing { get; } = new("observed", Perms.P0666, path) { Data = "data"u8.ToArray() };
        public bool Append { get; init; }
        public bool HoldWrite { get; init; }
        public int XattrReads { get; private set; }
        public int SizeReads { get; private set; }
        public int Disposals { get; private set; }
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Qid IHandler.Qid => Backing.Qid with { Type = Append ? QidType.QTAPPEND : QidType.QTFILE };

        async ValueTask<Attr> IHandler.GetAttrAsync(CancellationToken cancellationToken)
        {
            Attr attr = await Backing.GetAttrAsync(cancellationToken);
            return attr with
            {
                Perm = Perm,
                Flags = Append ? FileFlags.Append : FileFlags.None,
                Qid = ((IHandler)this).Qid
            };
        }

        public async ValueTask<IOpenFile> OpenAsync(
            OpenMode mode, OpenFlags flags, CancellationToken cancellationToken = default) =>
            new Handle(this, await Backing.OpenAsync(mode, flags, cancellationToken));

        public ValueTask<ReadOnlyMemory<byte>> ListXattrAsync(CancellationToken cancellationToken = default)
        {
            XattrReads++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>("user.secret\0"u8.ToArray());
        }

        public ValueTask<ReadOnlyMemory<byte>> GetXattrAsync(string name, CancellationToken cancellationToken = default)
        {
            XattrReads++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>("secret"u8.ToArray());
        }

        public ValueTask SetXattrAsync(string name, ReadOnlyMemory<byte> value, XattrFlags flags,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RemoveXattrAsync(string name, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        private sealed class Handle(ObservedFile file, IOpenFile inner) : IOpenFile
        {
            public ValueTask<int> ReadAsync(ulong offset, Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                inner.ReadAsync(offset, buffer, cancellationToken);

            public async ValueTask<int> WriteAsync(ulong offset, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
            {
                file.WriteEntered.TrySetResult();
                if (file.HoldWrite)
                {
                    await file.WriteGate.Task.WaitAsync(cancellationToken);
                }

                Assert.Equal(0, file.Disposals);
                return await inner.WriteAsync(offset, value, cancellationToken);
            }

            public ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default)
            {
                file.SizeReads++;
                return inner.GetSizeAsync(cancellationToken);
            }

            public async ValueTask DisposeAsync()
            {
                file.Disposals++;
                await inner.DisposeAsync();
            }
        }
    }

    private sealed class FailingDirectory(ulong path)
        : MemoryNode("failing", FileKind.Directory, Perms.P0777, path), IDirectoryHandler
    {
        public ValueTask<IHandler?> LookupAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NinePException(NinePError.FromErrno(Errno.ENOENT));

        public ValueTask<DirectoryListing> ReadDirAsync(ulong cursor, int max, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DirectoryListing([], 0, true));

        public ValueTask<IHandler> CreateAsync(CreateRequest request, CancellationToken cancellationToken = default) =>
            throw new NinePException(NinePError.FromErrno(Errno.EOPNOTSUPP));

        public ValueTask RemoveAsync(string name, FileKind kind, CancellationToken cancellationToken = default) =>
            throw new NinePException(NinePError.FromErrno(Errno.EOPNOTSUPP));

        public ValueTask RenameAsync(string oldName, IDirectoryHandler newParent, string newName,
            CancellationToken cancellationToken = default) =>
            throw new NinePException(NinePError.FromErrno(Errno.EOPNOTSUPP));
    }

    private sealed class FailingCleanupLogger : ILogger
    {
        public int Attempts { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 3007)
            {
                Attempts++;
                throw new InvalidOperationException("test cleanup sink failure");
            }
        }
    }
}
