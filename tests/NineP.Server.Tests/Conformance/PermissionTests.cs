using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// Permission checks run in the core, against the fid's implicit identity, <b>before</b> the
/// handler is called (architecture §4, reference §5.2). A handler that forgot to check is still
/// not reachable without permission.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class PermissionTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// The core denies an open the identity has no bits for, and the handler records that it was
    /// never invoked.
    /// <b>Mutation:</b> deleting the <c>PermissionChecker.Require</c> call from
    /// <c>Dispatcher.OpenFidAsync</c> lets the open through and this test fails on both counts.
    /// </summary>
    [Fact]
    public async Task CoreDeniesBeforeHandlerIsCalled()
    {
        MemoryFilesystem tree = new();
        MemoryFile secret = tree.NewFile("secret", 0);
        secret.Owner = "root";
        secret.Uid = 0;
        tree.Root.Add(secret);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePException denied = await Assert.ThrowsAsync<NinePException>(
            async () => await session.OpenFileAsync("secret", OpenMode.Read, OpenFlags.None, Ct));

        Assert.Equal(Errno.EACCES, denied.Error.Errno);
        Assert.Equal(0, secret.Opens);
    }

    /// <summary>§5.4: a walk needs search permission on every directory it traverses.</summary>
    [Fact]
    public async Task WalkNeedsSearchPermission()
    {
        MemoryFilesystem tree = new();
        MemoryDirectory closed = tree.NewDirectory("closed", Perms.P0600);
        closed.Owner = "root";
        closed.Uid = 0;
        closed.Add(tree.NewFile("inside", Perms.P0666));
        tree.Root.Add(closed);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        Rwalk partial = await session.Messages.WalkAsync(
            new Twalk(0, session.Root.Fid, 70, ["closed", "inside"]), Ct);
        Assert.Single(partial.Wqids);
        await using NinePFid source = await session.WalkAsync("closed", Ct);
        NinePException denied = await Assert.ThrowsAsync<NinePException>(async () =>
            await session.Messages.WalkAsync(new Twalk(0, source.Fid, 70, ["inside"]), Ct));
        Assert.Equal(Errno.EACCES, denied.Error.Errno);
    }

    /// <summary>§5.5: a create needs write permission on the directory it creates in.</summary>
    [Fact]
    public async Task CreateNeedsWriteOnTheDirectory()
    {
        MemoryFilesystem tree = new();
        MemoryDirectory readOnly = tree.NewDirectory("ro", Perms.P0555);
        readOnly.Owner = "root";
        readOnly.Uid = 0;
        tree.Root.Add(readOnly);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePException denied = await Assert.ThrowsAsync<NinePException>(
            async () => await session.MkdirAsync("ro/nope", Perms.P0755, Ct));

        Assert.Equal(Errno.EACCES, denied.Error.Errno);
        Assert.Empty(readOnly.Children);
    }

    /// <summary>remove(5): the removal needs write permission in the parent, not on the file.</summary>
    [Fact]
    public async Task RemoveNeedsWriteInTheParent()
    {
        MemoryFilesystem tree = new();
        MemoryDirectory guarded = tree.NewDirectory("guarded", Perms.P0555);
        guarded.Owner = "root";
        guarded.Uid = 0;
        guarded.Add(tree.NewFile("kept", Perms.P0777));
        tree.Root.Add(guarded);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid file = await session.WalkAsync("guarded/kept", Ct);
        NinePException denied = await Assert.ThrowsAsync<NinePException>(
            async () => await file.RemoveAsync(Ct));

        Assert.Equal(Errno.EACCES, denied.Error.Errno);
        Assert.True(guarded.Children.ContainsKey("kept"));
    }

    /// <summary>stat(5): only the owner may change mode, group or times.</summary>
    [Fact]
    public async Task OnlyTheOwnerMayChangeMode()
    {
        MemoryFilesystem tree = new();
        MemoryFile other = tree.NewFile("theirs", Perms.P0666);
        other.Owner = "root";
        other.Uid = 0;
        tree.Root.Add(other);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid file = await session.WalkAsync("theirs", Ct);
        await using (file.ConfigureAwait(false))
        {
            NinePException denied = await Assert.ThrowsAsync<NinePException>(
                async () => await file.SetAttrAsync(new SetAttr { Perm = Perms.P0777 }, Ct));

            Assert.Equal(Errno.EPERM, denied.Error.Errno);
            Assert.Equal(Perms.P0666, other.Perm);
        }
    }

    /// <summary>reference §5.2: the identity of an attach is what the authenticator produced.</summary>
    [Fact]
    public async Task AttachIdentityReachesTheFilesystem()
    {
        MemoryFilesystem tree = new();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        Assert.Equal("glenda", tree.LastIdentity?.User);
        Assert.Equal(string.Empty, tree.LastAname);

        // A filesystem that refuses an attach is answered with its own error, not a crash.
        tree.RefuseAttach = NinePError.FromErrno(Errno.EACCES);
        NinePException refused = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages
                .AttachAsync(new Tattach(0, 90, Constants.NOFID, "glenda", "", Constants.NONUNAME), Ct));

        Assert.Equal(Errno.EACCES, refused.Error.Errno);
    }

    /// <summary>
    /// Rule 43: a timestamp the client asks the server to fill from its own clock is granted on
    /// write permission, not ownership -- utimensat(2) makes ownership sufficient, not necessary.
    /// This is the shape a truncating redirect arrives in: opening with <c>O_TRUNC</c> updates
    /// mtime, and v9fs sends size and an unset-valued mtime as one <c>Tsetattr</c>. Folding "to
    /// now" in with the owner-only fields refused every <c>&gt;</c> by a non-owner, and on a
    /// default v9fs mount -- which attaches as <c>nobody</c> with uid -1, an identity that can
    /// never equal any owner -- that is every client on the mount.
    /// <b>Mutation:</b> putting <c>update.MTimeToNow</c> back into the owner-only condition in
    /// <c>Dispatcher.ApplyAsync</c> makes this fail with EPERM.
    /// </summary>
    [Fact]
    public async Task ANonOwnerWithWritePermissionMayTruncateAndStampTheTime()
    {
        MemoryFilesystem tree = new();
        MemoryFile shared = tree.NewFile("ctl", Perms.P0666);
        shared.Owner = "root";
        shared.Uid = 0;
        shared.Data = [1, 2, 3, 4];
        tree.Root.Add(shared);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);
        await using NinePFid fid = await session.WalkAsync("ctl", Ct);

        await session.Messages.SetattrAsync(
            new Tsetattr(0, fid.Fid, SetAttrMask.Size | SetAttrMask.MTime, 0, 0, 0, 0, default, default), Ct);

        Assert.True(shared.LastUpdate?.MTimeToNow);
        Assert.Equal(0ul, shared.LastUpdate?.Size);
        Assert.Empty(shared.Data);
    }

    /// <summary>
    /// Rule 43's other half: write permission is what grants it, so an identity without the write
    /// bit is still refused -- and refused EACCES, the errno utimensat(2) names for this case,
    /// rather than the EPERM it reserves for an explicit time.
    /// </summary>
    [Fact]
    public async Task AServerStampedTimeStillNeedsTheWriteBit()
    {
        MemoryFilesystem tree = new();
        MemoryFile readOnly = tree.NewFile("ro", Perms.P0444);
        readOnly.Owner = "root";
        readOnly.Uid = 0;
        tree.Root.Add(readOnly);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);
        await using NinePFid fid = await session.WalkAsync("ro", Ct);

        NinePException denied = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages.SetattrAsync(
                new Tsetattr(0, fid.Fid, SetAttrMask.MTime, 0, 0, 0, 0, default, default), Ct));

        Assert.Equal(Errno.EACCES, denied.Error.Errno);
        Assert.Null(readOnly.LastUpdate);
    }

    /// <summary>
    /// Rule 43 moves only the server-stamped times. An <b>explicit</b> time is the file's identity
    /// the way its mode is, utimensat(2) reserves it to the owner, and a mode as open as 0666 does
    /// not buy it -- otherwise any writer could backdate a file it does not own.
    /// </summary>
    [Theory]
    [InlineData(SetAttrMask.MTime | SetAttrMask.MTimeSet)]
    [InlineData(SetAttrMask.ATime | SetAttrMask.ATimeSet)]
    public async Task AnExplicitTimeStaysTheOwnersAloneHoweverOpenTheModeIs(SetAttrMask mask)
    {
        MemoryFilesystem tree = new();
        MemoryFile shared = tree.NewFile("open", Perms.P0666);
        shared.Owner = "root";
        shared.Uid = 0;
        tree.Root.Add(shared);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);
        await using NinePFid fid = await session.WalkAsync("open", Ct);

        NinePException denied = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages.SetattrAsync(new Tsetattr(
                0, fid.Fid, mask, 0, 0, 0, 0, new TimeSpec(1, 0), new TimeSpec(1, 0)), Ct));

        Assert.Equal(Errno.EPERM, denied.Error.Errno);
        Assert.Null(shared.LastUpdate);
    }

    /// <summary>The owner keeps every one of them, which is what rule 43 widens rather than moves.</summary>
    [Fact]
    public async Task TheOwnerMayStillSetAnExplicitTime()
    {
        MemoryFilesystem tree = new();
        MemoryFile mine = tree.NewFile("mine", Perms.P0600);
        tree.Root.Add(mine);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);
        await using NinePFid fid = await session.WalkAsync("mine", Ct);

        await session.Messages.SetattrAsync(new Tsetattr(
            0, fid.Fid, SetAttrMask.MTime | SetAttrMask.MTimeSet, 0, 0, 0, 0, default,
            new TimeSpec(1_700_000_000, 0)), Ct);

        Assert.Equal(new TimeSpec(1_700_000_000, 0), mine.LastUpdate?.MTime);
    }
}
