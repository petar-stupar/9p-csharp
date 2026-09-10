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
        MemoryDirectory closed = tree.NewDirectory("closed", 0x180);
        closed.Owner = "root";
        closed.Uid = 0;
        closed.Add(tree.NewFile("inside", 0x1B6));
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
        MemoryDirectory readOnly = tree.NewDirectory("ro", 0x16D);
        readOnly.Owner = "root";
        readOnly.Uid = 0;
        tree.Root.Add(readOnly);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePException denied = await Assert.ThrowsAsync<NinePException>(
            async () => await session.MkdirAsync("ro/nope", 0x1ED, Ct));

        Assert.Equal(Errno.EACCES, denied.Error.Errno);
        Assert.Empty(readOnly.Children);
    }

    /// <summary>remove(5): the removal needs write permission in the parent, not on the file.</summary>
    [Fact]
    public async Task RemoveNeedsWriteInTheParent()
    {
        MemoryFilesystem tree = new();
        MemoryDirectory guarded = tree.NewDirectory("guarded", 0x16D);
        guarded.Owner = "root";
        guarded.Uid = 0;
        guarded.Add(tree.NewFile("kept", 0x1FF));
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
        MemoryFile other = tree.NewFile("theirs", 0x1B6);
        other.Owner = "root";
        other.Uid = 0;
        tree.Root.Add(other);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid file = await session.WalkAsync("theirs", Ct);
        await using (file.ConfigureAwait(false))
        {
            NinePException denied = await Assert.ThrowsAsync<NinePException>(
                async () => await file.SetAttrAsync(new SetAttr { Perm = 0x1FF }, Ct));

            Assert.Equal(Errno.EPERM, denied.Error.Errno);
            Assert.Equal(0x1B6u, other.Perm);
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
}
