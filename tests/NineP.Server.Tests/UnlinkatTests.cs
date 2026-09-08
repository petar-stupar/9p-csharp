using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Server.Internal;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests;

/// <summary>
/// <c>Tunlinkat</c> and its flag word (reference §5.9 and §8 rule 20). <c>AT_REMOVEDIR</c> is how
/// the client says which of <c>rmdir(2)</c> and <c>unlink(2)</c> it meant; the flags used to be
/// decoded and never read, so the server answered both with whichever the name happened to be.
/// </summary>
public sealed class UnlinkatTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 20: <c>AT_REMOVEDIR</c> is required for a directory. Without it the removal is an
    /// <c>unlink(2)</c> of a directory, which is <c>EISDIR</c>.
    /// <b>Mutation:</b> delete the kind check in <c>Dispatcher.UnlinkatAsync</c> and this fails.
    /// </summary>
    [Fact]
    public async Task DirectoryWithoutRemovedirIsEisdir()
    {
        MemoryFilesystem tree = Tree();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages
                .UnlinkatAsync(new Tunlinkat(0, session.Root.Fid, "sub", 0), Ct));

        Assert.Equal(Errno.EISDIR, refusal.Error.Errno);
        Assert.True(tree.Root.Children.ContainsKey("sub"));
    }

    /// <summary>Rule 20: with the flag, the same removal is the <c>rmdir(2)</c> it asked for.</summary>
    [Fact]
    public async Task DirectoryWithRemovedirIsRemoved()
    {
        MemoryFilesystem tree = Tree();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        await session.Messages
            .UnlinkatAsync(new Tunlinkat(0, session.Root.Fid, "sub", LinuxAbi.AT_REMOVEDIR), Ct);

        Assert.False(tree.Root.Children.ContainsKey("sub"));
    }

    /// <summary>
    /// Rule 20: <c>AT_REMOVEDIR</c> is refused for anything that is not a directory, because an
    /// <c>rmdir(2)</c> of a file is not the operation the client asked for.
    /// </summary>
    [Fact]
    public async Task FileWithRemovedirIsEnotdir()
    {
        MemoryFilesystem tree = Tree();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages.UnlinkatAsync(
                new Tunlinkat(0, session.Root.Fid, "hello.txt", LinuxAbi.AT_REMOVEDIR), Ct));

        Assert.Equal(Errno.ENOTDIR, refusal.Error.Errno);
        Assert.True(tree.Root.Children.ContainsKey("hello.txt"));
    }

    /// <summary>Rule 20: a file with no flags is the plain <c>unlink(2)</c>, and it works.</summary>
    [Fact]
    public async Task FileWithoutFlagsIsRemoved()
    {
        MemoryFilesystem tree = Tree();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        await session.Messages.UnlinkatAsync(new Tunlinkat(0, session.Root.Fid, "hello.txt", 0), Ct);

        Assert.False(tree.Root.Children.ContainsKey("hello.txt"));
    }

    /// <summary>
    /// Rule 20: any bit but <c>AT_REMOVEDIR</c> is <c>EINVAL</c>. The word names an operation this
    /// server does not implement — <c>AT_SYMLINK_NOFOLLOW</c>, say — and a removal is not the sort
    /// of request to guess at.
    /// </summary>
    /// <param name="flags">The flag word the client sends.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(0x100u)]
    [InlineData(0x1000u)]
    [InlineData(LinuxAbi.AT_REMOVEDIR | 0x100u)]
    public async Task AnUnknownFlagIsEinval(uint flags)
    {
        MemoryFilesystem tree = Tree();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages
                .UnlinkatAsync(new Tunlinkat(0, session.Root.Fid, "hello.txt", flags), Ct));

        Assert.Equal(Errno.EINVAL, refusal.Error.Errno);
        Assert.True(tree.Root.Children.ContainsKey("hello.txt"));
    }

    private static MemoryFilesystem Tree()
    {
        MemoryFilesystem tree = new();
        tree.Root.Add(tree.NewFile("hello.txt", 0x1B6));
        tree.Root.Add(tree.NewDirectory("sub", 0x1FF));
        return tree;
    }
}
