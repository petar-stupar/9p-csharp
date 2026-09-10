using System.Text;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Robustness;

/// <summary>
/// The wire half of authentication (reference §5.2, S-22, S-23): what an afid is bound to, what an
/// attach may do with one, and the two bounds the core puts on the exchange itself.
/// </summary>
[Trait("Category", "Robustness")]
public sealed class AfidLimitTests
{
    private static readonly byte[] Secret = "s3cret"u8.ToArray();

    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);






    /// <summary>
    /// Architecture §5: the exchange may carry at most <see cref="Limits.MaxAuthBytes"/> in each
    /// direction. A client that keeps writing past it is refused rather than buffered, because an
    /// afid is reachable before anything has been proved.
    /// </summary>
    [Fact]
    public async Task AfidWriteBoundedAt64KiB()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync(
            options => options with { Authenticator = new SinkAuthenticator() });
        await using WireClient client = await WireClient.ConnectAsync(
            harness, Dialect.P9_2000_u, msize: 16384, cancellationToken: Ct);

        await client.SendAsync(new Tauth(1, 1, "glenda", string.Empty, 1000), Ct);
        await client.ReceiveAsync<Rauth>(Ct);

        byte[] chunk = new byte[8192];
        ulong offset = 0;
        int accepted = 0;
        string? refusal = null;
        ushort tag = 2;

        // 64 KiB is eight of these chunks; the ninth must be refused rather than accepted.
        for (int i = 0; i < 9 && refusal is null; i++, tag++)
        {
            await client.SendAsync(new Twrite(tag, 1, offset, chunk), Ct);
            byte[] frame = await client.ReceiveFrameAsync(Ct);

            if (Peek(frame) == MessageType.Rerror)
            {
                refusal = DecodeError(client, frame);
                continue;
            }

            accepted += chunk.Length;
            offset += (ulong)chunk.Length;
        }

        Assert.Equal(Limits.Default.MaxAuthBytes, accepted);
        Assert.Equal("authentication failed", refusal);
    }

    /// <summary>
    /// Architecture §5: the exchange also has a wall-clock budget, so an authenticator that never
    /// answers cannot hold a connection open for ever. The clock is the injected one, so the test
    /// spends milliseconds rather than the default thirty seconds.
    /// </summary>
    [Fact]
    public async Task AfidExchangeTimesOut()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync(
            options => options with
            {
                Authenticator = new HangingAuthenticator(),
                Limits = Limits.Default with { AuthTimeout = TimeSpan.FromMilliseconds(100) },
            });
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_u, cancellationToken: Ct);

        await client.SendAsync(new Tauth(1, 1, "glenda", string.Empty, 1000), Ct);
        await client.ReceiveAsync<Rauth>(Ct);

        await client.SendAsync(new Twrite(2, 1, 0, "token"u8.ToArray()), Ct);
        Rerror refusal = await client.ReceiveAsync<Rerror>(Ct);

        Assert.Equal("authentication failed", refusal.Ename);
    }

    private static ServerOptions TokenOptions(ServerOptions options) =>
        options with { Authenticator = new TokenAuthenticator(Secret) };

    private static MessageType Peek(byte[] frame) => (MessageType)frame[4];

    private static string DecodeError(WireClient client, byte[] frame) =>
        NineP.Protocol.Codec.MessageCodec.Decode<Rerror>(frame, client.Dialect).Ename;

    /// <summary>Runs the token exchange over the afid, exactly as a mounting client would.</summary>
    private static async Task AuthenticateAsync(WireClient client, uint afid, string uname, uint nuname)
    {
        await client.SendAsync(new Tauth(1, afid, uname, string.Empty, nuname), Ct);
        await client.ReceiveAsync<Rauth>(Ct);

        await client.SendAsync(new Twrite(2, afid, 0, Secret), Ct);
        Rwrite written = await client.ReceiveAsync<Rwrite>(Ct);
        Assert.Equal((uint)Secret.Length, written.Count);

        await client.SendAsync(new Tread(3, afid, 0, 64), Ct);
        Rread answered = await client.ReceiveAsync<Rread>(Ct);
        Assert.Equal("ok\n", Encoding.UTF8.GetString(answered.Data.Span));
    }

    /// <summary>An authenticator whose exchange succeeds at once and reports a fixed user.</summary>


    /// <summary>An authenticator that swallows everything and never succeeds.</summary>
    private sealed class SinkAuthenticator : IAuthenticator
    {
        public bool IsRequired => true;

        // CA2000: the server core owns the session from here and disposes it with the afid.
#pragma warning disable CA2000
        public ValueTask<IAuthSession?> BeginAsync(
            AuthRequest request, PeerIdentity? peer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IAuthSession?>(new Session());
#pragma warning restore CA2000

        private sealed class Session : IAuthSession
        {
            public Identity? Identity => null;

            public ValueTask WriteAsync(
                ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
                ValueTask.CompletedTask;

            public ValueTask<ReadOnlyMemory<byte>> ReadAsync(
                int maxBytes, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>An authenticator whose exchange never returns until it is cancelled.</summary>
    private sealed class HangingAuthenticator : IAuthenticator
    {
        public bool IsRequired => true;

        // CA2000: the server core owns the session from here and disposes it with the afid.
#pragma warning disable CA2000
        public ValueTask<IAuthSession?> BeginAsync(
            AuthRequest request, PeerIdentity? peer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IAuthSession?>(new Session());
#pragma warning restore CA2000

        private sealed class Session : IAuthSession
        {
            public Identity? Identity => null;

            public async ValueTask WriteAsync(
                ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

            public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(
                int maxBytes, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return ReadOnlyMemory<byte>.Empty;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
