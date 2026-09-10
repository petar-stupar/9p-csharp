using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
#if NET10_0_OR_GREATER
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.TestSupport.FakeIssuer;
using NineP.TodoFs;
using Xunit;
using NineP.TestSupport;

namespace NineP.Server.Tests.Security;

/// <summary>
/// §8.3(a): every validation rule, one named case each, against the in-process issuer of §8.4. A
/// token that fails any of them is <c>"authentication failed"</c> and nothing more specific — a
/// client that could tell "wrong audience" from "expired" would learn something about the realm.
/// </summary>
[Trait("Category", "Security")]
public sealed class KeycloakAuthTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>A token the realm issued for this audience, in date, proves its identity.</summary>
    [Fact]
    public async Task ValidTokenProvesTheIdentity()
    {
        using FakeOidcIssuer issuer = FakeOidcIssuer.Start();
        using KeycloakAuthenticator authenticator = Authenticator(issuer);

        Identity identity = await authenticator.ValidateAsync(
            issuer.IssueToken(new FakeTokenOptions { Roles = ["todofs-admin"] }), Ct);

        Assert.Equal("glenda", identity.User);
        Assert.Contains("todofs-admin", identity.Groups);
    }

    /// <summary>The EC key of the realm's key set is accepted as well as the RSA one.</summary>
    [Fact]
    public async Task Es256IsAccepted()
    {
        using FakeOidcIssuer issuer = FakeOidcIssuer.Start();
        using KeycloakAuthenticator authenticator = Authenticator(issuer);

        Identity identity = await authenticator.ValidateAsync(
            issuer.IssueToken(new FakeTokenOptions { Key = FakeSigningKey.EllipticCurve }), Ct);

        Assert.Equal("glenda", identity.User);
    }

    /// <summary>Without <c>preferred_username</c> the identity is the <c>sub</c> claim.</summary>
    [Fact]
    public async Task IdentityFallsBackToSub()
    {
        using FakeOidcIssuer issuer = FakeOidcIssuer.Start();
        using KeycloakAuthenticator authenticator = Authenticator(issuer);

        Identity identity = await authenticator.ValidateAsync(
            issuer.IssueToken(new FakeTokenOptions { PreferredUsername = null, Subject = "sub-42" }), Ct);

        Assert.Equal("sub-42", identity.User);
    }

    /// <summary>A signature that does not verify against the published key is refused.</summary>
    [Fact]
    public Task RejectsBadSignature() =>
        RejectedAsync(new FakeTokenOptions { Key = FakeSigningKey.BadSignature });

    /// <summary>A token minted for another audience is refused.</summary>
    [Fact]
    public Task RejectsWrongAudience() =>
        RejectedAsync(new FakeTokenOptions { Audience = "some-other-service" });

    /// <summary>A token whose lifetime ended more than the leeway ago is refused.</summary>
    [Fact]
    public Task RejectsExpired() =>
        RejectedAsync(new FakeTokenOptions
        {
            NotBefore = TimeSpan.FromHours(-2),
            Lifetime = TimeSpan.FromMinutes(1),
        });

    /// <summary>A token whose <c>nbf</c> is beyond the leeway in the future is refused.</summary>
    [Fact]
    public Task RejectsNotYetValid() =>
        RejectedAsync(new FakeTokenOptions { NotBefore = TimeSpan.FromHours(1) });

    /// <summary>A header claiming <c>alg=none</c> is refused, signature or no signature.</summary>
    [Fact]
    public Task RejectsAlgNone() =>
        RejectedAsync(new FakeTokenOptions { Key = FakeSigningKey.None });

    /// <summary>A token under a <c>kid</c> the realm never published is refused.</summary>
    [Fact]
    public Task RejectsUnknownKid() =>
        RejectedAsync(new FakeTokenOptions { Key = FakeSigningKey.UnknownKid });

    /// <summary>A token signed by another key under a published <c>kid</c> is refused.</summary>
    [Fact]
    public Task RejectsWrongKey() =>
        RejectedAsync(new FakeTokenOptions { Key = FakeSigningKey.WrongKey });

    /// <summary>A token claiming another issuer is refused.</summary>
    [Fact]
    public Task RejectsWrongIssuer() =>
        RejectedAsync(new FakeTokenOptions { Issuer = "https://evil.invalid/realms/other" });

    /// <summary>
    /// The <c>Tattach</c> uname must be the identity the token proved, or empty. Otherwise a valid
    /// token would authenticate an attach claiming anybody.
    /// </summary>
    [Fact]
    public async Task AttachUnameMustMatchTheToken()
    {
        using FakeOidcIssuer issuer = FakeOidcIssuer.Start();
        using KeycloakAuthenticator authenticator = Authenticator(issuer);

        Assert.NotNull(await ExchangeAsync(authenticator, issuer, "glenda"));
        Assert.NotNull(await ExchangeAsync(authenticator, issuer, string.Empty));

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await ExchangeAsync(authenticator, issuer, "bootes"));

        Assert.Equal("authentication failed", refusal.Error.Ename);
    }

    private static async Task RejectedAsync(FakeTokenOptions options)
    {
        using FakeOidcIssuer issuer = FakeOidcIssuer.Start();
        using KeycloakAuthenticator authenticator = Authenticator(issuer);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await authenticator.ValidateAsync(issuer.IssueToken(options), Ct));

        Assert.Equal("authentication failed", refusal.Error.Ename);
        Assert.Equal(Errno.EACCES, refusal.Error.Errno);
    }

    private static async Task<Identity?> ExchangeAsync(
        KeycloakAuthenticator authenticator, FakeOidcIssuer issuer, string uname)
    {
        IAuthSession session = await authenticator.BeginAsync(
            new AuthRequest(uname, Constants.NONUNAME, string.Empty), peer: null, Ct)
            ?? throw new InvalidOperationException("Tauth was refused");

        await using (session)
        {
            await session.WriteAsync(TodoText.Utf8.GetBytes(issuer.IssueToken()), Ct);
            await session.ReadAsync(64, Ct);
            return session.Identity;
        }
    }

    internal static KeycloakAuthenticator Authenticator(FakeOidcIssuer issuer) => new(new KeycloakOptions
    {
        Issuer = issuer.Issuer,
        Audience = issuer.Audience,
        RequireHttps = false,
    });
}

/// <summary>The same rule under the name <c>docs/rule-index.md</c> row 73 gives it.</summary>
[Trait("Category", "Security")]
public sealed class KeycloakAuthenticatorTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>Every token the realm did not issue for this audience, in date, is refused.</summary>
    [Fact]
    public async Task RejectsBadTokens()
    {
        using FakeOidcIssuer issuer = FakeOidcIssuer.Start();
        using KeycloakAuthenticator authenticator = KeycloakAuthTests.Authenticator(issuer);

        FakeTokenOptions[] bad =
        [
            new() { Key = FakeSigningKey.BadSignature },
            new() { Key = FakeSigningKey.None },
            new() { Key = FakeSigningKey.UnknownKid },
            new() { Key = FakeSigningKey.WrongKey },
            new() { Audience = "some-other-service" },
            new() { Issuer = "https://evil.invalid/realms/other" },
            new() { NotBefore = TimeSpan.FromHours(-2), Lifetime = TimeSpan.FromMinutes(1) },

            // A token with no exp at all: the custom LifetimeValidator used to answer true for a
            // null expiry, which overrode RequireExpirationTime and made such a token valid for
            // ever.
            new() { OmitExpiry = true },
        ];

        foreach (FakeTokenOptions options in bad)
        {
            await Assert.ThrowsAsync<NinePException>(
                async () => await authenticator.ValidateAsync(issuer.IssueToken(options), Ct));
        }

        // The good token still works, so the refusals were about the tokens and not about the realm.
        Assert.Equal("glenda", (await authenticator.ValidateAsync(issuer.IssueToken(), Ct)).User);
    }
}

/// <summary>RK-48: an unknown <c>kid</c> costs the realm one JWKS fetch, not one per request.</summary>
[Trait("Category", "Security")]
public sealed class JwksTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>A hundred forged key ids trigger at most one refresh at the issuer.</summary>
    [Fact]
    public async Task UnknownKidDoesNotStampedeIssuer()
    {
        using FakeOidcIssuer issuer = FakeOidcIssuer.Start();
        using KeycloakAuthenticator authenticator = KeycloakAuthTests.Authenticator(issuer);

        // One good token first, so the initial fetch is not counted against the forgeries.
        await authenticator.ValidateAsync(issuer.IssueToken(), Ct);
        int before = issuer.Hits.GetValueOrDefault("/jwks");

        for (int i = 0; i < 100; i++)
        {
            await Assert.ThrowsAsync<NinePException>(async () => await authenticator.ValidateAsync(
                issuer.IssueToken(new FakeTokenOptions { Key = FakeSigningKey.UnknownKid }), Ct));
        }

        int after = issuer.Hits.GetValueOrDefault("/jwks");
        Assert.True(after - before <= 1, $"{after - before} JWKS fetches for 100 forged kids");
    }
}
#endif
