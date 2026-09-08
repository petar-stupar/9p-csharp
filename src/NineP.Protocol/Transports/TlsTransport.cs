using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NineP.Protocol.Internal;
using NineP.Protocol.Transports.Internal;

namespace NineP.Protocol.Transports;

/// <summary>
/// TLS 1.2 / 1.3 over TCP, with chain and host-name verification on by default and optional mutual
/// TLS. The insecure opt-out is a separate explicit option that logs on every connect, and
/// <see cref="TlsTransportOptions.AdditionalPeerCheck"/> can only ever <b>add</b> a check: it runs
/// after verification has already passed, so no callback can turn a bad certificate into a good
/// one. Closing sends <c>close_notify</c> before the socket goes away (RK-57).
/// </summary>
public sealed class TlsTransport : ITransport
{
    /// <summary>The floor and the ceiling: 1.2 minimum, 1.3 preferred, nothing older.</summary>
    /// <remarks>
    /// CA5398 asks for <c>SslProtocols.None</c> so that the operating system chooses. This
    /// workspace's architecture §3 fixes the floor at TLS 1.2 instead, and a test asserts that
    /// TLS 1.1 is refused; leaving the choice to the platform would make that assertion depend on
    /// the machine's configuration rather than on this library.
    /// </remarks>
#pragma warning disable CA5398
    internal const SslProtocols EnabledProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
#pragma warning restore CA5398

    private static readonly NinePScheme[] SupportedSchemes = [NinePScheme.Tls];

    private readonly TlsTransportOptions _options;
    private readonly TcpTransport _tcp;
    private readonly ILogger _logger;

    /// <summary>Creates a TLS transport.</summary>
    /// <param name="options">The TLS configuration; a server certificate is needed to listen.</param>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    public TlsTransport(TlsTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _tcp = new TcpTransport(options.Tcp);
        _logger = options.Logger ?? NullLogger.Instance;
    }

    /// <summary>The schemes this transport can dial and bind.</summary>
    public IReadOnlyCollection<NinePScheme> Schemes => SupportedSchemes;

    /// <summary>Dials a TLS endpoint and completes the handshake before returning.</summary>
    /// <param name="address">The <c>tls://host:port</c> endpoint.</param>
    /// <param name="cancellationToken">Cancels the dial and the handshake.</param>
    /// <returns>The authenticated connection; the caller disposes it.</returns>
    /// <exception cref="ArgumentException">The address is not a TLS address.</exception>
    /// <exception cref="AuthenticationException">The peer's certificate was not accepted.</exception>
    public async ValueTask<INinePConnection> ConnectAsync(
        NinePAddress address, CancellationToken cancellationToken = default)
    {
        TcpTransport.Require(address, NinePScheme.Tls);

        if (_options.AllowInsecureCertificates)
        {
            _logger.TlsVerificationDisabled(UntrustedText.Sanitize(address.ToString()));
        }

        INinePConnection inner = await _tcp
            .ConnectAsync(address with { Scheme = NinePScheme.Tcp }, cancellationToken)
            .ConfigureAwait(false);

        return await AuthenticateAsync(inner, address, isServer: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Binds a TLS endpoint.</summary>
    /// <param name="address">The <c>tls://host:port</c> endpoint; port 0 lets the kernel choose.</param>
    /// <param name="cancellationToken">Cancels the bind.</param>
    /// <returns>The listener, carrying the port that was actually bound.</returns>
    /// <exception cref="ArgumentException">The address is not a TLS address.</exception>
    /// <exception cref="InvalidOperationException">No server certificate was configured.</exception>
    public async ValueTask<INinePListener> ListenAsync(
        NinePAddress address, CancellationToken cancellationToken = default)
    {
        TcpTransport.Require(address, NinePScheme.Tls);

        if (_options.ServerCertificate is null)
        {
            throw new InvalidOperationException("a TLS listener needs TlsTransportOptions.ServerCertificate");
        }

        if (_options.AllowInsecureCertificates)
        {
            // Said out loud rather than left to be discovered: the opt-out is a client-side one,
            // so a listener configured with it still verifies whatever a client presents.
            _logger.TlsInsecureOptOutIgnoredWhenListening(UntrustedText.Sanitize(address.ToString()));
        }

        INinePListener inner = await _tcp
            .ListenAsync(address with { Scheme = NinePScheme.Tcp }, cancellationToken)
            .ConfigureAwait(false);

        return new HandshakeListener(inner, address with { Port = inner.LocalAddress.Port },
            async (connection, token) => await AuthenticateAsync(connection,
                connection.RemoteAddress with { Scheme = NinePScheme.Tls }, isServer: true, token).ConfigureAwait(false),
            (_options.Tcp ?? new TcpTransportOptions()).MaxConnections);
    }

    /// <summary>Wraps one connected socket in an authenticated <see cref="SslStream"/>.</summary>
    /// <param name="inner">The TCP connection underneath; its socket is borrowed, not owned.</param>
    /// <param name="address">The address to record on the connection.</param>
    /// <param name="isServer">True on the accepting side.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <returns>The authenticated connection.</returns>
    internal async ValueTask<INinePConnection> AuthenticateAsync(
        INinePConnection inner, NinePAddress address, bool isServer, CancellationToken cancellationToken)
    {
        Socket socket = ((TcpTransportConnection)inner).Socket;

        // CA2000: the network stream is owned by the SslStream, and the SslStream by the
        // connection this method returns, which is the caller's to dispose. The only path that
        // returns nothing disposes both in its catch.
#pragma warning disable CA2000
        NetworkStream network = new(socket, ownsSocket: false);

        // The callback goes on the authentication options, never on the constructor as well:
        // SslStream refuses a session that names it twice.
        SslStream ssl = new(network, leaveInnerStreamOpen: false);
#pragma warning restore CA2000

        try
        {
            using CancellationTokenSource budget =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_options.HandshakeTimeout);

            if (isServer)
            {
                await ssl.AuthenticateAsServerAsync(ServerOptions(), budget.Token).ConfigureAwait(false);
            }
            else
            {
                await ssl.AuthenticateAsClientAsync(ClientOptions(address), budget.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            await inner.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // CA2000: the stream's lifetime is now the connection's, and the connection is the
        // caller's to dispose — the contract of ITransport.
#pragma warning disable CA2000
        return new TlsTransportConnection(ssl, inner, address);
#pragma warning restore CA2000
    }

    /// <summary>
    /// Authenticates a caller-owned stream as the server. The WebSocket listener needs the stream
    /// rather than a connection, and this keeps the peer-validation policy in one place.
    /// </summary>
    /// <param name="stream">The accepted stream; the returned SslStream owns it.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <returns>The authenticated stream.</returns>
    internal async ValueTask<SslStream> AuthenticateServerStreamAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        // CA2000: the SslStream is returned to the caller, which owns it and the stream under it.
#pragma warning disable CA2000
        SslStream ssl = new(stream, leaveInnerStreamOpen: false);
#pragma warning restore CA2000

        try
        {
            using CancellationTokenSource budget =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_options.HandshakeTimeout);

            await ssl.AuthenticateAsServerAsync(ServerOptions(), budget.Token).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The client-side authentication options this transport's policy produces.</summary>
    /// <param name="host">The host name to verify the certificate against.</param>
    /// <returns>The options, carrying this transport's validation callback.</returns>
    internal SslClientAuthenticationOptions ClientAuthenticationOptions(string host) =>
        ClientOptionsFor(_options.TargetHost ?? Unbracket(host));

    /// <summary>The server-side authentication options this transport's policy produces.</summary>
    /// <returns>The options, carrying this transport's validation callback.</returns>
    internal SslServerAuthenticationOptions ServerAuthenticationOptions() => ServerOptions();

    private SslServerAuthenticationOptions ServerOptions() => new()
    {
        ServerCertificate = _options.ServerCertificate,
        ClientCertificateRequired = _options.RequireClientCertificate,
#pragma warning disable CA5398 // See EnabledProtocols: the floor is this workspace's, not the platform's.
        EnabledSslProtocols = EnabledProtocols,
#pragma warning restore CA5398
        // Renegotiation is off as a control of this library, not as a platform default: a
        // renegotiated handshake can change the peer's certificate under a connection whose
        // identity was checked once, and a BCL default is not something a test of this code can pin.
        AllowRenegotiation = false,
        RemoteCertificateValidationCallback = ValidatePeer,
    };

    private SslClientAuthenticationOptions ClientOptions(NinePAddress address) =>
        ClientAuthenticationOptions(address.Host);

    private SslClientAuthenticationOptions ClientOptionsFor(string targetHost) => new()
    {
        TargetHost = targetHost,
        ClientCertificates = _options.ClientCertificates,
#pragma warning disable CA5398 // See EnabledProtocols: the floor is this workspace's, not the platform's.
        EnabledSslProtocols = EnabledProtocols,
#pragma warning restore CA5398
        // See ServerOptions: off on both sides, and asserted by a test rather than assumed.
        AllowRenegotiation = false,
        RemoteCertificateValidationCallback = ValidatePeer,
    };

    private static string Unbracket(string host) => host.Trim('[', ']');

    private bool ValidatePeer(
        object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        // The one place verification can be turned off, and it is the option the connect path has
        // already logged a warning about. It is a *client* opt-out: it exists so a developer can
        // dial a server whose certificate is self-signed. Honouring it on the accepting side made
        // a listener with RequireClientCertificate accept any client certificate at all, which is
        // an authentication bypass reached by a flag whose name talks about the peer's
        // certificate on the other side of the connection.
        if (_options.AllowInsecureCertificates && sender is not SslStream { IsServer: true })
        {
            return true;
        }

        // A server that did not ask for a client certificate is not failed by its absence: the
        // platform still calls this back with a null certificate and RemoteCertificateNotAvailable.
        // A client that is offered no server certificate at all is always refused.
        if (certificate is null)
        {
            return sender is SslStream { IsServer: true } && !_options.RequireClientCertificate;
        }

        using X509Certificate2 peer = X509CertificateLoader2.FromCertificate(certificate);

        if (!IsChainAcceptable(peer, chain, errors, sender is SslStream { IsServer: true }))
        {
            return false;
        }

        // AdditionalPeerCheck runs only here, after the chain and the name have been accepted, so
        // it can refuse a certificate the platform trusted and can never rescue one it did not.
        return _options.AdditionalPeerCheck is null || _options.AdditionalPeerCheck(peer);
    }

    private bool IsChainAcceptable(X509Certificate2 peer, X509Chain? chain, SslPolicyErrors errors, bool isServer)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        // A name mismatch is never repairable by a custom root: the certificate is for another host.
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0 || _options.TrustedRoots is null)
        {
            return false;
        }

        using X509Chain custom = new();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        // Adding a trust anchor must not discard TLS purpose restrictions on the leaf or CAs.
        custom.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid(
            isServer ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1"));
        // A custom trust store is an explicit list of roots the caller pinned; it carries no CRL
        // or OCSP distribution point to check against, and the platform check has already run.
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        custom.ChainPolicy.CustomTrustStore.AddRange(_options.TrustedRoots);

        if (chain is not null)
        {
            foreach (X509ChainElement element in chain.ChainElements)
            {
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        return custom.Build(peer);
    }
}
