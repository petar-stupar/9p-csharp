using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// clunk(5) and remove(5): the fid is freed whatever the reply says. A server that kept the fid
/// alive after a failed remove would leave the client holding a number it may legally reuse.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class ClunkRemoveTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>Rule 32: a clunked fid is gone even when the removal it triggered failed.</summary>
    [Fact]
    public async Task FidGoneEvenWhenRemoveFails()
    {
        MemoryFilesystem tree = new();
        MemoryDirectory full = tree.NewDirectory("full", 0x1ED);
        full.Add(tree.NewFile("child", 0x1A4));
        tree.Root.Add(full);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid directory = await session.WalkAsync("full", Ct);
        uint fid = directory.Fid;

        // Removing a directory that is not empty fails, and the fid is freed all the same.
        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await directory.RemoveAsync(Ct));
        Assert.Equal(Errno.ENOTEMPTY, refusal.Error.Errno);

        NinePException unknown = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages.ClunkAsync(new Tclunk(0, fid), Ct));
        Assert.Equal(Errno.EBADF, unknown.Error.Errno);

        // Nothing was removed, either: the directory and its child are still there.
        Assert.True(tree.Root.Children.ContainsKey("full"));
    }

    /// <summary>
    /// clunk(5): a clunk whose <c>ORCLOSE</c> removal failed still frees the fid. The file is
    /// taken out of the tree behind the server's back after the open, so the removal the clunk
    /// triggers finds nothing — and the fid is gone regardless.
    /// </summary>
    [Fact]
    public async Task OrcloseFailureStillFreesTheFid()
    {
        MemoryFilesystem tree = new();
        tree.Root.Add(tree.NewFile("vanishing", 0x1B6));

        // 9P2000: ORCLOSE is a mode bit there, and .L has no open(2) flag that means it.
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid file = await session
            .OpenFileAsync("vanishing", OpenMode.Read, OpenFlags.RemoveOnClose, Ct);
        uint fid = file.Fid;

        Assert.True(tree.Root.Detach("vanishing"));

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages.ClunkAsync(new Tclunk(0, fid), Ct));
        Assert.Equal(Errno.ENOENT, refusal.Error.Errno);

        // The clunk was answered with an error and the fid is gone all the same.
        NinePException unknown = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages.ClunkAsync(new Tclunk(0, fid), Ct));
        Assert.Equal(Errno.EBADF, unknown.Error.Errno);
    }

    /// <summary>remove(5): a successful remove frees the fid and takes the file with it.</summary>
    [Fact]
    public async Task RemoveTakesTheFileAndTheFid()
    {
        MemoryFilesystem tree = new();
        tree.Root.Add(tree.NewFile("doomed", 0x1B6));

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        await session.RemoveAsync("doomed", Ct);

        Assert.False(tree.Root.Children.ContainsKey("doomed"));
    }

    /// <summary>
    /// Rule 26: a handler's clunk-time error <b>is</b> the reply. It used to be swallowed and the
    /// client answered <c>Rclunk</c>, so a handler whose flush of the last write failed had no way
    /// to say so — the one moment it can. The fid is freed regardless, which is clunk(5).
    /// <b>Mutation:</b> swallow the <c>NinePException</c> in <c>FidTable.ReleaseAsync</c> again
    /// and this fails.
    /// </summary>
    /// <param name="dialect">The dialect the clunk goes out in.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    [InlineData(Dialect.P9_2000_L)]
    public async Task AHandlersClunkErrorIsTheReply(Dialect dialect)
    {
        MemoryFilesystem tree = new();
        MemoryFile file = tree.NewFile("unflushable", 0x1B6);
        file.ClunkFailure = NinePError.FromErrno(Errno.EIO);
        tree.Root.Add(file);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(dialect);

        NinePFid fid = await session.WalkAsync("unflushable", Ct);
        uint number = fid.Fid;

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages.ClunkAsync(new Tclunk(0, number), Ct));
        Assert.Equal(Errno.EIO, refusal.Error.Errno);

        // clunk(5): the fid is gone all the same, and the handler was told.
        Assert.True(file.WasClunked);

        NinePException unknown = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages.ClunkAsync(new Tclunk(0, number), Ct));
        Assert.Equal(Errno.EBADF, unknown.Error.Errno);
    }

    /// <summary>
    /// Rule 26 on the remove path: the removal itself succeeded, so the file is gone and the fid
    /// is gone, and the reply is still the handler's refusal rather than an <c>Rremove</c> that
    /// hides it.
    /// </summary>
    [Fact]
    public async Task AHandlersClunkErrorSurvivesARemove()
    {
        MemoryFilesystem tree = new();
        MemoryFile file = tree.NewFile("doomed", 0x1B6);
        file.ClunkFailure = NinePError.FromErrno(Errno.EIO);
        tree.Root.Add(file);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid fid = await session.WalkAsync("doomed", Ct);
        uint number = fid.Fid;

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages.RemoveAsync(new Tremove(0, number), Ct));
        Assert.Equal(Errno.EIO, refusal.Error.Errno);

        Assert.False(tree.Root.Children.ContainsKey("doomed"));

        NinePException unknown = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages.ClunkAsync(new Tclunk(0, number), Ct));
        Assert.Equal(Errno.EBADF, unknown.Error.Errno);
    }

    /// <summary>
    /// Rule 26 has one place with nowhere to put the error, and it is still not a silent success:
    /// the fids a mid-session <c>Tversion</c> clunks (§5.1) are answered by one <c>Rversion</c>,
    /// so a handler that refuses one of those is dropped there rather than allowed to abandon the
    /// fids after it in the table.
    /// </summary>
    [Fact]
    public async Task ARefusedClunkDuringASessionResetDoesNotStrandTheRest()
    {
        MemoryFilesystem tree = new();
        MemoryFile refusing = tree.NewFile("refusing", 0x1B6);
        refusing.ClunkFailure = NinePError.FromErrno(Errno.EIO);
        tree.Root.Add(refusing);
        MemoryFile ordinary = tree.Root.Add(tree.NewFile("ordinary", 0x1B6));

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using WireClient client = await WireClient
            .ConnectAsync(harness, Dialect.P9_2000_L, cancellationToken: Ct);

        await client.AttachAsync(1, Ct);
        await client.WalkAsync(2, 1, 2, ["refusing"], Ct);
        await client.WalkAsync(3, 1, 3, ["ordinary"], Ct);

        // §5.1: a Tversion mid-session clunks every fid, and the reset finishes before the
        // Rversion goes out — so by the time this returns, every handler has been told.
        await client.SendAsync(
            new Tversion(Constants.NOTAG, 8192, "9P2000.L"), Dialect.P9_2000, Ct);
        await client.ReceiveAsync<Rversion>(Dialect.P9_2000, Ct);

        Assert.True(refusing.WasClunked);
        Assert.True(ordinary.WasClunked);
    }

    /// <summary>A clunk of a fid the connection does not hold is an unknown fid, not a crash.</summary>
    [Fact]
    public async Task ClunkOfAnUnknownFidIsEbadf()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePException unknown = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages.ClunkAsync(new Tclunk(0, 9999), Ct));

        Assert.Equal(Errno.EBADF, unknown.Error.Errno);
    }
}
