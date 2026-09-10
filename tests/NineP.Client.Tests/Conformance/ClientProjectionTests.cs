using NineP.Client.Tests;
using NineP.Client.Tests.Conformance;
using NineP.Protocol;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.Protocol.Negotiation;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using Xunit;

namespace NineP.Client.Tests.Conformance;

/// <summary>
/// Reference §8 rules 15 to 18, the client's half of dialect projection honesty: a request that
/// names something the negotiated dialect cannot carry is refused <b>before</b> anything is
/// written, and a reply is read only as far as the server said it was filled in. Every refusal
/// here is asserted against a <see cref="FakeNinePServer"/> that is then made to answer an
/// ordinary request, which is what proves the refused call put no frame on the wire: a stray
/// <c>Tlopen</c> or <c>Twstat</c> would be what the next read decoded.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class ClientProjectionTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 15: <c>.L</c> has no <c>ORCLOSE</c>, so <c>OpenAsync</c> refuses remove-on-close before
    /// the <c>Tlopen</c> is built. It used to be dropped, and the caller got an open file that was
    /// still there after the clunk.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task TheRemoveOnCloseFlagNeverReachesADotLOpen()
    {
        await using Harness harness = await Harness.StartAsync(Dialect.P9_2000_L);
        NinePFid fid = harness.Fid();

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await fid.OpenAsync(OpenMode.Write, OpenFlags.RemoveOnClose, Ct));

        Assert.Equal(Errno.EOPNOTSUPP, refusal.Error.Errno);
        await harness.AssertNothingWasSentAsync();
    }

    /// <summary>Rule 15: the same refusal on the create path, which opens the new fid as well.</summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task TheRemoveOnCloseFlagNeverReachesADotLCreate()
    {
        await using Harness harness = await Harness.StartAsync(Dialect.P9_2000_L);
        NinePFid fid = harness.Fid();

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await fid.CreateAsync("scratch", 0x1A4, OpenMode.Write, OpenFlags.RemoveOnClose, Ct));

        Assert.Equal(Errno.EOPNOTSUPP, refusal.Error.Errno);
        await harness.AssertNothingWasSentAsync();
    }

    /// <summary>
    /// Rule 15: <c>O_EXCL</c>, <c>O_DIRECTORY</c> and <c>O_NOFOLLOW</c> have no <c>Topen.mode</c>
    /// bit, so a 9P2000 or <c>.u</c> open naming one is refused rather than sent without it — the
    /// caller of an <c>O_NOFOLLOW</c> open in particular asked not to be handed a symlink's target.
    /// </summary>
    /// <param name="dialect">The dialect whose <c>mode[1]</c> cannot carry the flag.</param>
    /// <param name="flag">The .L-only flag the caller asked for.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Dialect.P9_2000, OpenFlags.Exclusive)]
    [InlineData(Dialect.P9_2000, OpenFlags.Directory)]
    [InlineData(Dialect.P9_2000, OpenFlags.NoFollow)]
    [InlineData(Dialect.P9_2000_u, OpenFlags.Exclusive)]
    [InlineData(Dialect.P9_2000_u, OpenFlags.Directory)]
    [InlineData(Dialect.P9_2000_u, OpenFlags.NoFollow)]
    public async Task LinuxOnlyOpenFlagsNeverReachA9P2000Server(Dialect dialect, OpenFlags flag)
    {
        await using Harness harness = await Harness.StartAsync(dialect);
        NinePFid fid = harness.Fid();

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await fid.OpenAsync(OpenMode.Read, flag, Ct));

        Assert.Equal(Errno.EOPNOTSUPP, refusal.Error.Errno);
        await harness.AssertNothingWasSentAsync();
    }

    /// <summary>
    /// Rule 16: an <c>OEXEC</c> open reaches a <c>.L</c> server as <c>O_RDONLY</c>. Access mode 3
    /// is <c>O_NOACCESS</c>, and a client that sent it would be asking for a handle that can
    /// neither read nor write.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task ExecOpensReadOnlyOnADotLServer()
    {
        await using Harness harness = await Harness.StartAsync(Dialect.P9_2000_L);
        NinePFid fid = harness.Fid();

        Task opening = fid.OpenAsync(OpenMode.Exec, OpenFlags.None, Ct).AsTask();
        Tlopen sent = await harness.Server.ReadAsync<Tlopen>(Ct);
        await harness.Server.WriteAsync(new Rlopen(sent.Tag, fid.Qid, 0), Ct);
        await opening;

        Assert.Equal(0u, sent.Flags);
    }

    /// <summary>
    /// Rule 15: stat(5) forbids setting <c>atime</c> through a <c>Twstat</c>. The field used to be
    /// dropped, which left the all-don't-touch record — stat(5)'s fsync — as the thing that went
    /// out, so the caller was told its update had been applied when what the server had been asked
    /// for was a flush.
    /// </summary>
    /// <param name="dialect">The non-.L dialect the update would be sent in.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    public async Task AnAtimeUpdateNeverReachesAWstatServer(Dialect dialect)
    {
        await using Harness harness = await Harness.StartAsync(dialect);
        NinePFid fid = harness.Fid();

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await fid.SetAttrAsync(new SetAttr { ATime = new TimeSpec(1, 0) }, Ct));

        Assert.Equal(Errno.EPERM, refusal.Error.Errno);
        await harness.AssertNothingWasSentAsync();
    }

    /// <summary>
    /// Rule 15: a <c>Twstat</c> carries a time value, never "the server's clock", so the three
    /// <c>ToNow</c> flags have no wstat spelling. This is the case the reference calls out by
    /// name: an update of <c>{ATimeToNow = true}</c> projected to
    /// <see cref="StatRecord.DontTouch"/> and went out as an fsync.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task AServerClockUpdateIsNotSentAsAnFsync()
    {
        await using Harness harness = await Harness.StartAsync(Dialect.P9_2000);
        NinePFid fid = harness.Fid();

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await fid.SetAttrAsync(new SetAttr { ATimeToNow = true }, Ct));

        Assert.Equal(Errno.EINVAL, refusal.Error.Errno);

        // The projector no longer reaches the record at all, which is the half of the rule the
        // wire cannot show: DontTouch is a request to flush the file, not an empty update.
        Assert.Throws<NinePException>(
            () => AttrProjector.ToWstat(new SetAttr { ATimeToNow = true }, Dialect.P9_2000));

        await harness.AssertNothingWasSentAsync();
    }

    /// <summary>
    /// Rule 15: <c>n_gid</c> is a <c>.u</c> field. A numeric group has nowhere to go in a plain
    /// 9P2000 stat record, so it is refused; in <c>.u</c> the same update is sent.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task ANumericGroupIsRefusedOnPlain9P2000()
    {
        await using (Harness legacy = await Harness.StartAsync(Dialect.P9_2000))
        {
            NinePFid fid = legacy.Fid();

            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await fid.SetAttrAsync(new SetAttr { Gid = 42 }, Ct));

            Assert.Equal(Errno.EINVAL, refusal.Error.Errno);
            await legacy.AssertNothingWasSentAsync();
        }

        await using Harness unix = await Harness.StartAsync(Dialect.P9_2000_u);
        NinePFid target = unix.Fid();

        Task setting = target.SetAttrAsync(new SetAttr { Gid = 42 }, Ct).AsTask();
        Twstat sent = await unix.Server.ReadAsync<Twstat>(Ct);
        await unix.Server.WriteAsync(new Rwstat(sent.Tag), Ct);
        await setting;

        Assert.Equal(42u, sent.Stat.NGid);
    }

    /// <summary>
    /// Rule 15: <c>Tsetattr</c> has neither a name nor a group-name field, so <c>.L</c> refuses
    /// both rather than sending an update with the only field the caller named missing.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task ATextualGroupAndANameAreRefusedOnDotL()
    {
        await using Harness harness = await Harness.StartAsync(Dialect.P9_2000_L);
        NinePFid fid = harness.Fid();

        NinePException group = await Assert.ThrowsAsync<NinePException>(
            async () => await fid.SetAttrAsync(new SetAttr { GroupName = "wheel" }, Ct));
        Assert.Equal(Errno.EINVAL, group.Error.Errno);

        NinePException name = await Assert.ThrowsAsync<NinePException>(
            async () => await fid.SetAttrAsync(new SetAttr { Name = "renamed" }, Ct));
        Assert.Equal(Errno.EINVAL, name.Error.Errno);

        await harness.AssertNothingWasSentAsync();
    }

    /// <summary>
    /// Rules 15 and 19: <c>Tlcreate.mode</c> is a POSIX mode word, in which <c>DMDIR</c>,
    /// <c>DMAPPEND</c>, <c>DMEXCL</c> and <c>DMTMP</c> have no bit, so a <c>.L</c> create asking
    /// for one is refused before the message is built. The server would have masked the bit off
    /// and answered <c>Rlcreate</c> for a plain file.
    /// </summary>
    /// <param name="bit">The <c>Tcreate.perm</c> bit that has no .L spelling.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(ModeBits.DMAPPEND)]
    [InlineData(ModeBits.DMEXCL)]
    [InlineData(ModeBits.DMTMP)]
    [InlineData(ModeBits.DMDIR)]
    public async Task TheFileFlagsNeverReachADotLCreate(uint bit)
    {
        await using Harness harness = await Harness.StartAsync(Dialect.P9_2000_L);
        NinePFid fid = harness.Fid();

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await fid.CreateAsync("flagged", bit | 0x1A4, OpenMode.Write, OpenFlags.None, Ct));

        Assert.Equal(Errno.EOPNOTSUPP, refusal.Error.Errno);
        await harness.AssertNothingWasSentAsync();
    }

    /// <summary>
    /// Rule 19: <c>Tsetattr.mode</c> is a POSIX mode word too, so <see cref="SetAttr.Flags"/> has
    /// no <c>.L</c> spelling and is refused rather than sent with the only field the caller named
    /// missing.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task TheFileFlagsNeverReachADotLSetattr()
    {
        await using Harness harness = await Harness.StartAsync(Dialect.P9_2000_L);
        NinePFid fid = harness.Fid();

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await fid.SetAttrAsync(new SetAttr { Flags = FileFlags.Append }, Ct));

        Assert.Equal(Errno.EINVAL, refusal.Error.Errno);
        await harness.AssertNothingWasSentAsync();
    }

    /// <summary>
    /// Rule 19: a <c>Twstat</c> mode word carries the permission bits and the file flags
    /// together, so an update stating only one half is completed from the file's own record —
    /// a <c>Tstat</c> goes out first, as Plan 9's <c>chmod</c> and v9fs do. A chmod therefore
    /// never clears <c>DMAPPEND</c>, and setting a flag never zeroes the permissions; an update
    /// stating both halves goes straight out.
    /// <b>Mutation:</b> drop the <c>Tstat</c> from <c>NinePFid.SetAttrAsync</c> and the projector
    /// refuses the half-stated word, so both halves of this test fail.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task AHalfStatedModeWordIsCompletedFromTheRecord()
    {
        await using Harness harness = await Harness.StartAsync(Dialect.P9_2000);
        NinePFid fid = harness.Fid();

        // A chmod on an append-only file keeps the file append-only.
        Task chmod = fid.SetAttrAsync(new SetAttr { Perm = 0x1A4 }, Ct).AsTask();
        Tstat stat = await harness.Server.ReadAsync<Tstat>(Ct);
        await harness.Server.WriteAsync(
            new Rstat(stat.Tag, Record(ModeBits.DMAPPEND | 0x1ED)), Ct);
        Twstat sent = await harness.Server.ReadAsync<Twstat>(Ct);
        await harness.Server.WriteAsync(new Rwstat(sent.Tag), Ct);
        await chmod;

        Assert.Equal(ModeBits.DMAPPEND | 0x1A4u, sent.Stat.Mode);

        // Setting a flag keeps the permission bits, and states the whole flag set.
        Task flagging = fid.SetAttrAsync(new SetAttr { Flags = FileFlags.Exclusive }, Ct).AsTask();
        stat = await harness.Server.ReadAsync<Tstat>(Ct);
        await harness.Server.WriteAsync(
            new Rstat(stat.Tag, Record(ModeBits.DMAPPEND | 0x1ED)), Ct);
        sent = await harness.Server.ReadAsync<Twstat>(Ct);
        await harness.Server.WriteAsync(new Rwstat(sent.Tag), Ct);
        await flagging;

        Assert.Equal(ModeBits.DMEXCL | 0x1EDu, sent.Stat.Mode);

        // Both halves stated: no Tstat, the Twstat is the next frame.
        Task whole = fid.SetAttrAsync(new SetAttr { Perm = 0x1B6, Flags = FileFlags.Temporary }, Ct).AsTask();
        sent = await harness.Server.ReadAsync<Twstat>(Ct);
        await harness.Server.WriteAsync(new Rwstat(sent.Tag), Ct);
        await whole;

        Assert.Equal(ModeBits.DMTMP | 0x1B6u, sent.Stat.Mode);
    }

    /// <summary>A stat record with the given mode word, for the fake server to answer with.</summary>
    /// <param name="mode">The mode word.</param>
    /// <returns>The record.</returns>
    private static StatRecord Record(uint mode) => StatRecord.DontTouch with
    {
        Qid = new Qid(QidType.QTFILE, 0, 1),
        Mode = mode,
        Name = "f",
        Uid = "glenda",
        Gid = "glenda",
        Muid = "glenda",
    };

    /// <summary>
    /// Rule 17: a qid marked <c>QTSYMLINK</c> is a symlink whatever the dialect, because
    /// <c>Attr.Kind</c> and the qid type byte must agree. Plain 9P2000 has no extension field, so
    /// the target stays unknown — which is honest, where "a plain file" was not.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task A9P2000SymlinkQidIsReportedAsASymlink()
    {
        await using Harness harness = await Harness.StartAsync(Dialect.P9_2000);
        NinePFid fid = harness.Fid();

        Task<Attr> stating = fid.GetAttrAsync(Ct).AsTask();
        Tstat request = await harness.Server.ReadAsync<Tstat>(Ct);
        StatRecord record = new()
        {
            Qid = new Qid(QidType.QTSYMLINK, 1, 7),
            Mode = 0x1FF,
            Name = "link",
            Uid = "glenda",
            Gid = "sys",
            Muid = "glenda",
        };
        await harness.Server.WriteAsync(new Rstat(request.Tag, record), Ct);

        Attr attr = await stating;

        Assert.Equal(FileKind.Symlink, attr.Kind);
        Assert.Equal(QidType.QTSYMLINK, attr.Qid.Type);
        Assert.Null(attr.SymlinkTarget);
    }

    /// <summary>
    /// Rule 17: the same qid in a directory listing. <c>ReadDirAsync</c> projects each stat record
    /// through the same code, so a listing that called a symlink a plain file disagreed with the
    /// <c>DirEntry.Qid</c> printed beside it.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task A9P2000SymlinkQidIsASymlinkInAListingToo()
    {
        await using Harness harness = await Harness.StartAsync(Dialect.P9_2000);
        NinePFid fid = harness.Fid();

        Task<List<DirEntry>> listing = ListAsync(fid);
        Tread request = await harness.Server.ReadAsync<Tread>(Ct);
        StatRecord record = new()
        {
            Qid = new Qid(QidType.QTSYMLINK, 1, 7),
            Mode = 0x1FF,
            Name = "link",
            Uid = "glenda",
            Gid = "sys",
            Muid = "glenda",
        };
        await harness.Server.WriteAsync(new Rread(request.Tag, Pack(record)), Ct);

        Tread end = await harness.Server.ReadAsync<Tread>(Ct);
        await harness.Server.WriteAsync(new Rread(end.Tag, ReadOnlyMemory<byte>.Empty), Ct);

        DirEntry entry = Assert.Single(await listing);

        Assert.Equal("link", entry.Name);
        Assert.Equal(FileKind.Symlink, entry.Kind);
        Assert.Equal(QidType.QTSYMLINK, entry.Qid.Type);
    }

    /// <summary>
    /// Rule 17: a client reads only what <c>Rgetattr.valid</c> marks. A reply that marks nothing
    /// but the qid is a server saying "I do not know", and reading its zero padding turned that
    /// into a file with nlink 0, unknown-as-root ids and an epoch mtime.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task AnUnmarkedGetattrFieldKeepsItsDefault()
    {
        await using Harness harness = await Harness.StartAsync(Dialect.P9_2000_L);
        NinePFid fid = harness.Fid();

        Task<Attr> stating = fid.GetAttrAsync(Ct).AsTask();
        Tgetattr request = await harness.Server.ReadAsync<Tgetattr>(Ct);
        Rgetattr reply = new(
            request.Tag,
            GetAttrMask.Size,
            new Qid(QidType.QTDIR, 3, 9),
            ModeBits.S_IFREG | 0x1A4,
            1000,
            1001,
            0,
            0,
            4096,
            512,
            8,
            new TimeSpec(1, 1),
            new TimeSpec(2, 2),
            new TimeSpec(3, 3),
            new TimeSpec(4, 4),
            5,
            6);
        await harness.Server.WriteAsync(reply, Ct);

        Attr attr = await stating;

        // Marked, so read.
        Assert.Equal(4096ul, attr.Size);

        // Unmarked, so the Attr default — and the kind comes from the qid, which is always valid.
        Assert.Equal(FileKind.Directory, attr.Kind);
        Assert.Equal(0u, attr.Perm);
        Assert.Equal(1ul, attr.NLink);
        Assert.Equal(Constants.NONUNAME, attr.Uid);
        Assert.Equal(Constants.NONUNAME, attr.Gid);
        Assert.Equal(default, attr.MTime);
        Assert.Equal(0ul, attr.Blocks);
        Assert.Equal(0ul, attr.DataVersion);
    }

    /// <summary>
    /// Rule 18: the answer must be the dialect that was just offered, or plain <c>9P2000</c> for a
    /// suffixed offer. A <em>higher</em> dialect than was offered would run the session in one the
    /// client never proposed, and <c>.u</c> answering a <c>.L</c> offer is neither the offer nor
    /// the base version — both used to be accepted because only <c>MinDialect</c> was consulted.
    /// </summary>
    /// <param name="offered">The dialect the client offers.</param>
    /// <param name="answered">The version string the server replies with.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Dialect.P9_2000, Constants.Version9P2000L)]
    [InlineData(Dialect.P9_2000, Constants.Version9P2000u)]
    [InlineData(Dialect.P9_2000_u, Constants.Version9P2000L)]
    [InlineData(Dialect.P9_2000_L, Constants.Version9P2000u)]
    public async Task AnAnswerThatIsNotTheOfferIsAVersionError(Dialect offered, string answered)
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        ClientOptions options = new() { Dialects = [offered], MinDialect = Dialect.P9_2000 };
        Task<NinePSession> connecting = NinePClient.ConnectAsync(wire, options, Ct).AsTask();
        await server.NegotiateAsync(answered, cancellationToken: Ct);

        NinePVersionException failure =
            await Assert.ThrowsAsync<NinePVersionException>(async () => await connecting);

        Assert.Equal(answered, failure.ServerVersion);
        Assert.Contains("to an offer of", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rule 18: the one downgrade version(5):70-78 allows is a suffixed offer answered with the
    /// base version, and <see cref="ClientOptions.MinDialect"/> is what gates it. Both halves are
    /// here: the same exchange is accepted under a floor of <c>9P2000</c> and refused under
    /// <c>.u</c>.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task TheSuffixStrippingDowngradeIsTheOnlyOne()
    {
        (INinePConnection allowed, FakeNinePServer floor9P2000) = FakeNinePServer.CreatePair();
        await using (floor9P2000)
        {
            Task<NinePSession> connecting = NinePClient
                .ConnectAsync(
                    allowed,
                    new ClientOptions { Dialects = [Dialect.P9_2000_L], MinDialect = Dialect.P9_2000 },
                    Ct)
                .AsTask();
            await floor9P2000.NegotiateAsync(Constants.Version9P2000, cancellationToken: Ct);

            await using NinePSession session = await connecting;
            Assert.Equal(Dialect.P9_2000, session.Dialect);
        }

        (INinePConnection refused, FakeNinePServer floorDotU) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = floorDotU;

        Task<NinePSession> refusing = NinePClient
            .ConnectAsync(
                refused,
                new ClientOptions { Dialects = [Dialect.P9_2000_L], MinDialect = Dialect.P9_2000_u },
                Ct)
            .AsTask();
        await floorDotU.NegotiateAsync(Constants.Version9P2000, cancellationToken: Ct);

        NinePVersionException failure =
            await Assert.ThrowsAsync<NinePVersionException>(async () => await refusing);
        Assert.Contains("below the configured floor", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rule 18, and rule 12 through it: an <c>Rversion</c> with no <c>Tversion</c> outstanding is
    /// an unexpected reply and terminates the session, as the multiplexer's class doc promises for
    /// every unexpected reply. It used to be discarded, which left the session running on a stream
    /// whose sender had just claimed to renegotiate the framing.
    /// </summary>
    /// <returns>The running test.</returns>
    [Fact]
    public async Task AnUnsolicitedRversionTerminatesTheSession()
    {
        await using Harness harness = await Harness.StartAsync(Dialect.P9_2000_L);

        Task<Rclunk> request = harness.Session.Messages.ClunkAsync(new Tclunk(0, 1), Ct).AsTask();
        await harness.Server.ReadAsync<Tclunk>(Ct);

        await harness.Server.WriteAsync(
            new Rversion(Constants.NOTAG, 8192, Constants.Version9P2000L), Ct);

        NinePProtocolException failure =
            await Assert.ThrowsAsync<NinePProtocolException>(async () => await request);

        Assert.Equal(ProtocolErrorKind.Type, failure.Kind);
        Assert.NotNull(harness.Session.Multiplexer.Termination);
    }

    private static async Task<List<DirEntry>> ListAsync(NinePFid fid)
    {
        List<DirEntry> entries = [];
        await foreach (DirEntry entry in fid.ReadDirAsync(
            TestDeadlines.Wrap(TestContext.Current.CancellationToken)))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static byte[] Pack(StatRecord record)
    {
        byte[] packed = new byte[512];
        WireWriter writer = new(packed);
        StatCodec.WriteRecord(ref writer, in record, Dialect.P9_2000);
        return packed.AsSpan(0, writer.Position).ToArray();
    }

    /// <summary>
    /// A negotiated session over a <see cref="FakeNinePServer"/> that writes nothing on its own,
    /// so the only frames on the wire are the ones the test's own calls put there.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private Harness(NinePSession session, FakeNinePServer server)
        {
            Session = session;
            Server = server;
        }

        public NinePSession Session { get; }

        public FakeNinePServer Server { get; }

        public static async Task<Harness> StartAsync(Dialect dialect)
        {
            (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
            ClientOptions options = new() { Dialects = [dialect], Msize = 8192 };

            Task<NinePSession> connecting = NinePClient
                .ConnectAsync(wire, options, CancellationToken.None).AsTask();

            await server.NegotiateAsync(Negotiator.VersionString(dialect));

            return new Harness(await connecting, server);
        }

        /// <summary>A fid the test drives directly, without the attach the server would answer.</summary>
        /// <returns>A fid bound to a directory qid.</returns>
        public NinePFid Fid() => new(Session, Session.RentFid(), new Qid(QidType.QTDIR, 0, 1));

        /// <summary>
        /// Proves the refused call wrote nothing: the next frame the server reads is the
        /// <c>Tclunk</c> this method sends, so a message the refusal had let through would be what
        /// the decode found instead.
        /// </summary>
        /// <returns>A task that completes once the probe has been answered.</returns>
        public async Task AssertNothingWasSentAsync()
        {
            CancellationToken token = TestDeadlines.Wrap(TestContext.Current.CancellationToken);
            Task<Rclunk> probe = Session.Messages.ClunkAsync(new Tclunk(0, 0xABCD), token).AsTask();

            Tclunk sent = await Server.ReadAsync<Tclunk>(token);
            Assert.Equal(0xABCDu, sent.Fid);

            await Server.WriteAsync(new Rclunk(sent.Tag), token);
            await probe;
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Session.DisposeAsync();
        }
    }
}
