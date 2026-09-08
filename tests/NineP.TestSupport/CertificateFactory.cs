using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NineP.TestSupport;

/// <summary>
/// Certificates for the transport tests, generated in this process and thrown away with it (E-3).
/// Nothing is committed: a fixture certificate is key material in a repository and it rots on its
/// expiry date, which turns a green suite red on a day nobody changed anything.
/// </summary>
public static class CertificateFactory
{
    /// <summary>Creates a self-signed certificate with the given subject alternative names.</summary>
    /// <param name="commonName">The subject common name.</param>
    /// <param name="dnsNames">The DNS subject alternative names; the common name when empty.</param>
    /// <returns>A certificate with an exportable private key, valid for one day.</returns>
    /// <exception cref="ArgumentNullException">The common name is null.</exception>
    public static X509Certificate2 CreateSelfSigned(string commonName, params string[] dnsNames)
    {
        ArgumentNullException.ThrowIfNull(commonName);

        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=" + commonName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder names = new();
        foreach (string name in dnsNames is { Length: > 0 } ? dnsNames : [commonName])
        {
            names.AddDnsName(name);
        }

        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment
            | X509KeyUsageFlags.KeyCertSign,
            critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], critical: false));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 ephemeral = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));

        // SslStream needs a certificate whose private key it can use; the ephemeral key a fresh
        // CertificateRequest produces is not always usable directly, and a PKCS#12 round trip is
        // the portable way to attach one.
        return Load(ephemeral.Export(X509ContentType.Pfx));
    }

    /// <summary>The certificate as a collection, which is what the TLS options take.</summary>
    /// <param name="certificate">The certificate to wrap.</param>
    /// <returns>A one-element collection.</returns>
    /// <exception cref="ArgumentNullException">The certificate is null.</exception>
    public static X509Certificate2Collection AsCollection(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return [certificate];
    }

    private static X509Certificate2 Load(byte[] pkcs12) =>
#if NET9_0_OR_GREATER
        X509CertificateLoader.LoadPkcs12(pkcs12, password: null, X509KeyStorageFlags.Exportable);
#else
        new X509Certificate2(pkcs12, (string?)null, X509KeyStorageFlags.Exportable);
#endif
}
