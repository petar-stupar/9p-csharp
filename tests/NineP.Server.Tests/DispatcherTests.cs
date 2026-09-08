using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Server.Internal;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests;

/// <summary>
/// Every legal T-message of every dialect, against the shipped server. The point is coverage of
/// the table in <c>docs/server.md</c>: each message reaches its handler method and comes back as
/// its own R-type, in all three dialects, without the caller ever naming one.
/// </summary>
public sealed class DispatcherTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>Every T-message the dispatcher routes is legal in at least one dialect.</summary>
    [Fact]
    public void EveryRoutedTypeIsWireLegal()
    {
        foreach (MessageType type in Dispatcher.HandledTypes)
        {
            Assert.True(
                MessageTypes.IsLegal(type, Dialect.P9_2000)
                || MessageTypes.IsLegal(type, Dialect.P9_2000_u)
                || MessageTypes.IsLegal(type, Dialect.P9_2000_L),
                MessageTypes.GetName(type) + " is routed but is legal in no dialect");
        }
    }

    /// <summary>
    /// The dispatcher routes every legal T-message: the 13 shared and legacy ones plus the 19 of
    /// .L, which with the 34 R-types accounts for the 66 legal type numbers.
    /// </summary>
    [Fact]
    public void EveryLegalTMessageIsRouted()
    {
        List<MessageType> legal = [.. Enum.GetValues<MessageType>()
            .Where(MessageTypes.IsRequest)
            .Where(type => MessageTypes.IsLegal(type, Dialect.P9_2000)
                || MessageTypes.IsLegal(type, Dialect.P9_2000_u)
                || MessageTypes.IsLegal(type, Dialect.P9_2000_L))];

        Assert.Equal(32, legal.Count);
        Assert.Equal([.. legal.Order()], [.. Dispatcher.HandledTypes.Order()]);
    }

    /// <summary>
    /// The messages every dialect carries, answered by the shipped server over the shipped client.
    /// </summary>
    /// <param name="dialect">The dialect the session negotiates.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    [InlineData(Dialect.P9_2000_L)]
    public async Task SharedMessagesAnswerInEveryDialect(Dialect dialect)
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(dialect);

        Rwalk walk = await session.Messages.WalkAsync(new Twalk(0, session.Root.Fid, 20, ["hello.txt"]), Ct);
        Assert.Single(walk.Wqids);

        await session.Messages.WalkAsync(new Twalk(0, 20, 21, []), Ct);
        await Open(session, 20, dialect, OpenMode.ReadWrite);

        Rread read = await session.Messages.ReadAsync(new Tread(0, 20, 0, 64), Ct);
        Assert.Equal("hello, 9P\n"u8.ToArray(), read.Data.ToArray());

        Rwrite written = await session.Messages.WriteAsync(new Twrite(0, 20, 0, "HELLO"u8.ToArray()), Ct);
        Assert.Equal(5u, written.Count);

        await session.Messages.ClunkAsync(new Tclunk(0, 21), Ct);
        await session.Messages.ClunkAsync(new Tclunk(0, 20), Ct);

        // Tauth is refused by a server with no authenticator, which is an answer, not a crash.
        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages.AuthAsync(new Tauth(0, 30, "glenda", "", Constants.NONUNAME), Ct));
        Assert.Equal(Errno.ECONNREFUSED, refusal.Error.Errno);

        Rflush flush = await session.Messages.FlushAsync(new Tflush(0, 4242), Ct);
        Assert.NotEqual(Constants.NOTAG, flush.Tag);
    }

    /// <summary>The stat pair 9P2000 and .u carry, and the wstat that renames.</summary>
    /// <param name="dialect">The dialect the session negotiates.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    public async Task LegacyMessagesAnswer(Dialect dialect)
    {
        MemoryFilesystem tree = Populated();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(dialect);

        NinePFid file = await session.WalkAsync("hello.txt", Ct);
        await using (file.ConfigureAwait(false))
        {
            Rstat stat = await session.Messages.StatAsync(new Tstat(0, file.Fid), Ct);
            Assert.Equal("hello.txt", stat.Stat.Name);

            StatRecord rename = StatRecord.DontTouch with { Name = "renamed.txt" };
            await session.Messages.WstatAsync(new Twstat(0, file.Fid, rename), Ct);
            Assert.True(tree.Root.Children.ContainsKey("renamed.txt"));

            // Tcreate makes the fid the new, open file (reference §5.5).
            NinePFid directory = await session.WalkAsync("sub", Ct);
            await using (directory.ConfigureAwait(false))
            {
                await session.Messages
                    .CreateAsync(new Tcreate(0, directory.Fid, "fresh", 0x1B6, 1, Extension(dialect)), Ct);
            }
        }
    }

    /// <summary>Every message only .L carries, answered by the shipped server.</summary>
    [Fact]
    public async Task LinuxMessagesAnswer()
    {
        MemoryFilesystem tree = Populated();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid root = session.Root;

        Rstatfs statfs = await session.Messages.StatfsAsync(new Tstatfs(0, root.Fid), Ct);
        Assert.Equal(StatFs.V9fsMagic, statfs.Stat.Type);

        await session.Messages.MkdirAsync(new Tmkdir(0, root.Fid, "made", 0x1ED, 1000), Ct);
        await session.Messages.SymlinkAsync(new Tsymlink(0, root.Fid, "link", "hello.txt", 1000), Ct);
        await session.Messages.MknodAsync(new Tmknod(0, root.Fid, "node", 0x21A4, 1, 3, 1000), Ct);

        NinePFid link = await session.WalkAsync("link", Ct);
        await using (link.ConfigureAwait(false))
        {
            Rreadlink target = await session.Messages.ReadlinkAsync(new Treadlink(0, link.Fid), Ct);
            Assert.Equal("hello.txt", target.Target);
        }

        NinePFid file = await session.OpenFileAsync("hello.txt", OpenMode.ReadWrite, OpenFlags.None, Ct);
        await using (file.ConfigureAwait(false))
        {
            Rgetattr attributes = await session.Messages
                .GetattrAsync(new Tgetattr(0, file.Fid, GetAttrMask.All), Ct);
            Assert.Equal(QidType.QTFILE, attributes.Qid.Type);

            await session.Messages.SetattrAsync(
                new Tsetattr(0, file.Fid, SetAttrMask.Mode, 0x1B6, 0, 0, 0, default, default), Ct);
            await session.Messages.FsyncAsync(new Tfsync(0, file.Fid, 1), Ct);

            LockRequest request = new(LockType.WriteLock, LockFlags.None, 0, 0, 7, "client");
            Assert.Equal(LockStatus.Success, (await session.Messages.LockAsync(new Tlock(0, file.Fid, request), Ct)).Status);
            Rgetlock held = await session.Messages
                .GetlockAsync(new Tgetlock(0, file.Fid, LockType.WriteLock, 0, 0, 7, "client"), Ct);
            Assert.Equal(LockType.Unlock, held.Result.Type);

            await session.Messages.LinkAsync(new Tlink(0, root.Fid, file.Fid, "hardlink"), Ct);
            await session.Messages.RenameAsync(new Trename(0, file.Fid, root.Fid, "moved.txt"), Ct);
        }

        await session.Messages.RenameatAsync(new Trenameat(0, root.Fid, "moved.txt", root.Fid, "back.txt"), Ct);
        await session.Messages.UnlinkatAsync(new Tunlinkat(0, root.Fid, "hardlink", 0), Ct);
        Assert.False(tree.Root.Children.ContainsKey("hardlink"));
    }

    /// <summary>Extended attributes: walk onto one, write one, and read the name list back.</summary>
    [Fact]
    public async Task XattrMessagesAnswer()
    {
        MemoryFilesystem tree = Populated();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid file = await session.WalkAsync("hello.txt", Ct);
        await using (file.ConfigureAwait(false))
        {
            await file.SetXattrAsync("user.colour", "blue"u8.ToArray(), XattrFlags.None, Ct);
            Assert.Equal("blue"u8.ToArray(), await file.GetXattrAsync("user.colour", Ct));

            byte[] names = await file.GetXattrAsync("", Ct);
            Assert.Equal("user.colour\0"u8.ToArray(), names);
        }
    }

    /// <summary>
    /// Rule 21: a <c>Txattrcreate</c> whose <c>attr_size</c> is zero <b>removes</b> the attribute.
    /// That is how v9fs and diod spell <c>removexattr(2)</c>, and storing an empty value instead
    /// left the name in the listing and answered a <c>getxattr</c> that should have been
    /// <c>ENODATA</c>.
    /// <b>Mutation:</b> call <c>SetXattrAsync</c> for a zero-length value in
    /// <c>Dispatcher.CommitXattrAsync</c> again and this fails.
    /// </summary>
    [Fact]
    public async Task XattrcreateWithZeroSizeRemovesTheAttribute()
    {
        MemoryFilesystem tree = Populated();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid file = await session.WalkAsync("hello.txt", Ct);
        await using (file.ConfigureAwait(false))
        {
            await file.SetXattrAsync("user.colour", "blue"u8.ToArray(), XattrFlags.None, Ct);
            Assert.Equal("user.colour\0"u8.ToArray(), await file.GetXattrAsync("", Ct));

            // An empty value is a removal, not an empty attribute.
            await file.SetXattrAsync("user.colour", ReadOnlyMemory<byte>.Empty, XattrFlags.None, Ct);

            NinePException gone = await Assert.ThrowsAsync<NinePException>(
                async () => await file.GetXattrAsync("user.colour", Ct));
            Assert.Equal(Errno.ENODATA, gone.Error.Errno);

            Assert.Empty(await file.GetXattrAsync("", Ct));
        }
    }

    /// <summary>Rule 35: in a .L session a <c>Tread</c> on a directory is an error.</summary>
    [Fact]
    public async Task DotLReadOnDirectoryIsAnError()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid directory = await session.OpenFileAsync("/", OpenMode.Read, OpenFlags.None, Ct);
        await using (directory.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.ReadAsync(new Tread(0, directory.Fid, 0, 64), Ct));

            Assert.Equal(Errno.EISDIR, refusal.Error.Errno);
        }
    }

    private static string? Extension(Dialect dialect) =>
        dialect == Dialect.P9_2000_u ? string.Empty : null;

    private static async Task Open(NinePSession session, uint fid, Dialect dialect, OpenMode mode)
    {
        if (dialect == Dialect.P9_2000_L)
        {
            await session.Messages.LopenAsync(new Tlopen(0, fid, (uint)mode), Ct);
            return;
        }

        await session.Messages.OpenAsync(new Topen(0, fid, (byte)mode), Ct);
    }

    private static MemoryFilesystem Populated()
    {
        MemoryFilesystem tree = new();
        MemoryFile greeting = tree.NewFile("hello.txt", 0x1B6);
        greeting.Data = "hello, 9P\n"u8.ToArray();
        tree.Root.Add(greeting);
        tree.Root.Add(tree.NewDirectory("sub", 0x1FF));
        return tree;
    }
}
