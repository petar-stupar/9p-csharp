using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Security;

/// <summary>
/// Reference §8 rule 40: the abuse budgets that bound what an authenticated or a guessing peer can
/// spend, as opposed to the malformed input <see cref="HostileClientTests"/> covers. Each one
/// refuses rather than delays, so the reader stays free and the peers beside the offender are
/// untouched — a limit that stalls everyone is an outage, not a limit.
/// </summary>
[Trait("Category", "Security")]
public sealed class ResourceLimitTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 40, the rate half: a connection past its budget is answered EAGAIN at once, the
    /// refusals are counted, a <c>Tflush</c> is never metered, and the session stays usable.
    /// <b>Mutation:</b> delete the <c>TryTake</c> pair in <c>ServerSession.AdmitAsync</c> and this
    /// test fails on <c>RequestsMetered</c>.
    /// </summary>
    [Fact]
    public async Task ARequestFloodIsMeteredAndFlushStillAnswered()
    {
        const int Burst = 4;

        await using ServerHarness harness = await ServerHarness.StartAsync(options => options with
        {
            Limits = Limits.Default with
            {
                // The burst is the general window, so this asks for exactly Burst tokens up front
                // and one more per second — a rate no flood of sequential requests can outrun.
                MaxInFlightPerConnection = Burst + 8,
                FlushReservePerConnection = 8,
                MaxRequestsPerSecondPerConnection = 1,
            },
        });

        await using WireClient wire = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
        await wire.AttachAsync(1, Ct);

        int refused = 0;
        for (ushort tag = 10; tag < 40; tag++)
        {
            await wire.SendAsync(new Tgetattr(tag, 1, GetAttrMask.Basic), Ct);
            if (await IsRefusalAsync(wire))
            {
                refused++;
            }
        }

        Assert.True(refused > 0, "a flood well past the rate must be refused");
        Assert.Equal(refused, harness.Server.Counters.RequestsMetered);

        // Rule 40: Tflush is never metered, so a client that spent its budget can still cancel.
        await wire.SendAsync(new Tflush(99, 10), Ct);
        await wire.ReceiveAsync<Rflush>(Ct);

        // A second connection has its own budget and is untouched by the flooder's.
        await using WireClient healthy = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
        await healthy.AttachAsync(1, Ct);
        await healthy.SendAsync(new Tgetattr(1, 1, GetAttrMask.Basic), Ct);
        await healthy.ReceiveAsync<Rgetattr>(Ct);
    }

    /// <summary>
    /// Rule 40, the connection half: one address cannot take every slot of the listener. The
    /// excess connection is closed at once and the address's own earlier connections keep working.
    /// <b>Mutation:</b> drop the <c>TryHold</c> call in <c>ListenerContext.AcceptAsync</c> and the
    /// third connection succeeds, failing this test.
    /// </summary>
    [Fact]
    public async Task OneAddressCannotHoldEveryConnection()
    {
        const int PerAddress = 2;

        // A real socket: loopback is one address, and the in-process transport is deliberately
        // exempt — whoever can dial it already runs inside this process.
        TcpTransport transport = new();
        await using NinePServer server = new(new ServerOptions
        {
            Listen = [new NinePAddress(NinePScheme.Tcp, "127.0.0.1", 0, string.Empty)],
            Transports = [transport],
            Limits = Limits.Default with { MaxConnectionsPerAddress = PerAddress },
        });

        Task serving = server.ServeAsync(ServerHarness.Populate(new MemoryFilesystem()), Ct);
        await server.Listening;
        NinePAddress bound = server.Endpoints[0];

        List<NinePSession> held = [];
        try
        {
            for (int i = 0; i < PerAddress; i++)
            {
                held.Add(await ConnectAsync(transport, bound));
            }

            await Assert.ThrowsAnyAsync<Exception>(async () => await ConnectAsync(transport, bound));
            Assert.Equal(PerAddress, server.Counters.ConnectionsOpen);
            Assert.True(server.Counters.ConnectionsRefused > 0);

            // The connections that were inside the cap are unharmed by their neighbour's refusal.
            foreach (NinePSession session in held)
            {
                Assert.Equal(FileKind.Directory, (await session.GetAttrAsync("/", Ct)).Kind);
            }

            // Releasing a slot lets the same address back in; the cap is concurrency, not a ban.
            await held[0].DisposeAsync();
            held.RemoveAt(0);
            held.Add(await ConnectAsync(transport, bound));
        }
        finally
        {
            foreach (NinePSession session in held)
            {
                await session.DisposeAsync();
            }

            await server.DisposeAsync();
            await serving.WaitAsync(Ct);
        }
    }

    /// <summary>
    /// Rule 40, the authentication half: past the per-address budget the exchange is refused
    /// before the authenticator is asked, so a peer that is guessing stops paying for the
    /// credential check — for <c>PasswordAuthenticator</c> a PBKDF2 derivation that costs the
    /// same for an unknown user as for a known one. A success clears the budget.
    /// <b>Mutation:</b> delete the <c>MayAttempt</c> check in <c>Dispatcher.AuthAsync</c> and the
    /// counting authenticator is asked a fourth time, failing this test.
    /// </summary>
    [Fact]
    public async Task AnAuthFloodStopsPayingTheDerivation()
    {
        const int Budget = 3;

        CountingAuthenticator authenticator = new("s3cret");
        TcpTransport transport = new();
        await using NinePServer server = new(new ServerOptions
        {
            Listen = [new NinePAddress(NinePScheme.Tcp, "127.0.0.1", 0, string.Empty)],
            Transports = [transport],
            Authenticator = authenticator,
            Limits = Limits.Default with { MaxAuthFailuresPerAddress = Budget },
        });

        Task serving = server.ServeAsync(ServerHarness.Populate(new MemoryFilesystem()), Ct);
        await server.Listening;
        NinePAddress bound = server.Endpoints[0];

        try
        {
            for (int attempt = 0; attempt < Budget; attempt++)
            {
                await Assert.ThrowsAnyAsync<Exception>(async () =>
                    await ConnectAsync(transport, bound, "wrong"));
            }

            Assert.Equal(Budget, authenticator.Begins);

            // Past the budget the refusal arrives without the authenticator being asked at all.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await ConnectAsync(transport, bound, "wrong"));
            Assert.Equal(Budget, authenticator.Begins);
            Assert.True(server.Counters.AuthAttemptsThrottled > 0);
        }
        finally
        {
            await server.DisposeAsync();
            await serving.WaitAsync(Ct);
        }
    }

    /// <summary>A budget of zero disables the throttle, which is how an operator opts out.</summary>
    [Fact]
    public async Task ABudgetOfZeroThrottlesNothing()
    {
        CountingAuthenticator authenticator = new("s3cret");
        TcpTransport transport = new();
        await using NinePServer server = new(new ServerOptions
        {
            Listen = [new NinePAddress(NinePScheme.Tcp, "127.0.0.1", 0, string.Empty)],
            Transports = [transport],
            Authenticator = authenticator,
            Limits = Limits.Default with { MaxAuthFailuresPerAddress = 0 },
        });

        Task serving = server.ServeAsync(ServerHarness.Populate(new MemoryFilesystem()), Ct);
        await server.Listening;
        NinePAddress bound = server.Endpoints[0];

        try
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                await Assert.ThrowsAnyAsync<Exception>(async () =>
                    await ConnectAsync(transport, bound, "wrong"));
            }

            Assert.Equal(12, authenticator.Begins);
            Assert.Equal(0, server.Counters.AuthAttemptsThrottled);

            // And the right credential still attaches, from the same address.
            await using NinePSession good = await ConnectAsync(transport, bound, "s3cret");
            Assert.Equal(FileKind.Directory, (await good.GetAttrAsync("/", Ct)).Kind);
        }
        finally
        {
            await server.DisposeAsync();
            await serving.WaitAsync(Ct);
        }
    }

    /// <summary>An authentication that succeeds clears whatever the address had spent.</summary>
    [Fact]
    public async Task ASuccessClearsTheAddressBudget()
    {
        const int Budget = 2;

        CountingAuthenticator authenticator = new("s3cret");
        TcpTransport transport = new();
        await using NinePServer server = new(new ServerOptions
        {
            Listen = [new NinePAddress(NinePScheme.Tcp, "127.0.0.1", 0, string.Empty)],
            Transports = [transport],
            Authenticator = authenticator,
            Limits = Limits.Default with { MaxAuthFailuresPerAddress = Budget },
        });

        Task serving = server.ServeAsync(ServerHarness.Populate(new MemoryFilesystem()), Ct);
        await server.Listening;
        NinePAddress bound = server.Endpoints[0];

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(async () => await ConnectAsync(transport, bound, "wrong"));

            await using (NinePSession good = await ConnectAsync(transport, bound, "s3cret"))
            {
                Assert.Equal(FileKind.Directory, (await good.GetAttrAsync("/", Ct)).Kind);
            }

            // The one failure before the success is forgotten, so the full budget is available.
            for (int attempt = 0; attempt < Budget; attempt++)
            {
                await Assert.ThrowsAnyAsync<Exception>(async () => await ConnectAsync(transport, bound, "wrong"));
            }

            Assert.Equal(0, server.Counters.AuthAttemptsThrottled);
        }
        finally
        {
            await server.DisposeAsync();
            await serving.WaitAsync(Ct);
        }
    }

    private static async Task<NinePSession> ConnectAsync(
        ITransport transport, NinePAddress address, string? token = null)
    {
        ClientOptions options = new()
        {
            Dialects = [Dialect.P9_2000_L],
            Msize = 8192,
            Uname = "glenda",
            Credential = token is null ? null : new TokenCredential(System.Text.Encoding.ASCII.GetBytes(token)),
        };

        NinePSession session = await NinePClient.ConnectAsync(transport, address, options, Ct);
        try
        {
            await session.AttachAsync(Ct);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>Reads one reply and reports whether it was the EAGAIN of a refused request.</summary>
    private static async Task<bool> IsRefusalAsync(WireClient wire)
    {
        byte[] frame = await wire.ReceiveFrameAsync(Ct);
        if (NineP.Protocol.Codec.MessageCodec.PeekType(frame) != MessageType.Rlerror)
        {
            return false;
        }

        Rlerror error = NineP.Protocol.Codec.MessageCodec.Decode<Rlerror>(frame, Dialect.P9_2000_L);
        return error.Ecode == Errno.EAGAIN;
    }

    /// <summary>A token authenticator that counts how often the exchange was begun at all.</summary>
    private sealed class CountingAuthenticator(string secret) : IAuthenticator
    {
        private readonly TokenAuthenticator _inner = new(System.Text.Encoding.ASCII.GetBytes(secret));
        private int _begins;

        public int Begins => Volatile.Read(ref _begins);

        public bool IsRequired => true;

        public ValueTask<IAuthSession?> BeginAsync(
            AuthRequest request, PeerIdentity? peer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _begins);
            return _inner.BeginAsync(request, peer, cancellationToken);
        }
    }
}
