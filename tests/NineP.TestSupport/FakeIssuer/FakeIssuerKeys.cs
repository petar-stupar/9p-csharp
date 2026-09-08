using System.Globalization;
using System.Security.Cryptography;

namespace NineP.TestSupport.FakeIssuer;

/// <summary>
/// The keys the fake issuer signs with, generated in this process (E-3). There are no key files to
/// rot and no network access is needed; the impostor key exists so that a test can present a token
/// that is signed, and signed correctly, by the wrong key.
/// </summary>
public sealed class FakeIssuerKeys : IDisposable
{
    /// <summary>Generates a fresh set.</summary>
    public FakeIssuerKeys()
    {
        Rsa = RSA.Create(2048);
        Ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Impostor = RSA.Create(2048);
        RsaKid = "rsa-" + Guid.NewGuid().ToString("N")[..8];
        EcKid = "ec-" + Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>The published RSA key.</summary>
    public RSA Rsa { get; }

    /// <summary>The published EC key.</summary>
    public ECDsa Ec { get; }

    /// <summary>A key that is never published.</summary>
    public RSA Impostor { get; }

    /// <summary>The published RSA key's id.</summary>
    public string RsaKid { get; }

    /// <summary>The published EC key's id.</summary>
    public string EcKid { get; }

    /// <summary>The JWKS document these keys publish.</summary>
    /// <returns>The JSON of the key set.</returns>
    public string Jwks()
    {
        RSAParameters rsa = Rsa.ExportParameters(includePrivateParameters: false);
        ECParameters ec = Ec.ExportParameters(includePrivateParameters: false);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{{\"keys\":[" +
            "{{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"{0}\",\"n\":\"{1}\",\"e\":\"{2}\"}}," +
            "{{\"kty\":\"EC\",\"use\":\"sig\",\"alg\":\"ES256\",\"crv\":\"P-256\",\"kid\":\"{3}\",\"x\":\"{4}\",\"y\":\"{5}\"}}" +
            "]}}",
            RsaKid,
            FakeToken.Base64Url(rsa.Modulus!),
            FakeToken.Base64Url(rsa.Exponent!),
            EcKid,
            FakeToken.Base64Url(ec.Q.X!),
            FakeToken.Base64Url(ec.Q.Y!));
    }

    /// <summary>Releases the keys.</summary>
    public void Dispose()
    {
        Rsa.Dispose();
        Ec.Dispose();
        Impostor.Dispose();
    }
}
