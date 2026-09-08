using System.Security.Cryptography.X509Certificates;
using NineP.Protocol.Auth.Internal;
using NineP.Protocol.Transports;

namespace NineP.Protocol.Auth;

/// <summary>
/// Derives the identity from the peer's TLS client certificate (workspace architecture §5). There
/// is nothing to exchange over the afid: mutual TLS already proved who the peer is before the first
/// 9P byte, so the session is authenticated the moment it begins — and an attach that claims a
/// different <c>uname</c> is refused rather than quietly renamed.
/// </summary>
public sealed class TlsClientCertAuthenticator : IAuthenticator
{
    private readonly Func<X509Certificate2, Identity?> _mapper;

    /// <summary>Creates an authenticator over a certificate-to-identity mapping.</summary>
    /// <param name="mapper">The mapping; the default takes the first DNS SAN, else the CN.</param>
    public TlsClientCertAuthenticator(Func<X509Certificate2, Identity?>? mapper = null) =>
        _mapper = mapper ?? DefaultMapper;

    /// <summary>An attach must present a verified afid.</summary>
    public bool IsRequired => true;

    /// <summary>The identity the default mapping derives: the first DNS SAN, else the common name.</summary>
    /// <param name="certificate">The peer's client certificate.</param>
    /// <returns>The identity, or null when the certificate names nobody.</returns>
    /// <exception cref="ArgumentNullException">The certificate is null.</exception>
    public static Identity? DefaultMapper(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        string? name = FirstDnsName(certificate) ?? CommonName(certificate);
        return name is null ? null : new Identity { User = name };
    }

    /// <summary>Authenticates from the certificate the transport already validated.</summary>
    /// <param name="request">The triple the afid will be bound to.</param>
    /// <param name="peer">The transport's evidence; without a client certificate there is none.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The session, or null when there is no certificate or the uname disagrees.</returns>
    public ValueTask<IAuthSession?> BeginAsync(
        AuthRequest request, PeerIdentity? peer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (peer?.ClientCertificate is not { } certificate || _mapper(certificate) is not { } identity)
        {
            return ValueTask.FromResult<IAuthSession?>(null);
        }

        // An empty uname means "unspecified" (S-22); anything else must be the name the certificate
        // carries, or the client is asking to be somebody it has not proved it is.
        if (request.Uname.Length != 0 && !string.Equals(request.Uname, identity.User, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<IAuthSession?>(null);
        }

        // CA2000: the session is the core's to dispose once the afid is clunked.
#pragma warning disable CA2000
        return ValueTask.FromResult<IAuthSession?>(new TlsClientCertSession(identity));
#pragma warning restore CA2000
    }

    private static string? FirstDnsName(X509Certificate2 certificate)
    {
        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension is X509SubjectAlternativeNameExtension san)
            {
                foreach (string name in san.EnumerateDnsNames())
                {
                    return name;
                }
            }
        }

        return null;
    }

    private static string? CommonName(X509Certificate2 certificate)
    {
        string name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return name.Length == 0 ? null : name;
    }
}
