using System.Text;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Security;

/// <summary>
/// The wire half of authentication (reference §5.2, S-22, S-23): what an afid is bound to, what an
/// attach may do with one, and the two bounds the core puts on the exchange itself.
/// </summary>
[Trait("Category", "Security")]
public sealed class AfidTests
{
    private static readonly byte[] Secret = "s3cret"u8.ToArray();

    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// S-22: the afid is bound to the whole triple, so an attach that keeps the <c>uname</c> but
    /// claims a different <c>n_uname</c> is refused.
    /// <b>Mutation:</b> dropping the <c>n_uname</c> comparison from
    /// <c>Dispatcher.ResolveIdentityAsync</c> lets the second attach through and this test fails.
    /// </summary>
    [Fact]
    public async Task DifferentNUnameRejected()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync(TokenOptions);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_u, cancellationToken: Ct);

        await AuthenticateAsync(client, afid: 1, uname: "glenda", nuname: 1000);

        // The same afid, the same uname, a different numeric identity: the triple does not agree.
        await client.SendAsync(new Tattach(3, 2, 1, "glenda", string.Empty, 1001), Ct);
        Rerror refusal = await client.ReceiveAsync<Rerror>(Ct);

        Assert.Equal("authentication failed", refusal.Ename);

        // The same afid with the triple it was bound to still works, so the refusal was the
        // n_uname and nothing else.
        await client.SendAsync(new Tattach(4, 2, 1, "glenda", string.Empty, 1000), Ct);
        await client.ReceiveAsync<Rattach>(Ct);
    }

    /// <summary>
    /// Reference §5.2: the session runs as whoever the exchange proved, never as the name the
    /// client typed. The authenticator here reports "verified" while the client claims "glenda".
    /// <b>Mutation:</b> answering with <c>Tattach.Uname</c> instead of
    /// <c>IAuthSession.Identity</c> makes the tree see "glenda" and this test fails.
    /// </summary>
    [Fact]
    public async Task IdentityComesFromTheAuthenticator()
    {
        MemoryFilesystem tree = new();
        await using ServerHarness harness = await ServerHarness.StartAsync(
            options => options with { Authenticator = new FixedIdentityAuthenticator("verified") },
            tree: tree);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_u, cancellationToken: Ct);

        await AuthenticateAsync(client, afid: 1, uname: "glenda", nuname: 1000);
        await client.SendAsync(new Tattach(3, 2, 1, "glenda", string.Empty, 1000), Ct);
        await client.ReceiveAsync<Rattach>(Ct);

        Assert.Equal("verified", tree.LastIdentity?.User);
    }

    /// <summary>
    /// The same rule from the other side: the claimed <c>uname</c> reaches the filesystem nowhere,
    /// not even when the authenticator's answer happens to be empty of groups or uid.
    /// </summary>
    [Fact]
    public async Task SessionIdentityIsAuthenticatorOutput()
    {
        MemoryFilesystem tree = new();
        await using ServerHarness harness = await ServerHarness.StartAsync(
            options => options with { Authenticator = new FixedIdentityAuthenticator("bootes") },
            tree: tree);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_u, cancellationToken: Ct);

        await AuthenticateAsync(client, afid: 1, uname: "glenda", nuname: 1000);
        await client.SendAsync(new Tattach(3, 2, 1, "glenda", string.Empty, 1000), Ct);
        await client.ReceiveAsync<Rattach>(Ct);

        Assert.NotEqual("glenda", tree.LastIdentity?.User);
        Assert.Equal("bootes", tree.LastIdentity?.User);
    }


    /// <summary>
    /// An afid whose exchange never succeeded carries no identity, so the attach that presents it
    /// is refused rather than run as the claim.
    /// </summary>
    [Fact]
    public async Task UnverifiedAfidRejected()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync(TokenOptions);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_u, cancellationToken: Ct);

        await client.SendAsync(new Tauth(1, 1, "glenda", string.Empty, 1000), Ct);
        await client.ReceiveAsync<Rauth>(Ct);

        // The wrong token: the exchange never produces an identity.
        await client.SendAsync(new Twrite(2, 1, 0, "wrong!"u8.ToArray()), Ct);
        await client.ReceiveFrameAsync(Ct);

        await client.SendAsync(new Tattach(3, 2, 1, "glenda", string.Empty, 1000), Ct);
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
    private sealed class FixedIdentityAuthenticator(string user) : IAuthenticator
    {
        public bool IsRequired => true;

        // CA2000: the server core owns the session from here and disposes it with the afid.
#pragma warning disable CA2000
        public ValueTask<IAuthSession?> BeginAsync(
            AuthRequest request, PeerIdentity? peer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IAuthSession?>(new Session(user));
#pragma warning restore CA2000

        private sealed class Session(string user) : IAuthSession
        {
            public Identity? Identity { get; private set; }

            public ValueTask WriteAsync(
                ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
            {
                Identity = new Identity { User = user, Uid = 4242 };
                return ValueTask.CompletedTask;
            }

            public ValueTask<ReadOnlyMemory<byte>> ReadAsync(
                int maxBytes, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult<ReadOnlyMemory<byte>>(
                    Identity is null ? ReadOnlyMemory<byte>.Empty : "ok\n"u8.ToArray());

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }




}
