using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NineP.Protocol.Auth;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>
/// Every shipped authenticator against its mirror credential, over a real
/// <see cref="MemoryTransport"/> wire (S-31).
/// </summary>
public sealed class AuthenticatorTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    private static AuthRequest Request => new("glenda", 1000, "");

    /// <summary>The token authenticator and the token credential agree over the wire.</summary>
    [Fact]
    public async Task TokenExchangeProducesTheIdentity()
    {
        byte[] secret = "s3cr3t-token"u8.ToArray();

        Identity? identity = await AfidPump.ExchangeAsync(
            new TokenAuthenticator(secret), new TokenCredential(secret), Request, cancellationToken: Ct);

        Assert.NotNull(identity);
        Assert.Equal("glenda", identity.User);
        Assert.Empty(identity.Groups);
        Assert.Empty(identity.Claims);
    }

    /// <summary>A per-user secret lookup resolves the user the secret belongs to.</summary>
    [Fact]
    public async Task TokenLookupResolvesThePerUserSecret()
    {
        byte[] secret = "per-user"u8.ToArray();
        TokenAuthenticator authenticator = new(request =>
            request.Uname == "glenda" ? secret : null);

        Identity? identity = await AfidPump.ExchangeAsync(
            authenticator, new TokenCredential(secret), Request, cancellationToken: Ct);

        Assert.NotNull(identity);
        Assert.Equal("glenda", identity.User);
    }

    /// <summary>A lookup with no secret for the request refuses the Tauth outright.</summary>
    [Fact]
    public async Task TokenLookupWithNoSecretRefusesTauth()
    {
        TokenAuthenticator authenticator = new(_ => null);

        Identity? identity = await AfidPump.ExchangeAsync(
            authenticator, new TokenCredential("anything"u8.ToArray()), Request, cancellationToken: Ct);

        Assert.Null(identity);
    }

    /// <summary>The password authenticator and its credential agree over the wire.</summary>
    [Fact]
    public async Task PasswordExchangeProducesTheIdentity()
    {
        using TempPasswordFile file = TempPasswordFile.With("glenda", "correct horse battery staple");

        Identity? identity = await AfidPump.ExchangeAsync(
            new PasswordAuthenticator(PasswordFileStore.Load(file.Path)),
            new PasswordCredential("glenda", "correct horse battery staple"),
            Request,
            cancellationToken: Ct);

        Assert.NotNull(identity);
        Assert.Equal("glenda", identity.User);
    }

    /// <summary>A wrong password is refused, and the client is told so rather than left hanging.</summary>
    [Fact]
    public async Task AWrongPasswordIsRefused()
    {
        using TempPasswordFile file = TempPasswordFile.With("glenda", "correct horse battery staple");

        await Assert.ThrowsAsync<NinePException>(async () => await AfidPump.ExchangeAsync(
            new PasswordAuthenticator(PasswordFileStore.Load(file.Path)),
            new PasswordCredential("glenda", "hunter2"),
            Request,
            cancellationToken: Ct));
    }

    /// <summary>An unknown user is refused exactly the way a wrong password is.</summary>
    [Fact]
    public async Task AnUnknownUserIsRefused()
    {
        using TempPasswordFile file = TempPasswordFile.With("glenda", "correct horse battery staple");

        await Assert.ThrowsAsync<NinePException>(async () => await AfidPump.ExchangeAsync(
            new PasswordAuthenticator(PasswordFileStore.Load(file.Path)),
            new PasswordCredential("rob", "correct horse battery staple"),
            Request,
            cancellationToken: Ct));
    }

    /// <summary>
    /// An unknown user costs the same derivation as a known one. Returning early on the dictionary
    /// miss answered in microseconds where a known name took a full 600 000-iteration PBKDF2, and
    /// that difference enumerates the accounts of a password file with a stopwatch.
    /// </summary>
    [Fact]
    public void AnUnknownUserCostsTheSameDerivation()
    {
        using TempPasswordFile file = TempPasswordFile.With("glenda", "correct horse battery staple");
        PasswordFileStore store = PasswordFileStore.Load(file.Path);

        // The minimum over three runs, so a scheduling hiccup lengthens a sample rather than
        // deciding the test.
        TimeSpan known = FastestVerify(store, "glenda");
        TimeSpan unknown = FastestVerify(store, "rob");

        // The two are the same work; the assertion is loose because the point is the difference
        // between "one PBKDF2" and "none at all", which is three orders of magnitude.
        Assert.True(
            unknown >= known / 4,
            string.Format(
                CultureInfo.InvariantCulture,
                "an unknown user took {0} ms against {1} ms for a known one",
                unknown.TotalMilliseconds,
                known.TotalMilliseconds));
    }

    /// <summary>
    /// The credential is judged once per exchange. Every <c>Twrite</c> after the two lines were
    /// complete used to re-run the whole derivation, so one connection could buy
    /// <c>MaxAuthBytes</c> worth of them a few bytes at a time.
    /// </summary>
    [Fact]
    public async Task TheCredentialIsJudgedOnce()
    {
        CountingStore store = new();
        IAuthSession session = (await new PasswordAuthenticator(store).BeginAsync(Request, null, Ct))!;

        await using (session.ConfigureAwait(false))
        {
            await session.WriteAsync(Encoding.UTF8.GetBytes("glenda\nhunter2\n"), Ct);

            Assert.Equal(1, store.Calls);
            Assert.NotNull(session.Identity);

            for (int i = 0; i < 8; i++)
            {
                await session.WriteAsync("x"u8.ToArray(), Ct);
            }

            Assert.Equal(1, store.Calls);
        }
    }

    /// <summary>The bearer credential writes the token the caller holds; the packages never log in.</summary>
    [Fact]
    public async Task BearerTokenExchangeProducesTheIdentity()
    {
        byte[] token = Encoding.ASCII.GetBytes("header.payload.signature");

        Identity? identity = await AfidPump.ExchangeAsync(
            new TokenAuthenticator(token),
            new BearerTokenCredential("header.payload.signature"),
            Request,
            cancellationToken: Ct);

        Assert.NotNull(identity);
        Assert.Equal("glenda", identity.User);
    }

    /// <summary>The provider form is asked for a token on every attach, so a caller can refresh it.</summary>
    [Fact]
    public async Task BearerTokenProviderIsConsultedPerAttach()
    {
        int calls = 0;
        byte[] token = "rotating"u8.ToArray();
        BearerTokenCredential credential = new(_ =>
        {
            calls++;
            return ValueTask.FromResult("rotating");
        });

        await AfidPump.ExchangeAsync(new TokenAuthenticator(token), credential, Request, cancellationToken: Ct);
        await AfidPump.ExchangeAsync(new TokenAuthenticator(token), credential, Request, cancellationToken: Ct);

        Assert.Equal(2, calls);
    }

    /// <summary>The callback credential is the client-side extension point, and it runs as written.</summary>
    [Fact]
    public async Task CallbackCredentialRunsTheCallersExchange()
    {
        byte[] secret = "callback"u8.ToArray();
        CallbackCredential credential = new(async (channel, token) =>
        {
            await channel.WriteAsync(secret, token);
            ReadOnlyMemory<byte> answer = await channel.ReadAsync(3, token);
            Assert.Equal("ok\n"u8.ToArray(), answer.ToArray());
        });

        Identity? identity = await AfidPump.ExchangeAsync(
            new TokenAuthenticator(secret), credential, Request, cancellationToken: Ct);

        Assert.NotNull(identity);
    }

    /// <summary>The TLS authenticator derives the identity from the certificate, with no exchange.</summary>
    [Fact]
    public async Task TlsClientCertExchangeProducesTheCertificateIdentity()
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("glenda");
        PeerIdentity peer = new() { ClientCertificate = certificate };

        Identity? identity = await AfidPump.ExchangeAsync(
            new TlsClientCertAuthenticator(),
            new TokenCredential("ignored"u8.ToArray()),
            Request,
            peer,
            Ct);

        Assert.NotNull(identity);
        Assert.Equal("glenda", identity.User);
    }

    /// <summary>Without a client certificate there is nothing to derive, so Tauth is refused.</summary>
    [Fact]
    public async Task TlsClientCertWithoutACertificateRefusesTauth()
    {
        Identity? identity = await AfidPump.ExchangeAsync(
            new TlsClientCertAuthenticator(),
            new TokenCredential("ignored"u8.ToArray()),
            Request,
            peer: null,
            Ct);

        Assert.Null(identity);
    }

    /// <summary>An attach claiming a uname the certificate does not carry is refused.</summary>
    [Fact]
    public async Task TlsClientCertRefusesAMismatchedUname()
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("glenda");
        PeerIdentity peer = new() { ClientCertificate = certificate };

        Identity? identity = await AfidPump.ExchangeAsync(
            new TlsClientCertAuthenticator(),
            new TokenCredential("ignored"u8.ToArray()),
            new AuthRequest("rob", 1001, ""),
            peer,
            Ct);

        Assert.Null(identity);
    }

    /// <summary>An empty uname means "unspecified", so the certificate's name is taken as it is.</summary>
    [Fact]
    public async Task TlsClientCertAcceptsAnUnspecifiedUname()
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("glenda");
        PeerIdentity peer = new() { ClientCertificate = certificate };

        Identity? identity = await AfidPump.ExchangeAsync(
            new TlsClientCertAuthenticator(),
            new TokenCredential("ignored"u8.ToArray()),
            new AuthRequest("", Constants.NONUNAME, ""),
            peer,
            Ct);

        Assert.NotNull(identity);
        Assert.Equal("glenda", identity.User);
    }

    /// <summary>A caller-supplied mapping replaces the SAN/CN rule entirely.</summary>
    [Fact]
    public async Task TlsClientCertHonoursACustomMapping()
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("glenda");
        PeerIdentity peer = new() { ClientCertificate = certificate };

        Identity? identity = await AfidPump.ExchangeAsync(
            new TlsClientCertAuthenticator(_ => new Identity { User = "mapped", Uid = 7 }),
            new TokenCredential("ignored"u8.ToArray()),
            new AuthRequest("mapped", 7, ""),
            peer,
            Ct);

        Assert.NotNull(identity);
        Assert.Equal("mapped", identity.User);
        Assert.Equal(7u, identity.Uid);
    }

    /// <summary>Every shipped authenticator requires an afid; none of them lets a NOFID attach in.</summary>
    [Fact]
    public void EveryShippedAuthenticatorRequiresAnAfid()
    {
        Assert.True(new TokenAuthenticator("x"u8.ToArray()).IsRequired);
        Assert.True(new PasswordAuthenticator(new NeverStore()).IsRequired);
        Assert.True(new TlsClientCertAuthenticator().IsRequired);
    }

    /// <summary>An anonymous identity is the claim and nothing more.</summary>
    [Fact]
    public void AnonymousIdentityCarriesNoGroups()
    {
        Identity anonymous = Identity.Anonymous("nobody", 65534);

        Assert.Equal("nobody", anonymous.User);
        Assert.Equal(65534u, anonymous.Uid);
        Assert.Empty(anonymous.Groups);
        Assert.Empty(anonymous.Claims);
    }

    /// <summary>
    /// An absent uid is the wire sentinel, never a nullable (workspace architecture §12 rule 2):
    /// an identity built without one, anonymous or not, carries NONUNAME rather than 0, which
    /// would be root.
    /// </summary>
    [Fact]
    public void AnIdentityWithoutAUidCarriesNonuname()
    {
        Assert.Equal(Constants.NONUNAME, Identity.Anonymous("nobody").Uid);
        Assert.Equal(Constants.NONUNAME, new Identity { User = "glenda" }.Uid);
    }

    /// <summary>A credential that cannot be a credential is refused at construction.</summary>
    [Fact]
    public void ImpossibleCredentialsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new TokenCredential(default));
        Assert.Throws<ArgumentException>(() => new TokenAuthenticator(default(ReadOnlyMemory<byte>)));
        Assert.Throws<ArgumentException>(() => new PasswordCredential("gle\nnda", "x"));
        Assert.Throws<ArgumentException>(() => new PasswordCredential("glenda", "x\ny"));
        Assert.Throws<ArgumentException>(() => new BearerTokenCredential(string.Empty));
        Assert.Throws<ArgumentNullException>(() => new CallbackCredential(null!));
        Assert.Throws<ArgumentNullException>(() => new PasswordAuthenticator(null!));
        Assert.Throws<ArgumentNullException>(
            () => new TokenAuthenticator(default(Func<AuthRequest, ReadOnlyMemory<byte>?>)!));
    }

    /// <summary>Constant-time equality answers what an ordinary comparison would.</summary>
    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("abc", "ab", false)]
    [InlineData("", "", true)]
    public void ConstantTimeAgreesWithEquality(string left, string right, bool expected) =>
        Assert.Equal(
            expected,
            ConstantTime.Equals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right)));

    /// <summary>The fastest of three verifications, in wall time.</summary>
    /// <param name="store">The store to verify against.</param>
    /// <param name="user">The user name to present.</param>
    /// <returns>The shortest of the three.</returns>
    private static TimeSpan FastestVerify(PasswordFileStore store, string user)
    {
        TimeSpan fastest = TimeSpan.MaxValue;

        for (int i = 0; i < 3; i++)
        {
            long started = Stopwatch.GetTimestamp();
            store.VerifyAsync(user, "hunter2"u8.ToArray(), Ct).AsTask().GetAwaiter().GetResult();
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

            if (elapsed < fastest)
            {
                fastest = elapsed;
            }
        }

        return fastest;
    }

    /// <summary>A store that counts how often it was asked, and always says yes.</summary>
    private sealed class CountingStore : IPasswordStore
    {
        public int Calls { get; private set; }

        public ValueTask<Identity?> VerifyAsync(
            string user, ReadOnlyMemory<byte> password, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult<Identity?>(new Identity { User = user });
        }
    }

    private sealed class NeverStore : IPasswordStore
    {
        public ValueTask<Identity?> VerifyAsync(
            string user, ReadOnlyMemory<byte> password, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Identity?>(null);
    }

    private sealed class TempPasswordFile : IDisposable
    {
        private TempPasswordFile(string path) => Path = path;

        public string Path { get; }

        public static TempPasswordFile With(string user, string password)
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ninep-" + Guid.NewGuid().ToString("N") + ".pw");

            // 600 000 iterations is deliberately slow; the tests that need the real cost say so,
            // and this one only needs a line the store will parse.
            File.WriteAllText(
                path,
                user + ":" + PasswordFileStore.HashPassword(password, PasswordFileStore.MinimumIterations) + "\n");

            return new TempPasswordFile(path);
        }

        public void Dispose() => File.Delete(Path);
    }
}
