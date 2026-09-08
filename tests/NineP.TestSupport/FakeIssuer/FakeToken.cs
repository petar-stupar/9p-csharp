using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NineP.TestSupport.FakeIssuer;

/// <summary>Which key a fake token is signed with, or that it is not signed at all.</summary>
public enum FakeSigningKey
{
    /// <summary>The issuer's published RSA key: <c>RS256</c>.</summary>
    Rsa = 0,

    /// <summary>The issuer's published EC key: <c>ES256</c>.</summary>
    EllipticCurve = 1,

    /// <summary>A key the issuer never published, under a <c>kid</c> that is in the JWKS.</summary>
    WrongKey = 2,

    /// <summary>A key the issuer never published, under a <c>kid</c> that is not in the JWKS.</summary>
    UnknownKid = 3,

    /// <summary><c>alg=none</c>: a header claiming no signature at all.</summary>
    None = 4,

    /// <summary>The right key and header, with the signature bytes corrupted.</summary>
    BadSignature = 5,
}

/// <summary>Everything a test can vary about one token the fake issuer mints.</summary>
public sealed record FakeTokenOptions
{
    /// <summary>The <c>sub</c> claim.</summary>
    public string Subject { get; init; } = "0000-subject";

    /// <summary>The <c>preferred_username</c> claim; null omits it, so <c>sub</c> is the identity.</summary>
    public string? PreferredUsername { get; init; } = "glenda";

    /// <summary>The realm roles the token carries in <c>realm_access.roles</c>.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>The <c>iss</c> claim; null uses the issuer's own URL.</summary>
    public string? Issuer { get; init; }

    /// <summary>The <c>aud</c> claim; null uses the issuer's configured audience.</summary>
    public string? Audience { get; init; }

    /// <summary>How long the token is valid for, from <see cref="NotBefore"/>.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>When the token becomes valid, relative to now.</summary>
    public TimeSpan NotBefore { get; init; } = TimeSpan.FromMinutes(-1);

    /// <summary>True to mint a token with no <c>exp</c> claim at all.</summary>
    public bool OmitExpiry { get; init; }

    /// <summary>Which key signs it.</summary>
    public FakeSigningKey Key { get; init; } = FakeSigningKey.Rsa;
}

/// <summary>
/// A JWT built by hand. The signing libraries will not mint a token with <c>alg=none</c> or with a
/// deliberately broken signature, which are two of the cases the validator has to refuse, so the
/// fixture writes the three parts itself.
/// </summary>
internal static class FakeToken
{
    /// <summary>Builds one token.</summary>
    /// <param name="options">What to vary.</param>
    /// <param name="issuer">The issuer's URL, for the default <c>iss</c>.</param>
    /// <param name="audience">The issuer's audience, for the default <c>aud</c>.</param>
    /// <param name="keys">The issuer's keys.</param>
    /// <param name="now">The clock the timestamps come from.</param>
    /// <returns>The compact serialisation.</returns>
    public static string Build(
        FakeTokenOptions options,
        string issuer,
        string audience,
        FakeIssuerKeys keys,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keys);

        long notBefore = now.Add(options.NotBefore).ToUnixTimeSeconds();
        string header = Header(options, keys);
        string payload = Payload(options, issuer, audience, notBefore, notBefore + (long)options.Lifetime.TotalSeconds);

        string signingInput = Encode(header) + "." + Encode(payload);

        return options.Key == FakeSigningKey.None
            ? signingInput + "."
            : signingInput + "." + Base64Url(Sign(options, keys, signingInput));
    }

    private static string Header(FakeTokenOptions options, FakeIssuerKeys keys)
    {
        (string algorithm, string kid) = options.Key switch
        {
            FakeSigningKey.EllipticCurve => ("ES256", keys.EcKid),
            FakeSigningKey.None => ("none", keys.RsaKid),
            FakeSigningKey.UnknownKid => ("RS256", "kid-that-was-never-published"),
            _ => ("RS256", keys.RsaKid),
        };

        return string.Format(
            CultureInfo.InvariantCulture,
            "{{\"alg\":\"{0}\",\"typ\":\"JWT\",\"kid\":\"{1}\"}}",
            algorithm,
            kid);
    }

    private static string Payload(
        FakeTokenOptions options, string issuer, string audience, long notBefore, long expires)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("iss", options.Issuer ?? issuer);
            writer.WriteString("aud", options.Audience ?? audience);
            writer.WriteString("sub", options.Subject);
            writer.WriteNumber("iat", notBefore);
            writer.WriteNumber("nbf", notBefore);
            if (!options.OmitExpiry)
            {
                writer.WriteNumber("exp", expires);
            }

            if (options.PreferredUsername is { } username)
            {
                writer.WriteString("preferred_username", username);
            }

            writer.WriteStartObject("realm_access");
            writer.WriteStartArray("roles");
            foreach (string role in options.Roles)
            {
                writer.WriteStringValue(role);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static byte[] Sign(FakeTokenOptions options, FakeIssuerKeys keys, string signingInput)
    {
        byte[] data = Encoding.UTF8.GetBytes(signingInput);

        byte[] signature = options.Key switch
        {
            FakeSigningKey.EllipticCurve => keys.Ec.SignData(
                data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
            FakeSigningKey.WrongKey or FakeSigningKey.UnknownKid => keys.Impostor.SignData(
                data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            _ => keys.Rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
        };

        if (options.Key == FakeSigningKey.BadSignature)
        {
            signature[0] ^= 0xFF;
        }

        return signature;
    }

    private static string Encode(string json) => Base64Url(Encoding.UTF8.GetBytes(json));

    /// <summary>Base64url without padding, which is what a JWS carries.</summary>
    /// <param name="bytes">The bytes to encode.</param>
    /// <returns>The encoded text.</returns>
    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
