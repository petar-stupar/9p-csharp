using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests;

/// <summary>
/// What a <c>Tcreate</c> may ask for (reference §5.5 and §8 rules 19, 24 and 25). The theme is the
/// one the projection-honesty rules share: a create that names something the handler model or the
/// open rules cannot carry is refused, never masked away and answered <c>Rcreate</c>.
/// </summary>
public sealed class CreateTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 19: <c>DMAPPEND</c>, <c>DMEXCL</c> and <c>DMTMP</c> in <c>Tcreate.perm</c> are refused
    /// with <c>EPERM</c>. <c>CreateRequest</c> carries no file flags, so the perm mask used to drop
    /// them and the client was answered <c>Rcreate</c> for a plain file where it had asked for an
    /// append-only, exclusive or temporary one.
    /// <b>Mutation:</b> delete the refusal in <c>Dispatcher.CreateAsync</c> and this fails.
    /// </summary>
    /// <param name="bit">The high perm bit the create carries.</param>
    /// <param name="dialect">The dialect the create goes out in.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(ModeBits.DMAPPEND, Dialect.P9_2000)]
    [InlineData(ModeBits.DMEXCL, Dialect.P9_2000)]
    [InlineData(ModeBits.DMTMP, Dialect.P9_2000)]
    [InlineData(ModeBits.DMAPPEND, Dialect.P9_2000_u)]
    [InlineData(ModeBits.DMEXCL, Dialect.P9_2000_u)]
    [InlineData(ModeBits.DMTMP, Dialect.P9_2000_u)]
    public async Task CreateWithAnUnsupportedModeBitIsRefused(uint bit, Dialect dialect)
    {
        MemoryFilesystem tree = Tree();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(dialect);

        NinePFid directory = await session.WalkAsync("sub", Ct);
        await using (directory.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.CreateAsync(
                    new Tcreate(0, directory.Fid, "flagged", bit | 0x1B6, 0, Extension(dialect)), Ct));

            // The refusal names the bits, exactly as the DMDIR one does. A 9P2000 Rerror carries
            // the ename alone and this ename is not one of ErrorTable's, so only a .u peer — whose
            // Rerror carries errno[4] — gets the EPERM reference §8 rule 19 names.
            Assert.Equal("create cannot set DMAPPEND/DMEXCL/DMTMP", refusal.Error.Ename);

            if (dialect == Dialect.P9_2000_u)
            {
                Assert.Equal(Errno.EPERM, refusal.Error.Errno);
            }

            // Nothing was created: the refusal comes before the handler is asked.
            Assert.False(Sub(tree).Children.ContainsKey("flagged"));
        }
    }

    /// <summary>
    /// Rule 19: the same perm without those bits is an ordinary create, so the refusal above is
    /// about the three bits and not about the create path.
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
