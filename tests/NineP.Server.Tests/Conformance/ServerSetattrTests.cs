using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

[Trait("Category", "Conformance")]
public sealed class ServerSetattrTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    [Fact]
    public async Task AValidMaskOfZeroChangesNothingAndDoesNotFsync()
    {
        RecordingDirectory directory = new();
        await using ServerHarness h = await ServerHarness.StartAsync(filesystem: directory);
        await using NinePSession s = await h.ConnectAsync(Dialect.P9_2000_L);
        await using NinePFid fid = await s.WalkAsync("/", Ct);
        Attr before = await fid.GetAttrAsync(Ct);
        await s.Messages.SetattrAsync(new Tsetattr(0, fid.Fid, SetAttrMask.None, uint.MaxValue,
            uint.MaxValue, uint.MaxValue, ulong.MaxValue, new TimeSpec(1, 2), new TimeSpec(3, 4)), Ct);
        Assert.Null(directory.LastUpdate);
        Assert.Equal(0, directory.Fsyncs);
        Assert.Equal(before, await fid.GetAttrAsync(Ct));
        await BoundaryTests.Error(Errno.EBADF, async () => await s.Messages.SetattrAsync(
            new Tsetattr(0, 987, SetAttrMask.None, 0, 0, 0, 0, default, default), Ct));
    }

    [Theory]
    [InlineData(SetAttrMask.ATimeSet)]
    [InlineData(SetAttrMask.MTimeSet)]
    [InlineData(SetAttrMask.ATimeSet | SetAttrMask.MTimeSet)]
    [InlineData(SetAttrMask.ATimeSet | SetAttrMask.Mode)]
    [InlineData(SetAttrMask.MTimeSet | SetAttrMask.Size)]
    public async Task OrphanTimeModifiersAreEinvalWithoutMutation(SetAttrMask mask)
    {
        RecordingDirectory directory = new();
        await using ServerHarness h = await ServerHarness.StartAsync(filesystem: directory);
        await using NinePSession s = await h.ConnectAsync(Dialect.P9_2000_L);
        await using NinePFid fid = await s.WalkAsync("/", Ct);
        Attr before = await fid.GetAttrAsync(Ct);
        await BoundaryTests.Error(Errno.EINVAL, async () => await s.Messages.SetattrAsync(
            new Tsetattr(0, fid.Fid, mask, 0x180, 0, 0, 0, default, default), Ct));
        Assert.Null(directory.LastUpdate);
        Assert.Equal(0, directory.Fsyncs);
        Assert.Equal(before, await fid.GetAttrAsync(Ct));
    }

    [Theory]
    [InlineData(Dialect.P9_2000, 1ul)]
    [InlineData(Dialect.P9_2000_u, 1ul)]
    [InlineData(Dialect.P9_2000_L, 0ul)]
    [InlineData(Dialect.P9_2000_L, 1ul)]
    public async Task DirectorySizeIsRefusedBeforeAnyPartOfTheUpdate(Dialect dialect, ulong size)
    {
        RecordingDirectory directory = new();
        await using ServerHarness h = await ServerHarness.StartAsync(filesystem: directory);
        await using NinePSession s = await h.ConnectAsync(dialect);
        await using NinePFid fid = await s.WalkAsync("/", Ct);
        Attr before = await fid.GetAttrAsync(Ct);
        await BoundaryTests.Error(Errno.EISDIR, async () =>
        {
            if (dialect == Dialect.P9_2000_L)
            {
                await fid.SetAttrAsync(new SetAttr { Size = size, Perm = Perms.P0600 }, Ct);
            }
            else
            {
                await s.Messages.WstatAsync(new Twstat(0, fid.Fid,
                    StatRecord.DontTouch with { Length = size, Mode = ModeBits.DMDIR | 0x180 }), Ct);
            }
        });
        Assert.Null(directory.LastUpdate);
        Assert.Equal(before, await fid.GetAttrAsync(Ct));
    }

    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    public async Task LegacyDirectoryLengthZeroReachesTheHandler(Dialect dialect)
    {
        RecordingDirectory directory = new();
        await using ServerHarness h = await ServerHarness.StartAsync(filesystem: directory);
        await using NinePSession s = await h.ConnectAsync(dialect);
        await using NinePFid fid = await s.WalkAsync("/", Ct);
        await fid.SetAttrAsync(new SetAttr { Size = 0 }, Ct);
        Assert.Equal(0ul, directory.LastUpdate!.Size);
        Assert.Equal(0, directory.Fsyncs);
    }

    // Deliberately permissive: a handler-side rejection must not conceal a missing core check.
    private sealed class RecordingDirectory : IDirectoryHandler, IFilesystem
    {
        private readonly MemoryFilesystem _tree = new();
        public SetAttr? LastUpdate { get; private set; }
        public int Fsyncs { get; private set; }
        public Qid Qid => _tree.Root.Qid;
        public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default) => _tree.Root.GetAttrAsync(cancellationToken);
        public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default)
        {
            LastUpdate = update;
            return ValueTask.CompletedTask;
        }
        public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default)
        {
            Fsyncs++;
            return ValueTask.CompletedTask;
        }
        public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<IDirectoryHandler> AttachAsync(Identity identity, string aname, CancellationToken cancellationToken = default) => ValueTask.FromResult<IDirectoryHandler>(this);
        public ValueTask<IHandler?> LookupAsync(string name, CancellationToken cancellationToken = default) => _tree.Root.LookupAsync(name, cancellationToken);
        public ValueTask<DirectoryListing> ReadDirAsync(ulong cursor, int max, CancellationToken cancellationToken = default) => _tree.Root.ReadDirAsync(cursor, max, cancellationToken);
        public ValueTask<IHandler> CreateAsync(CreateRequest request, CancellationToken cancellationToken = default) => _tree.Root.CreateAsync(request, cancellationToken);
        public ValueTask RemoveAsync(string name, FileKind kind, CancellationToken cancellationToken = default) => _tree.Root.RemoveAsync(name, kind, cancellationToken);
        public ValueTask RenameAsync(string oldName, IDirectoryHandler newParent, string newName, CancellationToken cancellationToken = default) => _tree.Root.RenameAsync(oldName, newParent, newName, cancellationToken);
    }
}
