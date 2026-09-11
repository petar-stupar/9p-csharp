using System.Text;
using NineP.Client.Tests;
using NineP.Client.Tests.Conformance;
using NineP.Protocol;
using NineP.Protocol.Negotiation;
using NineP.Protocol.Transports;
using NineP.Server;
using NineP.TestSupport;
using Xunit;

namespace NineP.Client.Tests.Conformance;

/// <summary>
/// The high-level file API of §5.7, run as one scenario against the <b>shipped server</b> in every
/// dialect over <see cref="MemoryTransport"/> (S-31): the in-process round trip of §10.2. The point
/// of running the identical scenario three times is that a caller never learns which dialect was
/// negotiated — the same calls and the same results whether the wire carried <c>Topen</c> or
/// <c>Tlopen</c>, stat records or dirents.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class ClientFileApiTests
{
    private const string Greeting = "hello, 9P\n";

    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>Walk, list, read, write, stat, create, remove and mkdir, identical in all three dialects.</summary>
    /// <param name="version">The dialect string to negotiate.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Constants.Version9P2000)]
    [InlineData(Constants.Version9P2000u)]
    [InlineData(Constants.Version9P2000L)]
    public async Task SameScenarioInEveryDialect(string version)
    {
        await using Harness harness = await Harness.StartAsync(version);
        NinePSession session = harness.Session;

        IReadOnlyList<DirEntry> root = await session.ReadDirAsync("/", Ct);
        Assert.Equal(["hello.txt", "sub"], root.Select(entry => entry.Name).Order(StringComparer.Ordinal));
        Assert.Equal(FileKind.Directory, root.Single(entry => entry.Name == "sub").Kind);

        Assert.Equal(Greeting, Encoding.UTF8.GetString(await session.ReadFileAsync("hello.txt", Ct)));

        await session.WriteFileAsync("hello.txt", "rewritten"u8.ToArray(), Ct);
        Attr rewritten = await session.GetAttrAsync("hello.txt", Ct);
        Assert.Equal(FileKind.File, rewritten.Kind);
        Assert.Equal(9ul, rewritten.Size);

        await session.MkdirAsync("sub/deep", Perms.P0755, Ct);
        Assert.Equal(FileKind.Directory, (await session.GetAttrAsync("sub/deep", Ct)).Kind);

        IReadOnlyList<DirEntry> children = await session.ReadDirAsync("sub", Ct);
        Assert.Equal(["deep"], children.Select(entry => entry.Name));

        await session.RemoveAsync("sub/deep", Ct);
        Assert.Empty(await session.ReadDirAsync("sub", Ct));
    }

    /// <summary>
    /// A transfer larger than one message is chunked at the iounit with the in-flight window, and
    /// the bytes come back exactly as they went out — which is what the window has to preserve.
    /// </summary>
    /// <param name="version">The dialect string to negotiate.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Constants.Version9P2000)]
    [InlineData(Constants.Version9P2000u)]
    [InlineData(Constants.Version9P2000L)]
    public async Task LargeTransfersAreChunkedAtTheIounit(string version)
    {
        await using Harness harness = await Harness.StartAsync(version);

        byte[] payload = new byte[40 * 1024];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 31);
        }

        NinePFid created = await harness.Session.CreateFileAsync("sub/big.bin", Perms.P0644, Ct);
        await using (created.ConfigureAwait(false))
        {
            // The payload is several times the iounit this msize allows, so the transfer cannot
            // have happened in one message however the window scheduled it.
            Assert.True(payload.Length > created.Iounit * 2);
            await created.WriteAllAsync(payload, Ct);
        }

        Assert.Equal(payload, await harness.Session.ReadFileAsync("sub/big.bin", Ct));
        Assert.Equal((ulong)payload.Length, (await harness.Session.GetAttrAsync("sub/big.bin", Ct)).Size);
    }

    /// <summary>walk(5): ".." at the root walks to the root itself, in every dialect.</summary>
    /// <param name="version">The dialect string to negotiate.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Constants.Version9P2000)]
    [InlineData(Constants.Version9P2000u)]
    [InlineData(Constants.Version9P2000L)]
    public async Task DotDotNavigationReturnsToTheRoot(string version)
    {
        await using Harness harness = await Harness.StartAsync(version);

        NinePFid back = await harness.Session.WalkAsync("sub/..", Ct);
        await using (back.ConfigureAwait(false))
        {
            Assert.Equal(harness.Session.Root.Qid, back.Qid);
        }

        NinePFid above = await harness.Session.WalkAsync("..", Ct);
        await using (above.ConfigureAwait(false))
        {
            Assert.Equal(harness.Session.Root.Qid, above.Qid);
        }
    }

    /// <summary>A path that does not resolve is an error, and no fid is left behind.</summary>
    /// <param name="version">The dialect string to negotiate.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Constants.Version9P2000)]
    [InlineData(Constants.Version9P2000u)]
    [InlineData(Constants.Version9P2000L)]
    public async Task AMissingPathLeaksNoFid(string version)
    {
        await using Harness harness = await Harness.StartAsync(version);

        int clientFids = harness.Session.LiveFids;

        await Assert.ThrowsAsync<NinePException>(
            async () => await harness.Session.ReadFileAsync("nowhere.txt", Ct));

        // The clone the walk started with is clunked on the way out; nothing is left behind.
        Assert.Equal(clientFids, harness.Session.LiveFids);
    }

    /// <summary>
    /// Reference §8 rule 15: symlink creation, statfs, locks and xattrs exist only in <c>.L</c>, so
    /// outside it every one of them is <c>EOPNOTSUPP</c> before anything is sent — the operation is
    /// missing from the dialect, not merely unsupported by this server.
    /// </summary>
    /// <param name="version">The dialect string to negotiate.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Constants.Version9P2000)]
    [InlineData(Constants.Version9P2000u)]
    public async Task LinuxExtrasAreEopnotsuppElsewhere(string version)
    {
        await using Harness harness = await Harness.StartAsync(version);

        NinePException statfs = await Assert.ThrowsAsync<NinePException>(
            async () => await harness.Session.StatFsAsync("/", Ct));
        Assert.Equal(Errno.EOPNOTSUPP, statfs.Error.Errno);

        NinePException symlink = await Assert.ThrowsAsync<NinePException>(
            async () => await harness.Session.SymlinkAsync("link", "hello.txt", Ct));
        Assert.Equal(Errno.EOPNOTSUPP, symlink.Error.Errno);

        NinePFid fid = await harness.Session.WalkAsync("hello.txt", Ct);
        await using (fid.ConfigureAwait(false))
        {
            LockRequest request = new(LockType.WriteLock, LockFlags.None, 0, 0, 1, "c");

            NinePException locking = await Assert.ThrowsAsync<NinePException>(
                async () => await fid.LockAsync(request, Ct));
            Assert.Equal(Errno.EOPNOTSUPP, locking.Error.Errno);

            NinePException query = await Assert.ThrowsAsync<NinePException>(
                async () => await fid.GetLockAsync(request, Ct));
            Assert.Equal(Errno.EOPNOTSUPP, query.Error.Errno);

            NinePException xattr = await Assert.ThrowsAsync<NinePException>(
                async () => await fid.GetXattrAsync("user.x", Ct));
            Assert.Equal(Errno.EOPNOTSUPP, xattr.Error.Errno);

            NinePException setting = await Assert.ThrowsAsync<NinePException>(
                async () => await fid.SetXattrAsync("user.x", new byte[] { 1 }, XattrFlags.None, Ct));
            Assert.Equal(Errno.EOPNOTSUPP, setting.Error.Errno);
        }
    }

    /// <summary>
    /// Reference §8 rule 15: stat(5)'s <c>wstat</c> renames within one directory and has no
    /// destination directory at all, so a move is refused in 9P2000 and <c>.u</c> rather than
    /// performed as a rename that ignored where the caller asked the file to go. The file is still
    /// where it was, and the destination was never created.
    /// </summary>
    /// <param name="version">The dialect string to negotiate.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Constants.Version9P2000)]
    [InlineData(Constants.Version9P2000u)]
    public async Task ARenameAcrossDirectoriesIsRefusedOutsideDotL(string version)
    {
        await using Harness harness = await Harness.StartAsync(version);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await harness.Session.RenameAsync("hello.txt", "sub/hello.txt", Ct));

        Assert.Contains("across directories", refusal.Error.Ename, StringComparison.Ordinal);

        Assert.Equal(
            ["hello.txt", "sub"],
            (await harness.Session.ReadDirAsync("/", Ct)).Select(entry => entry.Name).Order(StringComparer.Ordinal));
        Assert.Empty(await harness.Session.ReadDirAsync("sub", Ct));
    }

    /// <summary>A rename inside one directory is the one those dialects do have, and it works.</summary>
    /// <param name="version">The dialect string to negotiate.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Constants.Version9P2000)]
    [InlineData(Constants.Version9P2000u)]
    public async Task ARenameWithinADirectoryStillWorks(string version)
    {
        await using Harness harness = await Harness.StartAsync(version);

        await harness.Session.RenameAsync("hello.txt", "greeting.txt", Ct);

        Assert.Equal(
            ["greeting.txt", "sub"],
            (await harness.Session.ReadDirAsync("/", Ct)).Select(entry => entry.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(Constants.Version9P2000)]
    [InlineData(Constants.Version9P2000u)]
    [InlineData(Constants.Version9P2000L)]
    public async Task EmptyFileApisDistinguishWriteAllFromTruncatingOpen(string version)
    {
        await using Harness h = await Harness.StartAsync(version);
        byte[] before = await h.Session.ReadFileAsync("hello.txt", Ct);
        await using (NinePFid f = await h.Session.OpenFileAsync("hello.txt", OpenMode.ReadWrite, OpenFlags.None, Ct))
        {
            long writes = h.Server.Counters.MessagesByType.GetValueOrDefault(NineP.Protocol.MessageType.Twrite);
            await f.WriteAllAsync(ReadOnlyMemory<byte>.Empty, Ct);
            Assert.Equal(before, await h.Session.ReadFileAsync("hello.txt", Ct));
            Assert.Equal(writes, h.Server.Counters.MessagesByType.GetValueOrDefault(NineP.Protocol.MessageType.Twrite));
        }
        await h.Session.WriteFileAsync("hello.txt", ReadOnlyMemory<byte>.Empty, Ct);
        Assert.Empty(await h.Session.ReadFileAsync("hello.txt", Ct));
        Assert.Equal(0ul, (await h.Session.GetAttrAsync("hello.txt", Ct)).Size);
        MemoryFile file = (MemoryFile)h.Tree.Root.Children["hello.txt"];
        file.Data = before;
        file.Flags = FileFlags.Append;
        await h.Session.WriteFileAsync("hello.txt", ReadOnlyMemory<byte>.Empty, Ct);
        Assert.Equal(before, await h.Session.ReadFileAsync("hello.txt", Ct));
        await using NinePFid created = await h.Session.CreateFileAsync("new", Perms.P0644, Ct);
        Assert.Equal(0ul, (await created.GetAttrAsync(Ct)).Size);
        Assert.Equal(0ul, (await h.Session.GetAttrAsync("/", Ct)).Size);
    }

    [Theory]
    [InlineData(Constants.Version9P2000)]
    [InlineData(Constants.Version9P2000u)]
    [InlineData(Constants.Version9P2000L)]
    public async Task TypedApiHandlesAstralAndMaximumNames(string version)
    {
        await using Harness h = await Harness.StartAsync(version);
        foreach (string name in new[] { "😀", new string('x', 255), new string('é', 127) + "x" })
        {
            await using (NinePFid f = await h.Session.CreateFileAsync(name, Perms.P0644, Ct))
            {
                await f.WriteAllAsync("ok"u8.ToArray(), Ct);
            }
            Assert.Equal("ok"u8.ToArray(), await h.Session.ReadFileAsync(name, Ct));
            Assert.Contains(await h.Session.ReadDirAsync("/", Ct), e => e.Name == name);
            await h.Session.RemoveAsync(name, Ct);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Task _serving;

        private Harness(NinePServer server, NinePSession session, MemoryFilesystem tree, Task serving)
        {
            Server = server;
            Session = session;
            Tree = tree;
            _serving = serving;
        }

        public NinePServer Server { get; }

        public NinePSession Session { get; }

        public MemoryFilesystem Tree { get; }

        public static async Task<Harness> StartAsync(string version)
        {
            MemoryTransport transport = new();
            NinePAddress address = new(NinePScheme.Memory, "c" + Guid.NewGuid().ToString("N"), 0, string.Empty);

            MemoryFilesystem tree = new();
            MemoryFile greeting = tree.NewFile("hello.txt", Perms.P0666);
            greeting.Data = Encoding.UTF8.GetBytes(Greeting);
            tree.Root.Add(greeting);
            tree.Root.Add(tree.NewDirectory("sub", Perms.P0777));

            NinePServer server = new(new ServerOptions { Listen = [address], Transports = [transport] });
            Task serving = server.ServeAsync(tree, CancellationToken.None);
            await server.Listening;

            Assert.True(Negotiator.TryParseVersion(version, out Dialect dialect));

            // The tree is owned by "glenda"; an unauthenticated attach runs as the uname it
            // claims (reference §5.2), so the client claims the owner.
            ClientOptions options = new() { Dialects = [dialect], Msize = 8192, Uname = "glenda" };
            NinePSession session = await NinePClient.ConnectAsync(transport, address, options, CancellationToken.None);
            await session.AttachAsync(TestDeadlines.Wrap(TestContext.Current.CancellationToken));

            return new Harness(server, session, tree, serving);
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Server.DisposeAsync();

            try
            {
                await _serving;
            }
            catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException)
            {
                // The accept loop was stopped on purpose.
            }
        }
    }
}
