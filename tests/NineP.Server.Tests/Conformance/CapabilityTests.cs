using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Messages;
using NineP.Server;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// Optional capabilities (architecture §4): a handler that does not implement one is answered
/// <c>EOPNOTSUPP</c> by the core — never a crash, and never a silent success.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class CapabilityTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Locks, extended attributes, hard links and statfs all answer <c>EOPNOTSUPP</c> when the
    /// handler lacks the interface, in a tree that implements none of them.
    /// </summary>
    [Fact]
    public async Task MissingCapabilityIsEopnotsupp()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: null, filesystem: new BareFilesystem());
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid file = await session.WalkAsync("plain", Ct);
        await using (file.ConfigureAwait(false))
        {
            await Refused(async () => await session.Messages.LockAsync(
                new Tlock(0, file.Fid, new LockRequest(LockType.WriteLock, LockFlags.None, 0, 0, 1, "c")), Ct));

            await Refused(async () => await session.Messages.GetlockAsync(
                new Tgetlock(0, file.Fid, LockType.WriteLock, 0, 0, 1, "c"), Ct));

            await Refused(async () => await session.Messages.XattrwalkAsync(
                new Txattrwalk(0, file.Fid, 60, "user.x"), Ct));

            await Refused(async () => await session.Messages.XattrcreateAsync(
                new Txattrcreate(0, file.Fid, "user.x", 1, XattrFlags.None), Ct));

            await Refused(async () => await session.Messages.StatfsAsync(new Tstatfs(0, file.Fid), Ct));

            await Refused(async () => await session.Messages.LinkAsync(
                new Tlink(0, session.Root.Fid, file.Fid, "alias"), Ct));
        }
    }

    /// <summary>A handler that is not a symlink answers <c>Treadlink</c> with EINVAL, not a crash.</summary>
    [Fact]
    public async Task ReadlinkOnAPlainFileIsRefused()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: null, filesystem: new BareFilesystem());
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid file = await session.WalkAsync("plain", Ct);
        await using (file.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.ReadlinkAsync(new Treadlink(0, file.Fid), Ct));

            Assert.Equal(Errno.EINVAL, refusal.Error.Errno);
        }
    }

    /// <summary>A handler that throws something unexpected is EIO, and the session survives it.</summary>
    [Fact]
    public async Task AThrowingHandlerIsIoErrorAndTheSessionLives()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: null, filesystem: new BareFilesystem());
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePException failure = await Assert.ThrowsAsync<NinePException>(
            async () => await session.WalkAsync("explode", Ct));

        Assert.Equal(Errno.EIO, failure.Error.Errno);
        Assert.Equal("i/o error", failure.Error.Ename);

        // The connection is still usable: one bad handler is not a denial of service.
        NinePFid still = await session.WalkAsync("plain", Ct);
        await still.DisposeAsync();
    }

    private static async Task Refused(Func<Task> request)
    {
        NinePException refusal = await Assert.ThrowsAsync<NinePException>(async () => await request());

        Assert.Equal(Errno.EOPNOTSUPP, refusal.Error.Errno);
    }

    /// <summary>A tree of handlers that implement nothing optional at all.</summary>
    private sealed class BareFilesystem : IFilesystem, IDirectoryHandler
    {
        private readonly BareFile _plain = new();

        public Qid Qid => new(QidType.QTDIR, 0, 1);

        public ValueTask<IDirectoryHandler> AttachAsync(
            Identity identity, string aname, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IDirectoryHandler>(this);

        public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new Attr
            {
                Qid = Qid,
                Kind = FileKind.Directory,
                Perm = 0x1FF,
                UserName = "glenda",
                GroupName = "glenda",
            });

        public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default) =>
            throw new NinePException(NinePError.FromErrno(Errno.EOPNOTSUPP));

        public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<IHandler?> LookupAsync(string name, CancellationToken cancellationToken = default) =>
            name switch
            {
                "plain" => ValueTask.FromResult<IHandler?>(_plain),
                "explode" => throw new InvalidOperationException("a handler bug"),
                _ => ValueTask.FromResult<IHandler?>(null),
            };

        public ValueTask<DirectoryListing> ReadDirAsync(
            ulong cursor, int max, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DirectoryListing([], cursor, true));

        public ValueTask<IHandler> CreateAsync(
            CreateRequest request, CancellationToken cancellationToken = default) =>
            throw new NinePException(NinePError.FromErrno(Errno.EROFS));

        public ValueTask RemoveAsync(
            string name, FileKind kind, CancellationToken cancellationToken = default) =>
            throw new NinePException(NinePError.FromErrno(Errno.EROFS));

        public ValueTask RenameAsync(
            string oldName, IDirectoryHandler newParent, string newName, CancellationToken cancellationToken = default) =>
            throw new NinePException(NinePError.FromErrno(Errno.EROFS));
    }

    /// <summary>A regular file with no optional capability on it whatsoever.</summary>
    private sealed class BareFile : IFileHandler
    {
        public Qid Qid => new(QidType.QTFILE, 0, 2);

        public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new Attr
            {
                Qid = Qid,
                Kind = FileKind.File,
                Perm = 0x1B6,
                UserName = "glenda",
                GroupName = "glenda",
            });

        public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<IOpenFile> OpenAsync(
            OpenMode mode, OpenFlags flags, CancellationToken cancellationToken = default) =>
            throw new NinePException(NinePError.FromErrno(Errno.EOPNOTSUPP));
    }
}
