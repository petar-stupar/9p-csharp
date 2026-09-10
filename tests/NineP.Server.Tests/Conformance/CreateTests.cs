using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// What a <c>Tcreate</c> may ask for (reference §5.5 and §8 rules 19, 24 and 25). The theme is the
/// one the projection-honesty rules share: what a create asks for reaches the handler whole, and a
/// create that names something the handler model or the open rules cannot carry is refused, never
/// masked away and answered <c>Rcreate</c>.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class CreateTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 19: <c>DMAPPEND</c>, <c>DMEXCL</c> and <c>DMTMP</c> in <c>Tcreate.perm</c> are what
    /// open(2) lets a create ask for, and they reach the handler as
    /// <see cref="CreateRequest.FileFlags"/>; the file the client is then told about has them.
    /// They used to be refused, and before that masked away and answered <c>Rcreate</c> for a
    /// plain file where the client had asked for an append-only, exclusive or temporary one.
    /// <b>Mutation:</b> stop passing <c>fileFlags</c> into the request in
    /// <c>Dispatcher.CreateChildAsync</c> and the handler assertion fails.
    /// </summary>
    /// <param name="bit">The high perm bit the create carries.</param>
    /// <param name="expected">The flag it names.</param>
    /// <param name="dialect">The dialect the create goes out in.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(ModeBits.DMAPPEND, FileFlags.Append, Dialect.P9_2000)]
    [InlineData(ModeBits.DMEXCL, FileFlags.Exclusive, Dialect.P9_2000)]
    [InlineData(ModeBits.DMTMP, FileFlags.Temporary, Dialect.P9_2000)]
    [InlineData(ModeBits.DMAPPEND, FileFlags.Append, Dialect.P9_2000_u)]
    [InlineData(ModeBits.DMEXCL, FileFlags.Exclusive, Dialect.P9_2000_u)]
    [InlineData(ModeBits.DMTMP, FileFlags.Temporary, Dialect.P9_2000_u)]
    public async Task CreateCarriesTheFileFlagsToTheHandler(uint bit, FileFlags expected, Dialect dialect)
    {
        MemoryFilesystem tree = Tree();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(dialect);

        NinePFid directory = await session.WalkAsync("sub", Ct);
        await using (directory.ConfigureAwait(false))
        {
            await session.Messages.CreateAsync(
                new Tcreate(0, directory.Fid, "flagged", bit | 0x1B6, 0, Extension(dialect)), Ct);

            CreateRequest created = Assert.IsType<CreateRequest>(Sub(tree).LastCreate);
            Assert.Equal(expected, created.FileFlags);
            Assert.Equal(expected, Sub(tree).Children["flagged"].Flags);

            // The fid is now the new file, and what it stats carries the bit the create asked for.
            StatRecord now = (await session.Messages.StatAsync(new Tstat(0, directory.Fid), Ct)).Stat;
            Assert.Equal(bit, now.Mode & bit);
        }
    }

    /// <summary>
    /// Rule 19: <c>DMAUTH</c> and <c>DMMOUNT</c> are the server's own bits, and a create asking
    /// for either is refused rather than answered <c>Rcreate</c> for a file that has neither —
    /// which is what the parent mask used to do, silently.
    /// </summary>
    /// <param name="bit">The server-owned bit the create carries.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(ModeBits.DMAUTH)]
    [InlineData(ModeBits.DMMOUNT)]
    public async Task CreateWithAServerOwnedBitIsRefused(uint bit)
    {
        MemoryFilesystem tree = Tree();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid directory = await session.WalkAsync("sub", Ct);
        await using (directory.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.CreateAsync(
                    new Tcreate(0, directory.Fid, "flagged", bit | 0x1B6, 0, null), Ct));

            // A 9P2000 Rerror carries the text alone; the ename is an ErrorTable row, so the
            // client recovers the EPERM the refusal means rather than EIO.
            Assert.Equal("create cannot set DMAUTH or DMMOUNT", refusal.Error.Ename);
            Assert.Equal(Errno.EPERM, refusal.Error.Errno);

            // Nothing was created: the refusal comes before the handler is asked.
            Assert.False(Sub(tree).Children.ContainsKey("flagged"));
        }
    }

    /// <summary>
    /// Rule 19: a success reply is a statement that the file has the flags the create asked for.
    /// A handler that took the request and made a plain file — one written before
    /// <see cref="CreateRequest.FileFlags"/> existed — is not answered <c>Rcreate</c> for it: the
    /// core reads the new file back, removes it again and refuses the create, and the fid stays
    /// the directory it was.
    /// <b>Mutation:</b> delete the read-back in <c>Dispatcher.CreateChildAsync</c> and this fails.
    /// </summary>
    [Fact]
    public async Task ACreateWhoseFlagsTheHandlerDroppedIsRemovedAndRefused()
    {
        MemoryFilesystem tree = Tree();
        Sub(tree).DropsFileFlags = true;
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid directory = await session.WalkAsync("sub", Ct);
        await using (directory.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.CreateAsync(
                    new Tcreate(0, directory.Fid, "flagged", ModeBits.DMAPPEND | 0x1B6, 0, null), Ct));

            Assert.Equal(Errno.EOPNOTSUPP, refusal.Error.Errno);

            // The handler did create the file; the core removed it before answering.
            Assert.NotNull(Sub(tree).LastCreate);
            Assert.False(Sub(tree).Children.ContainsKey("flagged"));

            // The fid is still the directory: an ordinary create on it works.
            await session.Messages.CreateAsync(new Tcreate(0, directory.Fid, "plain", 0x1B6, 0, null), Ct);
            Assert.True(Sub(tree).Children.ContainsKey("plain"));
        }
    }

    /// <summary>
    /// §5.5 and rule 19: the open a create performs is an open like any other, so a
    /// <c>DMEXCL</c> file is held by its creator from the moment it exists — a second client
    /// cannot open it until the creating fid is clunked, exactly as after a <c>Topen</c>.
    /// </summary>
    [Fact]
    public async Task ACreatedExclusiveFileIsHeldByItsCreator()
    {
        MemoryFilesystem tree = Tree();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession first = await harness.ConnectAsync(Dialect.P9_2000);
        await using NinePSession second = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid creator = await first.WalkAsync("sub", Ct);
        await first.Messages.CreateAsync(
            new Tcreate(0, creator.Fid, "locked", ModeBits.DMEXCL | 0x1B6, 1, null), Ct);

        await Assert.ThrowsAsync<NinePException>(
            async () => await second.OpenFileAsync("sub/locked", OpenMode.Read, OpenFlags.None, Ct));

        // Once the creator clunks, the next open succeeds: the lock is released, not leaked.
        await creator.DisposeAsync();
        NinePFid after = await second.OpenFileAsync("sub/locked", OpenMode.Read, OpenFlags.None, Ct);
        await after.DisposeAsync();
    }

    /// <summary>
    /// Rule 19: the same perm without those bits is an ordinary create, and the handler is told
    /// so: <see cref="CreateRequest.FileFlags"/> is <see cref="FileFlags.None"/>.
    /// </summary>
    [Fact]
    public async Task CreateWithoutThoseBitsStillWorks()
    {
        MemoryFilesystem tree = Tree();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid directory = await session.WalkAsync("sub", Ct);
        await using (directory.ConfigureAwait(false))
        {
            await session.Messages.CreateAsync(new Tcreate(0, directory.Fid, "plain", 0x1B6, 0, null), Ct);

            Assert.True(Sub(tree).Children.ContainsKey("plain"));
            Assert.Equal(FileFlags.None, Assert.IsType<CreateRequest>(Sub(tree).LastCreate).FileFlags);
        }
    }

    /// <summary>
    /// Rule 24: a .u <c>Tcreate</c> with <c>DMDEVICE</c> parses <c>extension</c> into the device
    /// numbers. The text used to be passed on as the symlink <c>target</c> with <c>rdev</c> left
    /// null, so the numbers the client sent never reached the handler at all.
    /// </summary>
    /// <param name="extension">The extension the create carries.</param>
    /// <param name="kind">The kind it names.</param>
    /// <param name="major">The major number it carries.</param>
    /// <param name="minor">The minor number it carries.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData("c 5 1", FileKind.CharDevice, 5u, 1u)]
    [InlineData("b 8 0", FileKind.BlockDevice, 8u, 0u)]
    public async Task DeviceCreateParsesTheExtension(
        string extension, FileKind kind, uint major, uint minor)
    {
        MemoryFilesystem tree = Tree();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_u);

        NinePFid directory = await session.WalkAsync("sub", Ct);
        await using (directory.ConfigureAwait(false))
        {
            await session.Messages.CreateAsync(
                new Tcreate(0, directory.Fid, "dev", ModeBits.DMDEVICE | 0x1B6, 0, extension), Ct);

            CreateRequest created = Assert.IsType<CreateRequest>(Sub(tree).LastCreate);

            Assert.Equal(kind, created.Kind);
            Assert.Equal(new DeviceId(major, minor), created.Rdev);

            // The extension is a device specification, not a symlink target: passing it on as one
            // is what the handler used to receive.
            Assert.Null(created.Target);
        }
    }

    /// <summary>
    /// Rule 24: a <c>DMDEVICE</c> create whose extension is missing or malformed is
    /// <c>EINVAL</c>, rather than a device node with no numbers behind it.
    /// </summary>
    /// <param name="extension">The extension the create carries.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData("")]
    [InlineData("c 5")]
    [InlineData("c 5 1 2")]
    [InlineData("x 5 1")]
    [InlineData("c five one")]
    [InlineData("c -5 1")]
    public async Task DeviceCreateWithABadExtensionIsEinval(string extension)
    {
        MemoryFilesystem tree = Tree();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_u);

        NinePFid directory = await session.WalkAsync("sub", Ct);
        await using (directory.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.CreateAsync(
                    new Tcreate(0, directory.Fid, "dev", ModeBits.DMDEVICE | 0x1B6, 0, extension), Ct));

            Assert.Equal(Errno.EINVAL, refusal.Error.Errno);
            Assert.False(Sub(tree).Children.ContainsKey("dev"));
        }
    }

    /// <summary>
    /// Rule 25: a create of a directory whose mode carries <c>OTRUNC</c> or <c>ORCLOSE</c> is
    /// refused with <c>EISDIR</c>, which is what <c>OpenState.Validate</c> answers for the same
    /// flags on an open. An accepted <c>ORCLOSE</c> was worse than untidy: the directory was
    /// created and then removed again when the fid was clunked.
    /// </summary>
    /// <param name="mode">The mode byte the create carries.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData((byte)0x10)]
    [InlineData((byte)0x40)]
    [InlineData((byte)0x50)]
    public async Task DirectoryCreateRefusesTruncateAndRemoveOnClose(byte mode)
    {
        MemoryFilesystem tree = Tree();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid directory = await session.WalkAsync("sub", Ct);
        await using (directory.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.CreateAsync(
                    new Tcreate(0, directory.Fid, "made", ModeBits.DMDIR | 0x1FF, mode, null), Ct));

            Assert.Equal(Errno.EISDIR, refusal.Error.Errno);
            Assert.False(Sub(tree).Children.ContainsKey("made"));
        }
    }

    /// <summary>
    /// Rule 25 and §5.5: the same create without those flags makes the directory, and the fid it
    /// leaves behind is the new directory opened for reading.
    /// </summary>
    [Fact]
    public async Task DirectoryCreateWithoutThoseFlagsStillWorks()
    {
        MemoryFilesystem tree = Tree();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid directory = await session.WalkAsync("sub", Ct);
        await using (directory.ConfigureAwait(false))
        {
            await session.Messages.CreateAsync(
                new Tcreate(0, directory.Fid, "made", ModeBits.DMDIR | 0x1FF, 0, null), Ct);

            Assert.True(Sub(tree).Children.ContainsKey("made"));

            // The fid is the new directory and it is open: a Tread of it packs stat records.
            await session.Messages.ReadAsync(new Tread(0, directory.Fid, 0, 512), Ct);
        }

        Assert.True(Sub(tree).Children.ContainsKey("made"));
    }

    /// <summary>
    /// Rule 23 at create time: a .u <c>Tcreate</c> of a symlink is how v9fs spells
    /// <c>symlink(2)</c> on a .u mount, so it succeeds — but the fid it leaves behind does not
    /// claim to be an open file it has nothing behind. A <c>Tread</c> of it is refused instead of
    /// answering zero bytes for ever.
    /// </summary>
    [Fact]
    public async Task SymlinkCreateLeavesAFidThatIsNotOpen()
    {
        MemoryFilesystem tree = Tree();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_u);

        NinePFid directory = await session.WalkAsync("sub", Ct);
        await using (directory.ConfigureAwait(false))
        {
            await session.Messages.CreateAsync(
                new Tcreate(0, directory.Fid, "link", ModeBits.DMSYMLINK | 0x1FF, 0, "hello.txt"), Ct);

            Assert.True(Sub(tree).Children.ContainsKey("link"));

            NinePException read = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.ReadAsync(new Tread(0, directory.Fid, 0, 64), Ct));

            Assert.Equal("bad open mode", read.Error.Ename);
        }
    }

    private static MemoryDirectory Sub(MemoryFilesystem tree) =>
        Assert.IsType<MemoryDirectory>(tree.Root.Children["sub"]);

    private static string? Extension(Dialect dialect) =>
        dialect == Dialect.P9_2000_u ? string.Empty : null;

    private static MemoryFilesystem Tree()
    {
        MemoryFilesystem tree = new();
        tree.Root.Add(tree.NewDirectory("sub", 0x1FF));
        return tree;
    }
}
