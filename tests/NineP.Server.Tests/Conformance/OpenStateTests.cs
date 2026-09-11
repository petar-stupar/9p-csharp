using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// Open state (reference §4.5, §5.5, §6.5): the mode byte, the flags that are flags rather than
/// modes, exclusive use, remove-on-close, and the iounit every open reply carries.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class OpenStateTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// S-21 and rule 12: <c>OAPPEND</c> is a flag OR'd with an access mode, not an access mode.
    /// <b>Mutation:</b> switching on the whole mode byte instead of its low two bits turns
    /// <c>OREAD | OAPPEND</c> (0x80) into an unknown mode and this test fails.
    /// </summary>
    [Fact]
    public void OreadPlusOappendStaysRead()
    {
        (OpenMode mode, OpenFlags flags) = Internal.OpenState.Decode(0x80);

        Assert.Equal(OpenMode.Read, mode);
        Assert.True(flags.HasFlag(OpenFlags.Append));

        (OpenMode write, OpenFlags writeFlags) = Internal.OpenState.Decode(0x81);
        Assert.Equal(OpenMode.Write, write);
        Assert.True(writeFlags.HasFlag(OpenFlags.Append));
    }

    /// <summary>Rule 11 (§4.5): any bit the reference does not define is <c>"bad open mode"</c>.</summary>
    [Fact]
    public void BadOpenModeRejected()
    {
        NinePException refusal = Assert.Throws<NinePException>(() => Internal.OpenState.Decode(0x04));

        Assert.Equal("bad open mode", refusal.Error.Ename);

        // The bits reference §4.5 does define are all accepted, OCEXEC included: it is
        // client-local and servers ignore it rather than refuse it.
        Internal.OpenState.Decode(0x10 | 0x20 | 0x40 | 0x80 | 0x02);
    }

    /// <summary>
    /// Rule 12: the .L flags a server honours are the access mode, <c>O_TRUNC</c>,
    /// <c>O_APPEND</c>, <c>O_EXCL</c>, <c>O_DIRECTORY</c> and <c>O_NOFOLLOW</c>; the rest are
    /// ignored rather than refused, because v9fs sends them routinely.
    /// </summary>
    [Fact]
    public void DotLFlagsHonoured()
    {
        (OpenMode mode, OpenFlags flags) = Internal.OpenState.DecodeLinux(
            0x1 | 0x200 | 0x400 | 0x80 | 0x10000 | 0x20000);

        Assert.Equal(OpenMode.Write, mode);
        Assert.True(flags.HasFlag(OpenFlags.Truncate));
        Assert.True(flags.HasFlag(OpenFlags.Append));
        Assert.True(flags.HasFlag(OpenFlags.Exclusive));
        Assert.True(flags.HasFlag(OpenFlags.Directory));
        Assert.True(flags.HasFlag(OpenFlags.NoFollow));

        // O_NONBLOCK, O_CLOEXEC and friends are ignored, not refused.
        (OpenMode plain, OpenFlags none) = Internal.OpenState.DecodeLinux(0x800 | 0x80000 | 0x2000000);
        Assert.Equal(OpenMode.Read, plain);
        Assert.Equal(OpenFlags.None, none);
    }

    /// <summary>Rule 30 (S-25): <c>iounit</c> is <c>msize - IOHDRSZ</c> on every open reply.</summary>
    /// <param name="dialect">The dialect the open goes out in.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_L)]
    public async Task IounitIsMsizeMinusIohdrsz(Dialect dialect)
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(dialect);

        NinePFid file = await session.OpenFileAsync("hello.txt", OpenMode.Read, OpenFlags.None, Ct);
        await using (file.ConfigureAwait(false))
        {
            Assert.Equal((int)session.Msize - Constants.IOHDRSZ, file.Iounit);
        }
    }

    /// <summary>§5.5: a fid that is already open cannot be opened again.</summary>
    [Fact]
    public async Task OpenOfAnOpenFidIsRefused()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid file = await session.OpenFileAsync("hello.txt", OpenMode.Read, OpenFlags.None, Ct);
        await using (file.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.OpenAsync(new Topen(0, file.Fid, 0), Ct));

            Assert.Equal("bad open mode", refusal.Error.Ename);
        }
    }

    /// <summary>
    /// §5.5: <c>DMEXCL</c> means one open fid at a time <b>across all clients</b>, so the second
    /// open fails on a different connection too — the registry is the server's, not a session's.
    /// </summary>
    [Fact]
    public async Task DmexclSecondOpenFails()
    {
        MemoryFilesystem tree = new();
        MemoryFile guarded = tree.NewFile("locked", Perms.P0644);
        guarded.Exclusive = true;
        tree.Root.Add(guarded);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession first = await harness.ConnectAsync(Dialect.P9_2000);
        await using NinePSession second = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid held = await first.OpenFileAsync("locked", OpenMode.Read, OpenFlags.None, Ct);
        await using (held.ConfigureAwait(false))
        {
            await Assert.ThrowsAsync<NinePException>(
                async () => await second.OpenFileAsync("locked", OpenMode.Read, OpenFlags.None, Ct));
        }

        // Once the holder clunks, the next open succeeds: the lock is released, not leaked.
        NinePFid after = await second.OpenFileAsync("locked", OpenMode.Read, OpenFlags.None, Ct);
        await after.DisposeAsync();
    }

    /// <summary>§4.5 and §5.7: a fid opened with <c>ORCLOSE</c> removes its file on clunk.</summary>
    [Fact]
    public async Task OrcloseRemovesOnClunk()
    {
        MemoryFilesystem tree = new();
        tree.Root.Add(tree.NewFile("scratch", Perms.P0666));

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid file = await session
            .OpenFileAsync("scratch", OpenMode.Read, OpenFlags.RemoveOnClose, Ct);

        Assert.True(tree.Root.Children.ContainsKey("scratch"));
        await file.DisposeAsync();
        Assert.False(tree.Root.Children.ContainsKey("scratch"));
    }

    /// <summary>
    /// Rule 23: a <c>Topen</c> or <c>Tlopen</c> of a symbolic link is <c>ELOOP</c>. 9P resolves
    /// nothing on the client's behalf, so there is no file for the open to land on; it used to
    /// succeed with no open file behind it and answer every <c>Tread</c> with zero bytes, which
    /// reads as an empty file. <c>ELOOP</c> is in <c>ErrorTable</c>, so all three dialects say the
    /// same thing — <c>"too many symbolic links"</c> in 9P2000, errno 40 in .u and .L.
    /// <b>Mutation:</b> delete the symlink arm of <c>OpenState.Validate</c> and this fails.
    /// </summary>
    /// <param name="dialect">The dialect the open goes out in.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    [InlineData(Dialect.P9_2000_L)]
    public async Task OpenOfASymlinkIsEloop(Dialect dialect)
    {
        MemoryFilesystem tree = new();
        tree.Root.Add(tree.NewFile("hello.txt", Perms.P0666));
        tree.Root.Add(tree.NewSymlink("link", "hello.txt"));

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(dialect);

        NinePFid link = await session.WalkAsync("link", Ct);
        await using (link.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await OpenRaw(session, link.Fid, dialect, 0));

            Assert.Equal(Errno.ELOOP, refusal.Error.Errno);
        }
    }

    /// <summary>
    /// Rule 23 and rule 12: <c>O_NOFOLLOW</c> is decoded, so it is honoured — and on a symbolic
    /// link it means the same <c>ELOOP</c> the open already draws.
    /// </summary>
    [Fact]
    public async Task NofollowOnASymlinkIsEloop()
    {
        MemoryFilesystem tree = new();
        tree.Root.Add(tree.NewSymlink("link", "elsewhere"));

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid link = await session.WalkAsync("link", Ct);
        await using (link.ConfigureAwait(false))
        {
            // O_NOFOLLOW is 0400000; the shipped client refuses to send it outside .L (rule 15),
            // so it goes out as a raw Tlopen here.
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.LopenAsync(new Tlopen(0, link.Fid, 0x20000), Ct));

            Assert.Equal(Errno.ELOOP, refusal.Error.Errno);
        }
    }

    /// <summary>
    /// Rule 23: a fifo, socket or device the server cannot open is <c>ENXIO</c>, never an open
    /// reply with nothing behind it. <c>ENXIO</c> now has a row in <c>ErrorTable</c>, so all three
    /// dialects say the same thing: .u and .L carry the number, and a 9P2000 peer is told
    /// <c>"no such device or address"</c>, which maps back to <c>ENXIO</c> rather than
    /// degrading to the <c>"i/o error"</c> / <c>EIO</c> it used to.
    /// </summary>
    /// <param name="dialect">The dialect the open goes out in.</param>
    /// <param name="errno">The errno the client ends up with in that dialect.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Dialect.P9_2000, Errno.ENXIO)]
    [InlineData(Dialect.P9_2000_u, Errno.ENXIO)]
    [InlineData(Dialect.P9_2000_L, Errno.ENXIO)]
    public async Task OpenOfAFifoIsEnxio(Dialect dialect, int errno)
    {
        MemoryFilesystem tree = new();
        tree.Root.Add(new MemoryFifo("pipe", Perms.P0666, 4242));

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(dialect);

        NinePFid pipe = await session.WalkAsync("pipe", Ct);
        await using (pipe.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await OpenRaw(session, pipe.Fid, dialect, 0));

            Assert.Equal(errno, refusal.Error.Errno);

            // And the fid is still unopened, so a read on it is refused — "bad open mode", which
            // is EINVAL, and only the errno survives an Rlerror — rather than answered with the
            // zero bytes the old open would have produced for ever.
            NinePException read = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.ReadAsync(new Tread(0, pipe.Fid, 0, 64), Ct));
            Assert.Equal(Errno.EINVAL, read.Error.Errno);
        }
    }

    /// <summary>
    /// Rule 23 and rule 12: <c>O_DIRECTORY</c> is decoded, so it is honoured — an open of anything
    /// but a directory under it is <c>ENOTDIR</c> rather than a silently ordinary open.
    /// </summary>
    [Fact]
    public async Task ODirectoryOnAPlainFileIsEnotdir()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid file = await session.WalkAsync("hello.txt", Ct);
        await using (file.ConfigureAwait(false))
        {
            // O_DIRECTORY is 0200000.
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.LopenAsync(new Tlopen(0, file.Fid, 0x10000), Ct));

            Assert.Equal(Errno.ENOTDIR, refusal.Error.Errno);
        }

        // The same flag on a directory is what it is for, and opens it.
        NinePFid directory = await session.WalkAsync("sub", Ct);
        await using (directory.ConfigureAwait(false))
        {
            await session.Messages.LopenAsync(new Tlopen(0, directory.Fid, 0x10000), Ct);
        }
    }

    /// <summary>§4.5: a directory may only be opened for reading or searching.</summary>
    [Fact]
    public async Task DirectoryOpenIsReadOrExecOnly()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePException write = await Assert.ThrowsAsync<NinePException>(
            async () => await session.OpenFileAsync("sub", OpenMode.Write, OpenFlags.None, Ct));
        Assert.Equal(Errno.EISDIR, write.Error.Errno);

        NinePException truncate = await Assert.ThrowsAsync<NinePException>(
            async () => await session.OpenFileAsync("sub", OpenMode.Read, OpenFlags.Truncate, Ct));
        Assert.Equal(Errno.EISDIR, truncate.Error.Errno);
    }

    /// <summary>Opens a fid with the message the dialect carries, bypassing the client's own rules.</summary>
    /// <param name="session">The attached session.</param>
    /// <param name="fid">The fid to open.</param>
    /// <param name="dialect">The dialect the session negotiated.</param>
    /// <param name="mode">The mode byte, which is also the .L flag word for the modes used here.</param>
    /// <returns>A task that completes when the reply has arrived.</returns>
    private static async Task OpenRaw(NinePSession session, uint fid, Dialect dialect, byte mode)
    {
        if (dialect == Dialect.P9_2000_L)
        {
            await session.Messages.LopenAsync(new Tlopen(0, fid, mode), Ct);
            return;
        }

        await session.Messages.OpenAsync(new Topen(0, fid, mode), Ct);
    }

    /// <summary>
    /// A named pipe: a handler with a kind but no <see cref="IFileHandler"/> behind it, which is
    /// exactly the shape rule 23 answers <c>ENXIO</c>. <c>MemoryFilesystem</c> makes every node it
    /// creates openable, so the one node that is not is written here.
    /// </summary>
    /// <param name="name">The node's name.</param>
    /// <param name="perm">Its permission bits.</param>
    /// <param name="path">Its qid path.</param>
    private sealed class MemoryFifo(string name, FilePermissions perm, ulong path)
        : MemoryNode(name, FileKind.Fifo, perm, path);
}
