using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using NineP.Protocol.Transports;
using NineP.Protocol.Transports.Internal;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>
/// The TLS transport of §5.5. Every certificate is generated in this process and thrown away with
/// it; nothing is read from disk and nothing is committed (E-3).
/// </summary>
public sealed class TlsTransportTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    private static NinePAddress Loopback => NinePAddress.Parse("tls://localhost:0");

    /// <summary>
    /// Verification is on by default, so the untrusted self-signed certificate a test generates is
    /// refused; the test-only trust hook — an explicit list of extra roots — is what accepts it.
    /// </summary>
    [Fact]
    public async Task UntrustedSelfSignedRejectedByDefaultAndAcceptedByTheTrustHook()
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("localhost");
        TlsTransport server = new(new TlsTransportOptions { ServerCertificate = certificate });
        await using INinePListener listener = await server.ListenAsync(Loopback, Ct);
        _ = AcceptOneAsync(listener);

        TlsTransport strict = new(new TlsTransportOptions());
        await Assert.ThrowsAsync<AuthenticationException>(
            async () => await strict.ConnectAsync(listener.LocalAddress, Ct));

        _ = AcceptOneAsync(listener);
        TlsTransport trusting = new(new TlsTransportOptions
        {
            TrustedRoots = CertificateFactory.AsCollection(certificate),
        });

        await using INinePConnection connection = await trusting.ConnectAsync(listener.LocalAddress, Ct);

        Assert.Equal(NinePScheme.Tls, connection.RemoteAddress.Scheme);
    }

    /// <summary>A certificate for another host is refused even when its chain is trusted.</summary>
    [Fact]
    public async Task HostnameMismatchRejectedByDefault()
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("other.invalid");
        TlsTransport server = new(new TlsTransportOptions { ServerCertificate = certificate });
        await using INinePListener listener = await server.ListenAsync(Loopback, Ct);
        _ = AcceptOneAsync(listener);

        TlsTransport client = new(new TlsTransportOptions
        {
            TrustedRoots = CertificateFactory.AsCollection(certificate),
        });

        await Assert.ThrowsAsync<AuthenticationException>(
            async () => await client.ConnectAsync(listener.LocalAddress, Ct));
    }

    /// <summary>The floor is TLS 1.2: a client that offers only 1.1 does not get a session.</summary>
    [Fact]
    public async Task Tls11Refused()
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("localhost");
        TlsTransport server = new(new TlsTransportOptions { ServerCertificate = certificate });
        await using INinePListener listener = await server.ListenAsync(Loopback, Ct);
        _ = AcceptOneAsync(listener);

        using Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync("localhost", listener.LocalAddress.Port, Ct);
        await using NetworkStream network = new(socket, ownsSocket: false);
        // CA5359: this raw client never reaches the handshake — refusing TLS 1.1 is the assertion.
        // Accepting any certificate here is what makes the failure attributable to the version.
#pragma warning disable CA5359
        await using SslStream ssl = new(network, leaveInnerStreamOpen: false, (_, _, _, _) => true);
#pragma warning restore CA5359

        // SYSLIB0039 / CA5397: naming TLS 1.1 is the point of this test — it is the version the
        // transport must refuse. Nothing in src/ ever names it.
#pragma warning disable SYSLIB0039, CA5397
        SslClientAuthenticationOptions options = new()
        {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls11,
        };
#pragma warning restore SYSLIB0039, CA5397

        await Assert.ThrowsAnyAsync<Exception>(async () => await ssl.AuthenticateAsClientAsync(options, Ct));
    }

    /// <summary>A session that does come up negotiated 1.2 or better.</summary>
    [Fact]
    public async Task NegotiatedProtocolIsAtLeastTls12()
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("localhost");
        TlsTransport server = new(new TlsTransportOptions { ServerCertificate = certificate });
        await using INinePListener listener = await server.ListenAsync(Loopback, Ct);
        Task<INinePConnection?> accepting = AcceptOneAsync(listener);

        TlsTransport client = new(new TlsTransportOptions
        {
            TrustedRoots = CertificateFactory.AsCollection(certificate),
        });
        await using INinePConnection connection = await client.ConnectAsync(listener.LocalAddress, Ct);
        INinePConnection? accepted = await accepting;

        Assert.NotNull(accepted);
        await using (accepted)
        {
            SslProtocols negotiated = ((TlsTransportConnection)connection).NegotiatedProtocol;

            // CA5398: naming the two acceptable versions is the assertion of this test.
#pragma warning disable CA5398
            Assert.True(
                negotiated is SslProtocols.Tls12 or SslProtocols.Tls13,
#pragma warning restore CA5398
                string.Format(CultureInfo.InvariantCulture, "negotiated {0}", negotiated));
        }
    }

    /// <summary>With mutual TLS the client's certificate reaches the server as its peer identity.</summary>
    [Fact]
    public async Task MutualTlsIdentityExposed()
    {
        using X509Certificate2 serverCertificate = CertificateFactory.CreateSelfSigned("localhost");
        using X509Certificate2 clientCertificate = CertificateFactory.CreateSelfSigned("client.invalid");

        TlsTransport server = new(new TlsTransportOptions
        {
            ServerCertificate = serverCertificate,
            RequireClientCertificate = true,
            TrustedRoots = CertificateFactory.AsCollection(clientCertificate),
        });
        await using INinePListener listener = await server.ListenAsync(Loopback, Ct);
        Task<INinePConnection?> accepting = AcceptOneAsync(listener);

        TlsTransport client = new(new TlsTransportOptions
        {
            TrustedRoots = CertificateFactory.AsCollection(serverCertificate),
            ClientCertificates = CertificateFactory.AsCollection(clientCertificate),
        });
        await using INinePConnection connection = await client.ConnectAsync(listener.LocalAddress, Ct);
        INinePConnection? accepted = await accepting;

        Assert.NotNull(accepted);
        await using (accepted)
        {
            Assert.NotNull(accepted.PeerIdentity);
            Assert.NotNull(accepted.PeerIdentity.ClientCertificate);
            Assert.Contains(
                "client.invalid", accepted.PeerIdentity.ClientCertificate.Subject, StringComparison.Ordinal);
        }
    }

    /// <summary>A clean close sends close_notify, so the peer reads an end of stream, not a reset.</summary>
    [Fact]
    public async Task CleanShutdownSendsCloseNotify()
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("localhost");
        TlsTransport server = new(new TlsTransportOptions { ServerCertificate = certificate });
        await using INinePListener listener = await server.ListenAsync(Loopback, Ct);
        Task<INinePConnection?> accepting = AcceptOneAsync(listener);

        TlsTransport client = new(new TlsTransportOptions
        {
            TrustedRoots = CertificateFactory.AsCollection(certificate),
        });
        INinePConnection connection = await client.ConnectAsync(listener.LocalAddress, Ct);
        INinePConnection? accepted = await accepting;
        Assert.NotNull(accepted);

        await using (accepted)
        {
            await connection.CloseAsync(CloseReason.Shutdown, Ct);

            Assert.Equal(CloseReason.Shutdown, ((TlsTransportConnection)connection).LocalCloseReason);
            Assert.Equal(0, await accepted.ReadAsync(new byte[16], Ct));
        }
    }

    /// <summary>The insecure opt-out logs a warning on every connect; it is never silent.</summary>
    [Fact]
    public async Task InsecureOptOutLogs()
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("other.invalid");
        TlsTransport server = new(new TlsTransportOptions { ServerCertificate = certificate });
        await using INinePListener listener = await server.ListenAsync(Loopback, Ct);
        Task<INinePConnection?> accepting = AcceptOneAsync(listener);

        RecordingLogger logger = new();
        TlsTransport client = new(new TlsTransportOptions
        {
            AllowInsecureCertificates = true,
            Logger = logger,
        });

        await using INinePConnection connection = await client.ConnectAsync(listener.LocalAddress, Ct);
        INinePConnection? accepted = await accepting;

        Assert.NotNull(accepted);
        await using (accepted)
        {
            (LogLevel level, string message) = Assert.Single(logger.Records);

            Assert.Equal(LogLevel.Warning, level);
            Assert.Contains("DISABLED", message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The insecure opt-out is client-side. Honouring it on the accepting side made a listener
    /// with <c>RequireClientCertificate</c> accept any client certificate at all — an
    /// authentication bypass reached through a flag that talks about the certificate on the other
    /// side of the connection — and it was never even logged there.
    /// </summary>
    [Fact]
    public async Task TheInsecureOptOutDoesNotReachTheListener()
    {
        using X509Certificate2 serverCertificate = CertificateFactory.CreateSelfSigned("localhost");
        using X509Certificate2 clientCertificate = CertificateFactory.CreateSelfSigned("client.invalid");

        RecordingLogger logger = new();
        TlsTransport server = new(new TlsTransportOptions
        {
            ServerCertificate = serverCertificate,
            RequireClientCertificate = true,

            // Trusts nothing: the client certificate below is signed by nobody this server knows.
            AllowInsecureCertificates = true,
            Logger = logger,
        });

        await using INinePListener listener = await server.ListenAsync(Loopback, Ct);

        // The listener says so rather than leaving it to be discovered.
        (LogLevel level, string message) = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("ignored when listening", message, StringComparison.Ordinal);

        Task<INinePConnection?> accepting = AcceptOneAsync(listener);

        TlsTransport client = new(new TlsTransportOptions
        {
            TrustedRoots = CertificateFactory.AsCollection(serverCertificate),
            ClientCertificates = CertificateFactory.AsCollection(clientCertificate),
        });

        try
        {
            // Under TLS 1.3 the client finishes its side before the server has judged the
            // certificate, so the connect may return and the refusal show up afterwards; either
            // way what matters is that the server never hands a connection out.
            await using INinePConnection connection = await client.ConnectAsync(listener.LocalAddress, Ct);
        }
        catch (Exception failure) when (failure is AuthenticationException or IOException)
        {
        }

        Task finished = await Task.WhenAny(accepting, Task.Delay(TimeSpan.FromSeconds(3), Ct));

        Assert.NotSame(accepting, finished);
    }

    /// <summary>AdditionalPeerCheck may only add a check: it cannot rescue a bad certificate.</summary>
    [Fact]
    public async Task AdditionalPeerCheckCannotRescueABadCertificate()
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("localhost");
        TlsTransport server = new(new TlsTransportOptions { ServerCertificate = certificate });
        await using INinePListener listener = await server.ListenAsync(Loopback, Ct);
        _ = AcceptOneAsync(listener);

        TlsTransport client = new(new TlsTransportOptions { AdditionalPeerCheck = _ => true });

        await Assert.ThrowsAsync<AuthenticationException>(
            async () => await client.ConnectAsync(listener.LocalAddress, Ct));
    }

    /// <summary>AdditionalPeerCheck can refuse a certificate the chain accepted.</summary>
    [Fact]
    public async Task AdditionalPeerCheckCanRefuseATrustedCertificate()
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("localhost");
        TlsTransport server = new(new TlsTransportOptions { ServerCertificate = certificate });
        await using INinePListener listener = await server.ListenAsync(Loopback, Ct);
        _ = AcceptOneAsync(listener);

        TlsTransport client = new(new TlsTransportOptions
        {
            TrustedRoots = CertificateFactory.AsCollection(certificate),
            AdditionalPeerCheck = _ => false,
        });

        await Assert.ThrowsAsync<AuthenticationException>(
            async () => await client.ConnectAsync(listener.LocalAddress, Ct));
    }

    /// <summary>
    /// S-2: renegotiation is off on both sides because this library says so, not because the
    /// platform happens to default that way. <c>docs/security.md</c> §4 makes the claim; this is
    /// what makes it true. <b>Mutation:</b> drop <c>AllowRenegotiation = false</c> from the client
    /// option builder in <c>TlsTransport</c> and the client assertion below fails, because the BCL
    /// default there is <c>true</c>. The same mutation on the server builder <b>survives</b> on
    /// .NET 8 and 10, whose <c>SslServerAuthenticationOptions</c> already defaults to
    /// <c>false</c>; the server assertion pins this code against that default ever changing, which
    /// is the whole of what it can do.
    /// </summary>
    [Fact]
    public void RenegotiationIsOffOnBothSides()
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("localhost");
        TlsTransport transport = new(new TlsTransportOptions { ServerCertificate = certificate });

        SslServerAuthenticationOptions server = transport.ServerAuthenticationOptions();
        SslClientAuthenticationOptions client = transport.ClientAuthenticationOptions("localhost");

        Assert.False(server.AllowRenegotiation);
        Assert.False(client.AllowRenegotiation);
        Assert.Equal(TlsTransport.EnabledProtocols, server.EnabledSslProtocols);
        Assert.Equal(TlsTransport.EnabledProtocols, client.EnabledSslProtocols);
    }

    /// <summary>A listener without a certificate is a configuration error, caught at listen time.</summary>
    [Fact]
    public async Task ListeningWithoutACertificateIsRefused()
    {
        TlsTransport transport = new(new TlsTransportOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transport.ListenAsync(Loopback, Ct));
    }

    /// <summary>Only tls:// addresses reach this transport, and options are required.</summary>
    [Fact]
    public async Task OnlyTlsAddressesAreAccepted()
    {
        TlsTransport transport = new(new TlsTransportOptions());
        NinePAddress tcp = NinePAddress.Parse("tcp://127.0.0.1:1");

        Assert.Equal([NinePScheme.Tls], transport.Schemes);
        Assert.Throws<ArgumentNullException>(() => new TlsTransport(null!));
        await Assert.ThrowsAsync<ArgumentException>(async () => await transport.ConnectAsync(tcp, Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await transport.ListenAsync(tcp, Ct));
    }

    private static Task<INinePConnection?> AcceptOneAsync(INinePListener listener) =>
        Task.Run(async () =>
        {
            try
            {
                return await listener.AcceptAsync(CancellationToken.None);
            }
            catch (Exception failure) when (failure is ObjectDisposedException or SocketException
                or AuthenticationException or IOException or OperationCanceledException)
            {
                return null;
            }
        });

}
