using System.Buffers;
using System.Buffers.Binary;
using NineP.Client.Tests;
using NineP.Client.Tests.Conformance;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using Xunit;

namespace NineP.Client.Tests.Robustness;

/// <summary>
/// Reference §8 rule 13: the five replies a client must refuse however well-formed their frames
/// are. Each is a server claiming to have done more than it was asked to do, and a client that
/// believed one would read past a buffer, resume a listing at the wrong cookie, or report a write
/// that never happened.
/// </summary>
[Trait("Category", "Robustness")]
public sealed class ClientProtocolErrorTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 56: an <c>Rread</c> over its <c>Tread.count</c>, an <c>Rwrite</c> over its
    /// <c>Twrite.count</c>, and an <c>Rreaddir</c> over its <c>Treaddir.count</c> are all refused.
    /// </summary>
    [Fact]
    public async Task OverCountRepliesAreRejected()
    {
        await using Harness harness = await Harness.StartAsync();
        NinePFid fid = harness.Fid();

        byte[] buffer = new byte[8];
        Task<int> reading = fid.ReadAsync(0, buffer, Ct).AsTask();
        Tread read = await harness.Server.ReadAsync<Tread>(Ct);
        await harness.Server.WriteAsync(new Rread(read.Tag, new byte[read.Count + 1]), Ct);

        NinePProtocolException overRead =
            await Assert.ThrowsAsync<NinePProtocolException>(async () => await reading);
        Assert.Equal(ProtocolErrorKind.Bounds, overRead.Kind);

        Task<int> writing = fid.WriteAsync(0, new byte[4], Ct).AsTask();
        Twrite write = await harness.Server.ReadAsync<Twrite>(Ct);
        await harness.Server.WriteAsync(new Rwrite(write.Tag, (uint)write.Data.Length + 1), Ct);

        NinePProtocolException overWrite =
            await Assert.ThrowsAsync<NinePProtocolException>(async () => await writing);
        Assert.Equal(ProtocolErrorKind.Bounds, overWrite.Kind);

        Task listing = Drain(fid);
        Treaddir readdir = await harness.Server.ReadAsync<Treaddir>(Ct);
        await harness.Server.WriteAsync(new Rreaddir(readdir.Tag, new byte[readdir.Count + 1]), Ct);

        NinePProtocolException overReaddir =
            await Assert.ThrowsAsync<NinePProtocolException>(async () => await listing);
        Assert.Equal(ProtocolErrorKind.Bounds, overReaddir.Kind);
    }

    /// <summary>Rule 56: more qids than names in an <c>Rwalk</c> is a protocol error, not a walk.</summary>
    [Fact]
    public async Task MoreQidsThanNamesIsRejected()
    {
        await using Harness harness = await Harness.StartAsync();
        NinePFid fid = harness.Fid();

        Task<NinePFid> walking = fid.WalkAsync(["a"], Ct).AsTask();

        // The clone that opens every walk, answered as walk(5) requires: no qids at all.
        Twalk clone = await harness.Server.ReadAsync<Twalk>(Ct);
        Assert.Empty(clone.Wnames);
        await harness.Server.WriteAsync(new Rwalk(clone.Tag, []), Ct);

        Twalk walk = await harness.Server.ReadAsync<Twalk>(Ct);
        Qid[] tooMany = [new Qid(QidType.QTDIR, 0, 1), new Qid(QidType.QTFILE, 0, 2)];
        await harness.Server.WriteAsync(new Rwalk(walk.Tag, tooMany), Ct);

        // The client throws away the clone it could not walk; answering that clunk is what lets
        // the refusal reach the caller rather than the disposal blocking on a silent server.
        Tclunk clunk = await harness.Server.ReadAsync<Tclunk>(Ct);
        await harness.Server.WriteAsync(new Rclunk(clunk.Tag), Ct);

        NinePProtocolException failure =
            await Assert.ThrowsAsync<NinePProtocolException>(async () => await walking);
        Assert.Equal(ProtocolErrorKind.NWName, failure.Kind);
    }

    /// <summary>Rule 56: a <c>stat[n]</c> whose inner size disagrees with the outer count is refused.</summary>
    [Fact]
    public async Task BadStatInnerSizeIsRejected()
    {
        await using Harness harness = await Harness.StartAsync(Constants.Version9P2000);
        NinePFid fid = harness.Fid();

        Task<Attr> stating = fid.GetAttrAsync(Ct).AsTask();
        Tstat request = await harness.Server.ReadAsync<Tstat>(Ct);

        StatRecord record = new() { Name = "hello", Uid = "glenda", Gid = "glenda", Muid = "glenda" };
        ArrayBufferWriter<byte> frame = new();
        Rstat reply = new(request.Tag, record);
        MessageCodec.Encode(frame, in reply, Dialect.P9_2000);

        // size[4] type[1] tag[2] then the outer n[2] and the record's own size[2], which stat(5)
        // lists under BUGS. Bending the inner one alone is the disagreement rule 2 rejects.
        byte[] corrupted = frame.WrittenSpan.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            corrupted.AsSpan(9), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(corrupted.AsSpan(9)) - 1));

        await harness.Server.WriteRawAsync(corrupted, Ct);

        NinePProtocolException failure =
            await Assert.ThrowsAsync<NinePProtocolException>(async () => await stating);
        Assert.Equal(ProtocolErrorKind.Stat, failure.Kind);
    }

    /// <summary>Rule 56: an <c>Rreaddir</c> whose last record is cut short is refused.</summary>
    [Fact]
    public async Task SplitDirentIsRejected()
    {
        await using Harness harness = await Harness.StartAsync();
        NinePFid fid = harness.Fid();

        Task listing = Drain(fid);
        Treaddir request = await harness.Server.ReadAsync<Treaddir>(Ct);

        byte[] packed = new byte[64];
        DirEntry entry = new("hello", new Qid(QidType.QTFILE, 0, 3), FileKind.File, 1);
        int written = DirEntryCodec.Pack(packed, [entry], out int count);
        Assert.Equal(1, count);

        // One byte short of the record the server said it packed: the tail is not a record.
        await harness.Server.WriteAsync(new Rreaddir(request.Tag, packed.AsMemory(0, written - 1)), Ct);

        await Assert.ThrowsAsync<NinePProtocolException>(async () => await listing);
    }

    private static async Task Drain(NinePFid fid)
    {
        await foreach (DirEntry entry in fid.ReadDirAsync(TestDeadlines.Wrap(TestContext.Current.CancellationToken)))
        {
            Assert.NotNull(entry.Name);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(NinePSession session, FakeNinePServer server)
        {
            Session = session;
            Server = server;
        }

        public NinePSession Session { get; }

        public FakeNinePServer Server { get; }

        public static async Task<Harness> StartAsync(string version = Constants.Version9P2000L)
        {
            (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
            ClientOptions options = new()
            {
                Dialects = [version == Constants.Version9P2000L ? Dialect.P9_2000_L : Dialect.P9_2000],
                Msize = 8192,
            };

            Task<NinePSession> connecting = NinePClient
                .ConnectAsync(wire, options, CancellationToken.None).AsTask();

            await server.NegotiateAsync(version);
            return new Harness(await connecting, server);
        }

        /// <summary>A fid the test drives directly, without the attach the server would answer.</summary>
        /// <returns>A fid bound to a directory qid.</returns>
        public NinePFid Fid() => new(Session, Session.RentFid(), new Qid(QidType.QTDIR, 0, 1));

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Session.DisposeAsync();
        }
    }
}
