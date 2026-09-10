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

namespace NineP.Client.Tests.Compat;

/// <summary>
/// The client's reply router, against a fake server that speaks the wire (S-31). Reference §8
/// rule 12 lives here: an unknown tag, an unexpected type or an oversize reply terminates the
/// session rather than being skipped.
/// </summary>
[Trait("Category", "Compat")]
public sealed class DialectNegotiationClientTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);










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





    private static Task<NinePSession> ConnectAsync(
        INinePConnection connection, Dialect preferred, uint? msize = null) =>
        NinePClient.ConnectAsync(
            connection,
            new ClientOptions { Dialects = [preferred], MinDialect = Dialect.P9_2000, Msize = msize },
            TestDeadlines.Wrap(TestContext.Current.CancellationToken)).AsTask();
}
