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

/// <summary>How <see cref="KeycloakAuthenticator"/> validates a token (§8.3).</summary>
internal sealed record KeycloakOptions
{
    /// <summary>The realm's issuer URL; a token claiming another <c>iss</c> is refused.</summary>
    public required string Issuer { get; init; }

    /// <summary>The audience this server is; a token for another <c>aud</c> is refused.</summary>
    public required string Audience { get; init; }

    /// <summary>The realm role that may use <c>/users/ctl</c>.</summary>
    public string AdminRole { get; init; } = "todofs-admin";

    /// <summary>How long the realm's JWKS is reused before it is fetched again.</summary>
    public TimeSpan JwksCache { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The leeway on <c>exp</c> and <c>nbf</c>, per §8.3.</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The clock lifetimes are judged against.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Whether the discovery and JWKS documents must be fetched over HTTPS. It is true in every
    /// deployment; the in-process fake issuer of §8.4 serves plain HTTP on the loopback interface,
    /// which is the one case that turns it off, and <c>--allow-insecure-issuer</c> is the flag
    /// that does so (E-4).
    /// </summary>
    public bool RequireHttps { get; init; } = true;
}
