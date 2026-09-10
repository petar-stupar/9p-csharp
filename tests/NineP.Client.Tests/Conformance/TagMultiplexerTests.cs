using System.Buffers;
using System.Buffers.Binary;
using NineP.Client.Internal;
using NineP.Client.Tests;
using NineP.Client.Tests.Conformance;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using Xunit;

namespace NineP.Client.Tests.Conformance;

/// <summary>
/// The client's reply router, against a fake server that speaks the wire (S-31). Reference §8
/// rule 12 lives here: an unknown tag, an unexpected type or an oversize reply terminates the
/// session rather than being skipped.
/// </summary>
[Trait("Category", "Conformance")]
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
