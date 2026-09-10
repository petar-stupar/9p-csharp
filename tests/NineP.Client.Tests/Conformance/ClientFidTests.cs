using NineP.Client.Tests;
using NineP.Client.Tests.Conformance;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using Xunit;

namespace NineP.Client.Tests.Conformance;

/// <summary>
/// The fid lifecycle of architecture §6 and the credential-driven afid exchange of reference §5.2,
/// against a fake server that reads and writes real frames over <see cref="MemoryTransport"/>.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class ClientFidTests
{
    private static readonly byte[] Secret = "s3cr3t"u8.ToArray();

    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>Disposing a fid clunks it, which is what makes the release deterministic.</summary>
    [Fact]
    public async Task DisposeClunks()
    {
        await using Harness harness = await Harness.StartAsync();

        Task<NinePFid> attaching = harness.Session.AttachAsync(Ct).AsTask();
        Tattach attach = await harness.Server.ReadAsync<Tattach>(Ct);
        await harness.Server.WriteAsync(new Rattach(attach.Tag, new Qid(QidType.QTDIR, 1, 1)), Ct);

        NinePFid root = await attaching;
        Assert.Equal(1, harness.Session.LiveFids);

        Task disposing = root.DisposeAsync().AsTask();
        Tclunk clunk = await harness.Server.ReadAsync<Tclunk>(Ct);

        Assert.Equal(root.Fid, clunk.Fid);
        await harness.Server.WriteAsync(new Rclunk(clunk.Tag), Ct);
        await disposing;

        Assert.Equal(0, harness.Session.LiveFids);
    }

    /// <summary>A second disposal writes nothing: one fid is clunked once, however often it is closed.</summary>
    [Fact]
    public async Task DoubleDisposeIsSafe()
    {
        await using Harness harness = await Harness.StartAsync();

        Task<NinePFid> attaching = harness.Session.AttachAsync(Ct).AsTask();
        Tattach attach = await harness.Server.ReadAsync<Tattach>(Ct);
        await harness.Server.WriteAsync(new Rattach(attach.Tag, new Qid(QidType.QTDIR, 1, 1)), Ct);

        NinePFid root = await attaching;

        Task first = root.DisposeAsync().AsTask();
        Tclunk clunk = await harness.Server.ReadAsync<Tclunk>(Ct);
        await harness.Server.WriteAsync(new Rclunk(clunk.Tag), Ct);
        await first;

        await root.DisposeAsync();

        // Nothing more reached the wire: the next frame the server sees is the one the test sends
        // after this point, so a stray second Tclunk would show up as a Tstat that is not a Tstat.
        Task<NinePFid> second = harness.Session.AttachAsync("other", "", null, Ct).AsTask();
        Tattach next = await harness.Server.ReadAsync<Tattach>(Ct);
        Assert.Equal("other", next.Uname);

        await harness.Server.WriteAsync(new Rattach(next.Tag, new Qid(QidType.QTDIR, 1, 2)), Ct);

        NinePFid tree = await second;
        Assert.NotSame(root, tree);
    }

    /// <summary>
    /// Reference §5.2: with a credential the client opens an afid, runs the exchange over
    /// <c>Tread</c>/<c>Twrite</c>, presents the afid in the attach, and clunks it afterwards. The
    /// server side of the exchange is a real <see cref="TokenAuthenticator"/>.
    /// </summary>
    [Fact]
    public async Task AttachRunsAfidExchange()
    {
        await using Harness harness = await Harness.StartAsync(
            options => options with { Credential = new TokenCredential(Secret), Uname = "glenda" });

        Task<NinePFid> attaching = harness.Session.AttachAsync(Ct).AsTask();

        Tauth auth = await harness.Server.ReadAsync<Tauth>(Ct);
        Assert.Equal("glenda", auth.Uname);
        Assert.NotEqual(Constants.NOFID, auth.Afid);
        await harness.Server.WriteAsync(new Rauth(auth.Tag, new Qid(QidType.QTAUTH, 0, 7)), Ct);

        TokenAuthenticator authenticator = new(Secret);
        await using IAuthSession session = Assert.IsAssignableFrom<IAuthSession>(
            await authenticator.BeginAsync(new AuthRequest(auth.Uname, auth.NUname, auth.Aname), null, Ct));

        Assert.Null(session.Identity);

        Twrite credential = await harness.Server.ReadAsync<Twrite>(Ct);
        Assert.Equal(auth.Afid, credential.Fid);
        await session.WriteAsync(credential.Data, Ct);
        await harness.Server.WriteAsync(new Rwrite(credential.Tag, (uint)credential.Data.Length), Ct);

        Tread acknowledgement = await harness.Server.ReadAsync<Tread>(Ct);
        Assert.Equal(auth.Afid, acknowledgement.Fid);
        ReadOnlyMemory<byte> answer = await session.ReadAsync((int)acknowledgement.Count, Ct);
        await harness.Server.WriteAsync(new Rread(acknowledgement.Tag, answer), Ct);

        // The exchange succeeded on the server side, and only now does the attach go out.
        Assert.NotNull(session.Identity);

        Tattach attach = await harness.Server.ReadAsync<Tattach>(Ct);
        Assert.Equal(auth.Afid, attach.Afid);
        await harness.Server.WriteAsync(new Rattach(attach.Tag, new Qid(QidType.QTDIR, 1, 1)), Ct);

        Tclunk afidClunk = await harness.Server.ReadAsync<Tclunk>(Ct);
        Assert.Equal(auth.Afid, afidClunk.Fid);
        await harness.Server.WriteAsync(new Rclunk(afidClunk.Tag), Ct);

        NinePFid root = await attaching;
        Assert.Equal(QidType.QTDIR, root.Qid.Type);
        Assert.Same(root, harness.Session.Root);
    }

    /// <summary>Without a credential no <c>Tauth</c> is sent at all and the attach carries NOFID.</summary>
    [Fact]
    public async Task AttachWithoutCredentialUsesNofid()
    {
        await using Harness harness = await Harness.StartAsync();

        Task<NinePFid> attaching = harness.Session.AttachAsync(Ct).AsTask();

        Tattach attach = await harness.Server.ReadAsync<Tattach>(Ct);
        Assert.Equal(Constants.NOFID, attach.Afid);
        Assert.Equal(Constants.NONUNAME, attach.NUname);

        await harness.Server.WriteAsync(new Rattach(attach.Tag, new Qid(QidType.QTDIR, 1, 1)), Ct);
        await attaching;
    }

    /// <summary>A refused <c>Tauth</c> surfaces the server's error; it never downgrades to NOFID.</summary>
    [Fact]
    public async Task RefusedAuthIsNotDowngradedToNofid()
    {
        await using Harness harness = await Harness.StartAsync(
            options => options with { Credential = new TokenCredential(Secret) });

        Task<NinePFid> attaching = harness.Session.AttachAsync(Ct).AsTask();

        Tauth auth = await harness.Server.ReadAsync<Tauth>(Ct);
        await harness.Server.WriteAsync(new Rlerror(auth.Tag, Errno.ECONNREFUSED), Ct);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(async () => await attaching);
        Assert.Equal(Errno.ECONNREFUSED, refusal.Error.Errno);
    }

    /// <summary>
    /// Every <c>AttachAsync</c> overload publishes <see cref="NinePSession.Root"/>, not only the
    /// one that reads its arguments from the options. The cli's <c>--auth-optional</c> falls back
    /// to this overload after a refused <c>Tauth</c>, and while it left the root null every
    /// command but <c>version</c> threw on the walk that followed.
    /// <b>Mutation:</b> move the <c>Interlocked.CompareExchange</c> back into the parameterless
    /// overload and the assertion below throws <see cref="InvalidOperationException"/>.
    /// </summary>
    [Fact]
    public async Task AttachWithAUnamePublishesTheRoot()
    {
        await using Harness harness = await Harness.StartAsync();

        Task<NinePFid> attaching = harness.Session
            .AttachAsync("glenda", string.Empty, null, Ct).AsTask();
        Tattach attach = await harness.Server.ReadAsync<Tattach>(Ct);
        await harness.Server.WriteAsync(new Rattach(attach.Tag, new Qid(QidType.QTDIR, 1, 1)), Ct);

        NinePFid root = await attaching;

        Assert.Same(root, harness.Session.Root);
    }

    /// <summary>
    /// A second attach is a second tree or a second user, so it leaves the root the first one
    /// bound exactly where it is: the exchange that publishes it is conditional.
    /// </summary>
    [Fact]
    public async Task ASecondAttachDoesNotDisplaceTheRoot()
    {
        await using Harness harness = await Harness.StartAsync();

        Task<NinePFid> attaching = harness.Session.AttachAsync(Ct).AsTask();
        Tattach attach = await harness.Server.ReadAsync<Tattach>(Ct);
        await harness.Server.WriteAsync(new Rattach(attach.Tag, new Qid(QidType.QTDIR, 1, 1)), Ct);
        NinePFid root = await attaching;

        Task<NinePFid> second = harness.Session.AttachAsync("other", string.Empty, null, Ct).AsTask();
        Tattach next = await harness.Server.ReadAsync<Tattach>(Ct);
        await harness.Server.WriteAsync(new Rattach(next.Tag, new Qid(QidType.QTDIR, 1, 2)), Ct);
        NinePFid other = await second;

        Assert.NotSame(root, other);
        Assert.Same(root, harness.Session.Root);
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

        public static async Task<Harness> StartAsync(Func<ClientOptions, ClientOptions>? tune = null)
        {
            (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
            ClientOptions options = new() { Dialects = [Dialect.P9_2000_L] };

            Task<NinePSession> connecting = NinePClient
                .ConnectAsync(wire, tune is null ? options : tune(options), CancellationToken.None)
                .AsTask();

            await server.NegotiateAsync(Constants.Version9P2000L);
            return new Harness(await connecting, server);
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Session.DisposeAsync();
        }
    }
}
