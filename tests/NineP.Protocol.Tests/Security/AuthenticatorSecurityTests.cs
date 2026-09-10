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

namespace NineP.Protocol.Tests.Security;

/// <summary>
/// Every shipped authenticator against its mirror credential, over a real
/// <see cref="MemoryTransport"/> wire (S-31).
/// </summary>
[Trait("Category", "Security")]
public sealed class AuthenticatorSecurityTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    private static AuthRequest Request => new("glenda", 1000, "");



    /// <summary>A lookup with no secret for the request refuses the Tauth outright.</summary>
    [Fact]
    public async Task TokenLookupWithNoSecretRefusesTauth()
    {
        TokenAuthenticator authenticator = new(_ => null);

        Identity? identity = await AfidPump.ExchangeAsync(
            authenticator, new TokenCredential("anything"u8.ToArray()), Request, cancellationToken: Ct);

        Assert.Null(identity);
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
