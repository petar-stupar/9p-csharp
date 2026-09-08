using System.Globalization;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Transports;

namespace NineP.TodoFs;

/// <summary>
/// Validates a Keycloak access token written to an afid, and answers <c>ok\n</c> when it holds
/// (§8.3). This is an <see cref="IAuthenticator"/> in the example and in no published package:
/// the packages never perform a login and never depend on an OIDC library (S-30).
/// </summary>
internal sealed class KeycloakAuthenticator : IAuthenticator, IDisposable
{
    private readonly KeycloakOptions _options;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configuration;
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>Builds an authenticator for one realm.</summary>
    /// <param name="options">The issuer, the audience and the bounds.</param>
    public KeycloakAuthenticator(KeycloakOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _configuration = new ConfigurationManager<OpenIdConnectConfiguration>(
            options.Issuer.TrimEnd('/') + "/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = options.RequireHttps })
        {
            AutomaticRefreshInterval = options.JwksCache < ConfigurationManager<OpenIdConnectConfiguration>.MinimumAutomaticRefreshInterval
                ? ConfigurationManager<OpenIdConnectConfiguration>.MinimumAutomaticRefreshInterval
                : options.JwksCache,
        };
    }

    /// <summary>
    /// An attach must present a verified afid: a bearer token is the only credential, so a
    /// <c>Tattach</c> with <c>afid = NOFID</c> is refused.
    /// </summary>
    public bool IsRequired => true;

    /// <summary>Starts an afid exchange.</summary>
    /// <param name="request">The triple the afid is bound to.</param>
    /// <param name="peer">Unused: a bearer token proves the client, not the transport.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The session that will judge the token.</returns>
    public ValueTask<IAuthSession?> BeginAsync(
        AuthRequest request, PeerIdentity? peer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // CA2000: the session is the server core's to dispose once the afid is clunked.
#pragma warning disable CA2000
        return ValueTask.FromResult<IAuthSession?>(new KeycloakAuthSession(this, request));
#pragma warning restore CA2000
    }

    /// <summary>Validates one access token and produces the identity it proves.</summary>
    /// <param name="token">The compact JWT.</param>
    /// <param name="cancellationToken">Cancels the validation.</param>
    /// <returns>The identity the token proves.</returns>
    /// <exception cref="NinePException">The token is not one this realm issued for this audience.</exception>
    public async Task<Identity> ValidateAsync(string token, CancellationToken cancellationToken = default)
    {
        TokenValidationResult result = await ValidateOnceAsync(token).ConfigureAwait(false);

        // §8.3: an unknown kid asks the realm for its keys again — once. RequestRefresh is
        // rate-limited by the manager's own refresh interval, so a hundred forged kids cost one
        // fetch and not a hundred (RK-48). A failed refresh is not retried per request either.
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            _configuration.RequestRefresh();
            result = await ValidateOnceAsync(token).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return result.IsValid
            ? IdentityOf(result)
            : throw new NinePException(NinePError.FromEname("authentication failed"));
    }

    /// <summary>
    /// Nothing of this type owns an unmanaged resource: the configuration manager keeps its own
    /// <c>HttpClient</c> and does not expose it. <see cref="IDisposable"/> is declared so that a
    /// caller's <c>using</c> stays correct if that ever changes.
    /// </summary>
    public void Dispose() => GC.SuppressFinalize(this);

    /// <summary>The name the session runs as, and the realm roles it carries.</summary>
    /// <param name="result">A validated token.</param>
    /// <returns>The identity.</returns>
    private static Identity IdentityOf(TokenValidationResult result)
    {
        // §8.3: preferred_username, falling back to sub. A token with neither names nobody.
        string user = Claim(result, "preferred_username")
            ?? Claim(result, "sub")
            ?? throw new NinePException(NinePError.FromEname("authentication failed"));

        // Not Identity.Anonymous: this identity was proved by a signed token, and Anonymous is
        // what an attach that proved nothing carries.
        return new Identity { User = user, Groups = Roles(result) };
    }

    private static string? Claim(TokenValidationResult result, string name) =>
        result.Claims.TryGetValue(name, out object? value) ? value as string : null;

    /// <summary>
    /// The realm roles of <c>realm_access.roles</c>, which reach the tree as the identity's groups
    /// so that the admin check is one membership test.
    /// </summary>
    /// <param name="result">A validated token.</param>
    /// <returns>The roles, or an empty list.</returns>
    private static List<string> Roles(TokenValidationResult result)
    {
        if (!result.Claims.TryGetValue("realm_access", out object? claim))
        {
            return [];
        }

        try
        {
            using JsonDocument realm = JsonDocument.Parse(claim as string ?? claim.ToString() ?? "{}");
            if (!realm.RootElement.TryGetProperty("roles", out JsonElement roles)
                || roles.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<string> names = [];
            foreach (JsonElement role in roles.EnumerateArray())
            {
                if (role.GetString() is { } name)
                {
                    names.Add(name);
                }
            }

            return names;
        }
        catch (JsonException)
        {
            // A realm_access that is not an object is a token nobody should act on; it simply
            // carries no roles.
            return [];
        }
    }

    private Task<TokenValidationResult> ValidateOnceAsync(string token) =>
        _handler.ValidateTokenAsync(token, Parameters());

    private TokenValidationParameters Parameters() => new()
    {
        ConfigurationManager = _configuration,
        ValidIssuer = _options.Issuer,
        ValidateIssuer = true,
        ValidAudience = _options.Audience,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        RequireSignedTokens = true,
        RequireExpirationTime = true,

        // alg=none and an HS256 key-confusion token both die here: neither algorithm is listed,
        // and a symmetric key is never one of the realm's published signing keys.
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256],

        // The clock is the injected one, so a test does not have to wait out a real lifetime.
        ValidateLifetime = true,
        LifetimeValidator = ValidLifetime,
    };

    private bool ValidLifetime(
        DateTime? notBefore, DateTime? expires, SecurityToken token, TokenValidationParameters parameters)
    {
        DateTime now = _options.TimeProvider.GetUtcNow().UtcDateTime;

        if (notBefore is DateTime from && now + _options.ClockSkew < from)
        {
            return false;
        }

        // A token with no exp is refused. Returning true for a null expiry overrode
        // RequireExpirationTime and made such a token valid for ever (§8.3 validates exp).
        return expires is DateTime until && now - _options.ClockSkew <= until;
    }

    /// <summary>One afid exchange: the token in, <c>ok\n</c> out when it holds.</summary>
    private sealed class KeycloakAuthSession(KeycloakAuthenticator authenticator, AuthRequest request)
        : IAuthSession
    {
        private readonly List<byte> _token = [];

        public Identity? Identity { get; private set; }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _token.AddRange(data.Span);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(
            int maxBytes, CancellationToken cancellationToken = default)
        {
            if (Identity is not null || _token.Count == 0)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            string token = TodoText.Utf8.GetString([.. _token]).Trim();
            Identity proven = await authenticator.ValidateAsync(token, cancellationToken)
                .ConfigureAwait(false);

            // §8.3: the attach's claimed uname must be the identity the token proves, or empty.
            // Without this an operator's token would authenticate an attach claiming anyone.
            if (request.Uname.Length != 0
                && !string.Equals(request.Uname, proven.User, StringComparison.Ordinal))
            {
                throw new NinePException(NinePError.FromEname("authentication failed"));
            }

            Identity = proven;
            return "ok\n"u8.ToArray();
        }

        public ValueTask DisposeAsync()
        {
            _token.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
