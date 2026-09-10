using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NineP.Protocol.Auth;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

/// <summary>
/// Every shipped authenticator against its mirror credential, over a real
/// <see cref="MemoryTransport"/> wire (S-31).
/// </summary>
[Trait("Category", "Conformance")]
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
