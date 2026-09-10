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

namespace NineP.Client.Tests.Robustness;

/// <summary>
/// The client's reply router, against a fake server that speaks the wire (S-31). Reference §8
/// rule 12 lives here: an unknown tag, an unexpected type or an oversize reply terminates the
/// session rather than being skipped.
/// </summary>
[Trait("Category", "Robustness")]
public sealed class ClientSessionTerminationTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);







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




    private static Task<NinePSession> ConnectAsync(
        INinePConnection connection, Dialect preferred, uint? msize = null) =>
        NinePClient.ConnectAsync(
            connection,
            new ClientOptions { Dialects = [preferred], MinDialect = Dialect.P9_2000, Msize = msize },
            TestDeadlines.Wrap(TestContext.Current.CancellationToken)).AsTask();
}
