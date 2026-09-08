using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NineP.Protocol.Transports;

/// <summary>
/// TLS configuration. Verification is on by default; the insecure opt-out is a separate, explicit
/// option and it is logged on every connect (workspace architecture §8.4), so a deployment that
/// turned it on cannot forget that it did.
/// </summary>
public sealed record TlsTransportOptions
{
    /// <summary>The server certificate with its private key; required in order to listen.</summary>
    public X509Certificate2? ServerCertificate { get; init; }

    /// <summary>Client certificates offered when dialling; supplying them enables mutual TLS.</summary>
    public X509Certificate2Collection? ClientCertificates { get; init; }

    /// <summary>
    /// Require a client certificate when listening; it becomes
    /// <see cref="PeerIdentity.ClientCertificate"/>.
    /// </summary>
    public bool RequireClientCertificate { get; init; }

    /// <summary>Extra roots used to validate the peer's chain; the platform store is used when null.</summary>
    public X509Certificate2Collection? TrustedRoots { get; init; }

    /// <summary>The host name verified against the server certificate; the dialled host when null.</summary>
    public string? TargetHost { get; init; }

    /// <summary>
    /// Disables chain and host-name verification <b>when dialling</b>, and logs a warning on every
    /// connect. Default false, and there is no way to reach it by accident: it is not implied by
    /// any other setting. It is deliberately client-side only — a listener with
    /// <see cref="RequireClientCertificate"/> still verifies the certificate a client presents,
    /// because otherwise a flag about the server's certificate would silently turn a client
    /// certificate into no authentication at all. A listener configured with it logs that it is
    /// being ignored.
    /// </summary>
    public bool AllowInsecureCertificates { get; init; }

    /// <summary>
    /// A callback that may only <b>add</b> checks: it is consulted after the chain and the host
    /// name have been verified, so returning true never rescues a certificate that failed them.
    /// </summary>
    public Func<X509Certificate2, bool>? AdditionalPeerCheck { get; init; }

    /// <summary>The handshake timeout. Default 30 s.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The transport used underneath; a default <see cref="TcpTransport"/> when null.</summary>
    public TcpTransportOptions? Tcp { get; init; }

    /// <summary>Where the insecure-mode warning goes; <see cref="NullLogger.Instance"/> when null.</summary>
    public ILogger? Logger { get; init; }
}
