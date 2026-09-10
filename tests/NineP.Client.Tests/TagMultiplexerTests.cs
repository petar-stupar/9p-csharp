using System.Buffers;
using System.Buffers.Binary;
using NineP.Client.Internal;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using Xunit;

namespace NineP.Client.Tests;

/// <summary>
/// The client's reply router, against a fake server that speaks the wire (S-31). Reference §8
/// rule 12 lives here: an unknown tag, an unexpected type or an oversize reply terminates the
/// session rather than being skipped.
/// </summary>
public sealed class TagMultiplexerTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>Requests are pipelined: N go out before any comes back, and replies may arrive
    /// out of order under distinct tags.</summary>
    [Fact]
    public async Task RequestsArePipelinedAndRepliesMayArriveOutOfOrder()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        Task<NinePSession> connecting = ConnectAsync(wire, Dialect.P9_2000_L);
        await server.NegotiateAsync(Constants.Version9P2000L, cancellationToken: Ct);
        await using NinePSession session = await connecting;

        Task<Rclunk>[] requests =
        [
            .. Enumerable.Range(0, 8).Select(
                fid => session.Messages.ClunkAsync(new Tclunk(0, (uint)fid), Ct).AsTask()),
        ];

        List<ushort> tags = [];
        for (int i = 0; i < requests.Length; i++)
        {
            Tclunk request = await server.ReadAsync<Tclunk>(Ct);
            tags.Add(request.Tag);
        }

        Assert.Equal(8, tags.Distinct().Count());
        Assert.DoesNotContain(Constants.NOTAG, tags);
        Assert.All(requests, request => Assert.False(request.IsCompleted));

        // Answered last-first: the router matches on the tag, not on arrival order.
        foreach (ushort tag in Enumerable.Reverse(tags))
        {
            await server.WriteAsync(new Rclunk(tag), Ct);
        }

        Rclunk[] replies = await Task.WhenAll(requests);

        Assert.Equal(tags, [.. replies.Select(reply => reply.Tag)]);
    }

    /// <summary>A tag is returned to the pool once its reply has been delivered.</summary>
    [Fact]
    public async Task TagsAreReusedAfterTheirReply()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        Task<NinePSession> connecting = ConnectAsync(wire, Dialect.P9_2000_L);
        await server.NegotiateAsync(Constants.Version9P2000L, cancellationToken: Ct);
        await using NinePSession session = await connecting;

        for (int i = 0; i < 3; i++)
        {
            Task<Rclunk> request = session.Messages.ClunkAsync(new Tclunk(0, 1), Ct).AsTask();
            Tclunk sent = await server.ReadAsync<Tclunk>(Ct);
            await server.WriteAsync(new Rclunk(sent.Tag), Ct);
            await request;
        }

        Assert.Equal(0, session.Multiplexer.TagsInUse);
        Assert.Equal(0, session.Multiplexer.Outstanding);
    }

    /// <summary>
    /// An error reply is an ordinary reply: it releases the request's tag exactly once. Releasing
    /// it twice put the number back in the pool twice, so two later requests rented the same tag
    /// and their replies cross-delivered — silently, until the pool had cycled once.
    /// </summary>
    [Fact]
    public async Task AnErrorReplyReleasesItsTagExactlyOnce()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        Task<NinePSession> connecting = ConnectAsync(wire, Dialect.P9_2000_L);
        await server.NegotiateAsync(Constants.Version9P2000L, cancellationToken: Ct);
        await using NinePSession session = await connecting;

        Task<Rlopen> failing = session.Messages.LopenAsync(new Tlopen(0, 1, 0), Ct).AsTask();
        Tlopen refused = await server.ReadAsync<Tlopen>(Ct);
        await server.WriteAsync(new Rlerror(refused.Tag, Errno.ENOENT), Ct);
        await Assert.ThrowsAsync<NinePException>(async () => await failing);

        // One rental, one return. A second return would show up as -1 here, and as a duplicate
        // in the free queue below.
        Assert.Equal(0, session.Multiplexer.TagsInUse);
        Assert.Equal(0, session.Multiplexer.Outstanding);

        Task<Rlopen> first = session.Messages.LopenAsync(new Tlopen(0, 2, 0), Ct).AsTask();
        Tlopen firstSent = await server.ReadAsync<Tlopen>(Ct);
        Task<Rlopen> second = session.Messages.LopenAsync(new Tlopen(0, 3, 0), Ct).AsTask();
        Tlopen secondSent = await server.ReadAsync<Tlopen>(Ct);

        Assert.NotEqual(firstSent.Tag, secondSent.Tag);

        await server.WriteAsync(new Rlopen(secondSent.Tag, new Qid(QidType.QTFILE, 0, 30), 0), Ct);
        await server.WriteAsync(new Rlopen(firstSent.Tag, new Qid(QidType.QTFILE, 0, 20), 0), Ct);

        Assert.Equal(20ul, (await first).Qid.Path);
        Assert.Equal(30ul, (await second).Qid.Path);
        Assert.Equal(0, session.Multiplexer.TagsInUse);
    }

    /// <summary>
    /// The pool cycled: after one error reply, every one of the 65535 tags is rented at once and
    /// no two live requests share a number. This is the shape the incident took — the duplicate a
    /// double release leaves in the pool sits at the back of the queue and only surfaces once the
    /// pool has come all the way round, which is why it took tens of thousands of requests to
    /// show up as a misdelivered reply.
    /// </summary>
    [Fact]
    public async Task TheWholeTagPoolIsRentableAfterAnErrorReply()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        Task<NinePSession> connecting = ConnectAsync(wire, Dialect.P9_2000_L);
        await server.NegotiateAsync(Constants.Version9P2000L, cancellationToken: Ct);
        await using NinePSession session = await connecting;

        Task<Rlopen> failing = session.Messages.LopenAsync(new Tlopen(0, 1, 0), Ct).AsTask();
        Tlopen refused = await server.ReadAsync<Tlopen>(Ct);
        await server.WriteAsync(new Rlerror(refused.Tag, Errno.ENOENT), Ct);
        await Assert.ThrowsAsync<NinePException>(async () => await failing);

        Task<Rclunk>[] requests =
        [
            .. Enumerable.Range(0, TagPool.Capacity).Select(
                fid => session.Messages.ClunkAsync(new Tclunk(0, (uint)fid), Ct).AsTask()),
        ];

        HashSet<ushort> tags = [];
        for (int i = 0; i < requests.Length; i++)
        {
            Tclunk sent = await server.ReadAsync<Tclunk>(Ct);
            Assert.True(tags.Add(sent.Tag), "two live requests were issued under the same tag");
        }

        Assert.Equal(TagPool.Capacity, session.Multiplexer.TagsInUse);

        // The pool holds 65535 tags, not 65536: one more request has nothing to rent. A double
        // release leaves a spare copy of a number here, and that copy is the misdelivery.
        NinePProtocolException exhausted = await Assert.ThrowsAsync<NinePProtocolException>(
            async () => await session.Messages.ClunkAsync(new Tclunk(0, 0), Ct));
        Assert.Equal(ProtocolErrorKind.Overflow, exhausted.Kind);

        foreach (ushort tag in tags)
        {
            await server.WriteAsync(new Rclunk(tag), Ct);
        }

        await Task.WhenAll(requests);
        Assert.Equal(0, session.Multiplexer.TagsInUse);
    }

    /// <summary>An error reply becomes a typed exception carrying the server's error value.</summary>
    [Fact]
    public async Task AnRlerrorBecomesATypedException()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        Task<NinePSession> connecting = ConnectAsync(wire, Dialect.P9_2000_L);
        await server.NegotiateAsync(Constants.Version9P2000L, cancellationToken: Ct);
        await using NinePSession session = await connecting;

        Task<Rclunk> request = session.Messages.ClunkAsync(new Tclunk(0, 1), Ct).AsTask();
        Tclunk sent = await server.ReadAsync<Tclunk>(Ct);
        await server.WriteAsync(new Rlerror(sent.Tag, Errno.EBADF), Ct);

        NinePException failure = await Assert.ThrowsAsync<NinePException>(async () => await request);

        Assert.Equal(Errno.EBADF, failure.Error.Errno);
        Assert.Equal("fid unknown or out of range", failure.Error.Ename);
    }

    /// <summary>A 9P2000.u error reply carries both halves, and the errno wins over the table.</summary>
    [Fact]
    public async Task AnRerrorCarriesBothHalvesInDotU()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        Task<NinePSession> connecting = ConnectAsync(wire, Dialect.P9_2000_u);
        await server.NegotiateAsync(Constants.Version9P2000u, cancellationToken: Ct);
        await using NinePSession session = await connecting;

        Task<Rclunk> request = session.Messages.ClunkAsync(new Tclunk(0, 1), Ct).AsTask();
        Tclunk sent = await server.ReadAsync<Tclunk>(Ct);
        await server.WriteAsync(new Rerror(sent.Tag, "file not found", Errno.ENOENT), Ct);

        NinePException failure = await Assert.ThrowsAsync<NinePException>(async () => await request);

        Assert.Equal(Errno.ENOENT, failure.Error.Errno);
        Assert.Equal("file not found", failure.Error.Ename);
    }

    /// <summary>Rule 55: a reply under a tag nothing is waiting for terminates the session.</summary>
    [Fact]
    public async Task UnknownTagTerminatesSession()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        Task<NinePSession> connecting = ConnectAsync(wire, Dialect.P9_2000_L);
        await server.NegotiateAsync(Constants.Version9P2000L, cancellationToken: Ct);
        await using NinePSession session = await connecting;

        Task<Rclunk> request = session.Messages.ClunkAsync(new Tclunk(0, 1), Ct).AsTask();
        Tclunk sent = await server.ReadAsync<Tclunk>(Ct);

        await server.WriteAsync(new Rclunk((ushort)(sent.Tag + 1)), Ct);

        NinePProtocolException failure =
            await Assert.ThrowsAsync<NinePProtocolException>(async () => await request);

        Assert.Equal(ProtocolErrorKind.Type, failure.Kind);
        Assert.NotNull(session.Multiplexer.Termination);
        await Assert.ThrowsAsync<NinePProtocolException>(
            async () => await session.Messages.ClunkAsync(new Tclunk(0, 2), Ct));
    }

    /// <summary>Rule 55: an answer of the wrong type terminates the session.</summary>
    [Fact]
    public async Task AnUnexpectedReplyTypeTerminatesSession()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        Task<NinePSession> connecting = ConnectAsync(wire, Dialect.P9_2000_L);
        await server.NegotiateAsync(Constants.Version9P2000L, cancellationToken: Ct);
        await using NinePSession session = await connecting;

        Task<Rclunk> request = session.Messages.ClunkAsync(new Tclunk(0, 1), Ct).AsTask();
        Tclunk sent = await server.ReadAsync<Tclunk>(Ct);

        // An Rremove is neither Tclunk+1 nor the dialect's error type.
        await server.WriteAsync(new Rremove(sent.Tag), Ct);

        NinePProtocolException failure =
            await Assert.ThrowsAsync<NinePProtocolException>(async () => await request);

        Assert.Equal(ProtocolErrorKind.Type, failure.Kind);
        Assert.NotNull(session.Multiplexer.Termination);
    }

    /// <summary>Rule 55: a reply larger than the negotiated msize terminates the session.</summary>
    [Fact]
    public async Task ReplyLargerThanMsizeTerminatesSession()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        Task<NinePSession> connecting = ConnectAsync(wire, Dialect.P9_2000_L, msize: 8192);
        await server.NegotiateAsync(Constants.Version9P2000L, 8192, Ct);
        await using NinePSession session = await connecting;

        Task<Rread> request = session.Messages.ReadAsync(new Tread(0, 1, 0, 4096), Ct).AsTask();
        Tread sent = await server.ReadAsync<Tread>(Ct);

        // A frame whose size field claims more than the negotiated msize is never read: the size
        // is peeked and refused before a byte of the body is waited for.
        byte[] oversize = new byte[Constants.HDRSZ + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(oversize, 9000);
        oversize[4] = (byte)MessageType.Rread;
        BinaryPrimitives.WriteUInt16LittleEndian(oversize.AsSpan(5), sent.Tag);
        await server.WriteRawAsync(oversize, Ct);

        NinePProtocolException failure =
            await Assert.ThrowsAsync<NinePProtocolException>(async () => await request);

        Assert.Equal(ProtocolErrorKind.Size, failure.Kind);
        Assert.NotNull(session.Multiplexer.Termination);
    }

    /// <summary>The negotiated session reports what was agreed, and the payload bound it implies.</summary>
    [Fact]
    public async Task NegotiationReportsWhatWasAgreed()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        Task<NinePSession> connecting = ConnectAsync(wire, Dialect.P9_2000_L);
        Tversion proposal = await server.NegotiateAsync(Constants.Version9P2000L, 65536, Ct);
        await using NinePSession session = await connecting;

        Assert.Equal(Constants.NOTAG, proposal.Tag);
        Assert.Equal(Constants.Version9P2000L, proposal.Version);
        Assert.Equal(ClientOptions.DefaultLinuxMsize, proposal.Msize);
        Assert.Equal(Dialect.P9_2000_L, session.Dialect);
        Assert.Equal(65536u, session.Msize);
        Assert.Equal(65536 - Constants.IOHDRSZ, session.MaxPayload);
    }

    /// <summary>A 9P2000 client asks for the smaller default msize of reference §5.1.</summary>
    [Fact]
    public async Task ALegacyClientAsksForTheLegacyMsize()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        Task<NinePSession> connecting = ConnectAsync(wire, Dialect.P9_2000);
        Tversion proposal = await server.NegotiateAsync(Constants.Version9P2000, cancellationToken: Ct);
        await using NinePSession session = await connecting;

        Assert.Equal(ClientOptions.DefaultLegacyMsize, proposal.Msize);
        Assert.Equal(Dialect.P9_2000, session.Dialect);
    }

    /// <summary>An answer of "unknown" is a refusal, and the exception records what came back.</summary>
    [Fact]
    public async Task AnUnknownAnswerThrows()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        Task<NinePSession> connecting = ConnectAsync(wire, Dialect.P9_2000_L);
        await server.NegotiateAsync(Constants.VersionUnknown, cancellationToken: Ct);

        NinePVersionException failure = await Assert.ThrowsAsync<NinePVersionException>(async () => await connecting);

        Assert.Equal(Constants.VersionUnknown, failure.ServerVersion);
    }

    /// <summary>
    /// <c>ClientOptions.Dialects</c> is a preference list, and it is walked. Reference §5.1:
    /// <c>"unknown"</c> refuses the version that was offered, not the connection, and until a
    /// version has been agreed the connection accepts nothing but another <c>Tversion</c>.
    /// Offering only the first entry meant a <c>.u</c>-only server answered a client whose list
    /// read <c>[.L, .u, 9P2000]</c> with <c>"unknown"</c> and the connect failed.
    /// </summary>
    [Fact]
    public async Task ARefusedDialectIsRetriedWithTheNextOnTheList()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        ClientOptions options = new()
        {
            Dialects = [Dialect.P9_2000_L, Dialect.P9_2000_u, Dialect.P9_2000],
            MinDialect = Dialect.P9_2000,
        };
        Task<NinePSession> connecting = NinePClient.ConnectAsync(wire, options, Ct).AsTask();

        Tversion linux = await server.ReadAsync<Tversion>(Ct);
        Assert.Equal(Constants.Version9P2000L, linux.Version);
        await server.WriteAsync(new Rversion(Constants.NOTAG, linux.Msize, Constants.VersionUnknown), Ct);

        Tversion unix = await server.ReadAsync<Tversion>(Ct);
        Assert.Equal(Constants.Version9P2000u, unix.Version);
        await server.WriteAsync(new Rversion(Constants.NOTAG, unix.Msize, Constants.VersionUnknown), Ct);

        Tversion legacy = await server.NegotiateAsync(Constants.Version9P2000, cancellationToken: Ct);
        Assert.Equal(Constants.Version9P2000, legacy.Version);

        await using NinePSession session = await connecting;

        Assert.Equal(Dialect.P9_2000, session.Dialect);
    }

    /// <summary>A downgrade below the configured floor throws rather than degrading silently.</summary>
    [Fact]
    public async Task VersionDowngradeBelowMinThrows()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        ClientOptions options = new()
        {
            Dialects = [Dialect.P9_2000_L],
            MinDialect = Dialect.P9_2000_u,
        };
        Task<NinePSession> connecting = NinePClient.ConnectAsync(wire, options, Ct).AsTask();
        await server.NegotiateAsync(Constants.Version9P2000, cancellationToken: Ct);

        NinePVersionException failure = await Assert.ThrowsAsync<NinePVersionException>(async () => await connecting);

        Assert.Equal(Constants.Version9P2000, failure.ServerVersion);
        Assert.Contains("below the configured floor", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reference §8 rule 18: the downgrade the caller's floor allows is the suffix-stripping one
    /// of version(5):70-78 and only that one — a suffixed offer answered with plain
    /// <c>"9P2000"</c>. This test used to offer <c>.L</c>, take <c>"9P2000.u"</c> for an answer and
    /// assert a <c>.u</c> session, on the strength of the floor alone; <c>.u</c> is neither the
    /// string that was offered nor the base version, and it is now a version error
    /// (<c>ClientProjectionTests.AnAnswerThatIsNotTheOfferIsAVersionError</c>).
    /// </summary>
    [Fact]
    public async Task ADowngradeAboveTheFloorIsAccepted()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        Task<NinePSession> connecting = ConnectAsync(wire, Dialect.P9_2000_L);
        await server.NegotiateAsync(Constants.Version9P2000, cancellationToken: Ct);
        await using NinePSession session = await connecting;

        Assert.Equal(Dialect.P9_2000, session.Dialect);
    }

    /// <summary>An msize larger than the client offered is refused: version(5) forbids it.</summary>
    [Fact]
    public async Task AnOversizeMsizeAnswerThrows()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        Task<NinePSession> connecting = ConnectAsync(wire, Dialect.P9_2000_L);
        Tversion proposal = await server.ReadAsync<Tversion>(Ct);
        await server.WriteAsync(
            new Rversion(Constants.NOTAG, proposal.Msize + 1, Constants.Version9P2000L), Ct);

        await Assert.ThrowsAsync<NinePVersionException>(async () => await connecting);
    }

    /// <summary>A message the dialect does not carry never reaches the wire.</summary>
    [Fact]
    public async Task AnIllegalMessageForTheDialectIsRefusedBeforeTheWire()
    {
        (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;

        Task<NinePSession> connecting = ConnectAsync(wire, Dialect.P9_2000);
        await server.NegotiateAsync(Constants.Version9P2000, cancellationToken: Ct);
        await using NinePSession session = await connecting;

        NinePProtocolException failure = await Assert.ThrowsAsync<NinePProtocolException>(
            async () => await session.Messages.GetattrAsync(new Tgetattr(0, 1, GetAttrMask.Basic), Ct));

        Assert.Equal(ProtocolErrorKind.Type, failure.Kind);
        Assert.Equal(0, session.Multiplexer.TagsInUse);
        Assert.Null(session.Multiplexer.Termination);
    }

    /// <summary>A memory:// address cannot be dialled by name; the instance must be handed over.</summary>
    [Fact]
    public async Task AMemoryAddressNeedsItsTransport()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await NinePClient.ConnectAsync(
            NinePAddress.Parse("memory://alpha"), new ClientOptions(), Ct));
    }

    /// <summary>The session's own transport is dialled and negotiated end to end.</summary>
    [Fact]
    public async Task TheTransportOverloadDialsAndNegotiates()
    {
        MemoryTransport transport = new();
        NinePAddress address = NinePAddress.Parse("memory://alpha");
        await using INinePListener listener = await transport.ListenAsync(address, Ct);

        Task<NinePSession> connecting = NinePClient
            .ConnectAsync(transport, address, new ClientOptions(), Ct).AsTask();

        INinePConnection? accepted = await listener.AcceptAsync(Ct);
        Assert.NotNull(accepted);

        FakeNinePServer server = FakeNinePServer.Wrap(accepted);
        await using (server)
        {
            await server.NegotiateAsync(Constants.Version9P2000L, cancellationToken: Ct);
            await using NinePSession session = await connecting;

            Assert.Equal(Dialect.P9_2000_L, session.Dialect);
        }
    }

    private static Task<NinePSession> ConnectAsync(
        INinePConnection connection, Dialect preferred, uint? msize = null) =>
        NinePClient.ConnectAsync(
            connection,
            new ClientOptions { Dialects = [preferred], MinDialect = Dialect.P9_2000, Msize = msize },
            TestDeadlines.Wrap(TestContext.Current.CancellationToken)).AsTask();
}
