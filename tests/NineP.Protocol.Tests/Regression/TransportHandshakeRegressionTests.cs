using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests.Regression;

/// <summary>TLS purpose constraints and independent bounded transport handshakes.</summary>
[Trait("Category", "Regression")]
public sealed class TransportHandshakeRegressionTests
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuth = "1.3.6.1.5.5.7.3.2";
    private const string CodeSigning = "1.3.6.1.5.5.7.3.3";
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(false, ServerAuth, true)]
    [InlineData(false, ClientAuth, false)]
    [InlineData(false, CodeSigning, false)]
    [InlineData(true, ClientAuth, true)]
    [InlineData(true, ServerAuth, false)]
    [InlineData(true, CodeSigning, false)]
    public void CustomRootPreservesPeerRoleOnCaIssuedLeaf(bool serverRole, string purpose, bool expected)
    {
        using X509Certificate2 root = RootCertificate();
        using X509Certificate2 leaf = Issue(root, "localhost", purpose, false);
        using X509Chain presented = PresentedChain(root, leaf);
        TlsTransport transport = new(new TlsTransportOptions { TrustedRoots = [root] });

        // The incoming chain has an otherwise untrusted root. Rebuilding it must enforce the
        // expected TLS role as well as signatures, for both accepting and dialing.
        MethodInfo method = typeof(TlsTransport).GetMethod("IsChainAcceptable", BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool actual = Assert.IsType<bool>(method.Invoke(transport,
            [leaf, presented, SslPolicyErrors.RemoteCertificateChainErrors, serverRole]));
        Assert.Equal(expected, actual);
        if (!serverRole)
        {
            RemoteCertificateValidationCallback callback = transport.ClientAuthenticationOptions("localhost").RemoteCertificateValidationCallback!;
            Assert.Equal(expected, callback(new object(), leaf, presented, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(callback(new object(), leaf, presented,
                SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntermediatePurposeRestrictionCannotBeRepairedByCustomTrust(bool serverRole)
    {
        using X509Certificate2 root = RootCertificate();
        using X509Certificate2 intermediate = Issue(root, "review intermediate", serverRole ? ServerAuth : ClientAuth, true);
        using X509Certificate2 leaf = Issue(intermediate, "localhost", serverRole ? ClientAuth : ServerAuth, false);
        using X509Chain presented = PresentedChain(root, leaf, intermediate);
        Assert.Equal(3, presented.ChainElements.Count);
        TlsTransport transport = new(new TlsTransportOptions { TrustedRoots = [root] });
        MethodInfo method = typeof(TlsTransport).GetMethod("IsChainAcceptable", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.False(Assert.IsType<bool>(method.Invoke(transport,
            [leaf, presented, SslPolicyErrors.RemoteCertificateChainErrors, serverRole])));
    }

    [Theory]
    [InlineData(NinePScheme.Tls, ServerAuth, true)]
    [InlineData(NinePScheme.Tls, ClientAuth, false)]
    [InlineData(NinePScheme.Tls, CodeSigning, false)]
    [InlineData(NinePScheme.Wss, ServerAuth, true)]
    [InlineData(NinePScheme.Wss, ClientAuth, false)]
    [InlineData(NinePScheme.Wss, CodeSigning, false)]
    public async Task ActualTlsAndWssHandshakesEnforceServerPurpose(NinePScheme scheme, string purpose, bool allowed)
    {
        using X509Certificate2 root = RootCertificate();
        using X509Certificate2 leaf = Issue(root, "localhost", purpose, false);
        (ITransport server, ITransport client) = Transports(scheme, leaf, root, 2, TimeSpan.FromSeconds(3));
        await using INinePListener listener = await server.ListenAsync(Address(scheme), Ct);
        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<INinePConnection?> accepting = listener.AcceptAsync(waiting.Token).AsTask();
        if (allowed)
        {
            await using INinePConnection connection = await client.ConnectAsync(listener.LocalAddress, Ct);
            await using INinePConnection accepted = Assert.IsAssignableFrom<INinePConnection>(await accepting.WaitAsync(TimeSpan.FromSeconds(3), Ct));
        }
        else
        {
            Exception failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await using INinePConnection connection = await client.ConnectAsync(listener.LocalAddress, Ct);
            });
            Assert.True(failure is AuthenticationException or WebSocketException or IOException, failure.ToString());
            await waiting.CancelAsync();
            try
            {
                // TLS 1.3 may complete the server's side before the client rejects its certificate.
                // The assertion is the dialing client's refusal; observe/dispose either server outcome.
                await using INinePConnection? accepted = await accepting;
            }
            catch (OperationCanceledException)
            {
                // No accepted connection was delivered before the explicit cancellation above.
            }
        }
    }

    [Theory]
    [InlineData(NinePScheme.Tls)]
    [InlineData(NinePScheme.Ws)]
    [InlineData(NinePScheme.Wss)]
    public async Task SilentPeerDoesNotBlockHealthyHandshakeAndPendingPlusActiveConnectionsStayCapped(NinePScheme scheme)
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("localhost");
        (ITransport server, ITransport client) = Transports(scheme, certificate, certificate, 2, TimeSpan.FromSeconds(10));
        await using INinePListener listener = await server.ListenAsync(Address(scheme), Ct);
        Task<INinePConnection?> accepting = listener.AcceptAsync(Ct).AsTask();
        await using Stream silent = await SilentPeerAsync(listener.LocalAddress, certificate);
        await using INinePConnection first = await client.ConnectAsync(listener.LocalAddress, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(3), Ct);
        await using INinePConnection accepted = Assert.IsAssignableFrom<INinePConnection>(await accepting.WaitAsync(TimeSpan.FromSeconds(3), Ct));

        // The silent handshake and handed-off connection fill both slots. The third peer stays
        // in the TCP backlog until a slot returns; the limit includes both stages.
        Task<INinePConnection> third = client.ConnectAsync(listener.LocalAddress, Ct).AsTask();
        Task<INinePConnection?> next = listener.AcceptAsync(Ct).AsTask();
        await Assert.ThrowsAsync<TimeoutException>(async () => await third.WaitAsync(TimeSpan.FromMilliseconds(150), Ct));
        Assert.False(next.IsCompleted);
        await accepted.DisposeAsync();
        await using INinePConnection thirdClient = await third.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        await using INinePConnection thirdServer = Assert.IsAssignableFrom<INinePConnection>(await next.WaitAsync(TimeSpan.FromSeconds(3), Ct));
    }

    [Theory]
    [InlineData(NinePScheme.Tls)]
    [InlineData(NinePScheme.Ws)]
    [InlineData(NinePScheme.Wss)]
    public async Task ExpiredHandshakeReturnsCapacityForWaitingHealthyPeer(NinePScheme scheme)
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("localhost");
        (ITransport server, ITransport client) = Transports(scheme, certificate, certificate, 1, TimeSpan.FromMilliseconds(400));
        await using INinePListener listener = await server.ListenAsync(Address(scheme), Ct);
        Task<INinePConnection?> accepting = listener.AcceptAsync(Ct).AsTask();
        await using Stream silent = await SilentPeerAsync(listener.LocalAddress, certificate);
        await using INinePConnection healthy = await client.ConnectAsync(listener.LocalAddress, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(3), Ct);
        await using INinePConnection accepted = Assert.IsAssignableFrom<INinePConnection>(await accepting.WaitAsync(TimeSpan.FromSeconds(3), Ct));
    }

    [Theory]
    [InlineData(NinePScheme.Tls)]
    [InlineData(NinePScheme.Ws)]
    [InlineData(NinePScheme.Wss)]
    public async Task CancellingOneAcceptPreservesHandshakeAndDisposalDrainsPendingAndQueuedConnections(NinePScheme scheme)
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("localhost");
        (ITransport server, ITransport client) = Transports(scheme, certificate, certificate, 2, TimeSpan.FromSeconds(10));
        await using INinePListener listener = await server.ListenAsync(Address(scheme), Ct);
        using CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<INinePConnection?> abandoned = listener.AcceptAsync(cancelled.Token).AsTask();
        await using Stream silent = await SilentPeerAsync(listener.LocalAddress, certificate);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned);
        await using INinePConnection healthy = await client.ConnectAsync(listener.LocalAddress, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(3), Ct);
        // A completed handshake is queued, without an awaiting consumer. Disposal must close it
        // along with the silent peer, without waiting for the ten-second handshake deadline.
        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), Ct);
        Assert.Null(await listener.AcceptAsync(Ct));
        Assert.Equal(0, await healthy.ReadAsync(new byte[1], Ct));
    }

    [Fact]
    public async Task DisposingTcpListenerWakesAcceptBlockedAtConnectionCap()
    {
        TcpTransport transport = new(new TcpTransportOptions { MaxConnections = 1 });
        await using INinePListener listener = await transport.ListenAsync(Address(NinePScheme.Tcp), Ct);
        await using INinePConnection client = await transport.ConnectAsync(listener.LocalAddress, Ct);
        await using INinePConnection accepted = Assert.IsAssignableFrom<INinePConnection>(await listener.AcceptAsync(Ct));
        Task<INinePConnection?> blocked = listener.AcceptAsync(CancellationToken.None).AsTask();
        Assert.False(blocked.IsCompleted);
        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), Ct);
        Assert.Null(await blocked.WaitAsync(TimeSpan.FromSeconds(3), Ct));
        // The connection outlives the listener; returning its slot afterwards must remain safe.
        await accepted.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentTcpListenerAndAcceptedConnectionDisposalDoesNotRaceSlotRelease()
    {
        for (int iteration = 0; iteration < 64; iteration++)
        {
            TcpTransport transport = new(new TcpTransportOptions { MaxConnections = 1 });
            await using INinePListener listener = await transport.ListenAsync(Address(NinePScheme.Tcp), Ct);
            await using INinePConnection client = await transport.ConnectAsync(listener.LocalAddress, Ct);
            await using INinePConnection accepted = Assert.IsAssignableFrom<INinePConnection>(await listener.AcceptAsync(Ct));
            using Barrier start = new(2);
            Task listenerClose = Task.Run(async () =>
            {
                start.SignalAndWait(Ct);
                await listener.DisposeAsync();
            }, Ct);
            Task connectionClose = Task.Run(async () =>
            {
                start.SignalAndWait(Ct);
                await accepted.DisposeAsync();
            }, Ct);
            await Task.WhenAll(listenerClose, connectionClose).WaitAsync(TimeSpan.FromSeconds(3), Ct);
        }
    }

    private static NinePAddress Address(NinePScheme scheme) => new(scheme, "localhost", 0, "/9p");

    private static (ITransport Server, ITransport Client) Transports(NinePScheme scheme,
        X509Certificate2 certificate, X509Certificate2 root, int capacity, TimeSpan handshakeTimeout)
    {
        TcpTransportOptions tcp = new() { MaxConnections = capacity };
        TlsTransportOptions serving = new() { ServerCertificate = certificate, Tcp = tcp, HandshakeTimeout = handshakeTimeout };
        TlsTransportOptions dialing = new() { TrustedRoots = [root] };
        return scheme == NinePScheme.Tls
            ? (new TlsTransport(serving), new TlsTransport(dialing))
            : (new WebSocketTransport(new WebSocketTransportOptions { Tls = serving, Tcp = tcp, HandshakeTimeout = handshakeTimeout }),
                new WebSocketTransport(new WebSocketTransportOptions { Tls = dialing }));
    }

    private static async Task<Stream> SilentPeerAsync(NinePAddress address, X509Certificate2 root)
    {
#pragma warning disable CA2000 // The returned NetworkStream owns the socket; failure disposes both below.
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
#pragma warning restore CA2000
        Stream? stream = null;
        try
        {
            await socket.ConnectAsync("localhost", address.Port, Ct);
            stream = new NetworkStream(socket, ownsSocket: true);
            if (address.Scheme == NinePScheme.Wss)
            {
                SslStream tls = new(stream, leaveInnerStreamOpen: false);
                stream = tls;
                TlsTransport trusted = new(new TlsTransportOptions { TrustedRoots = [root] });
                await tls.AuthenticateAsClientAsync(trusted.ClientAuthenticationOptions("localhost"), Ct);
            }

            if (address.Scheme != NinePScheme.Tls)
            {
                await stream.WriteAsync("GET /9p HTTP/1.1\r\nHost: localhost\r\n"u8.ToArray(), Ct);
            }

            return stream;
        }
        catch
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }

            socket.Dispose();
            throw;
        }
    }

    private static X509Chain PresentedChain(X509Certificate2 root, X509Certificate2 leaf, X509Certificate2? intermediate = null)
    {
        X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (intermediate is not null)
        {
            chain.ChainPolicy.ExtraStore.Add(intermediate);
        }

        Assert.True(chain.Build(leaf));
        return chain;
    }

    private static X509Certificate2 RootCertificate()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = Request("review root", key, null, true);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return Portable(certificate);
    }

    private static X509Certificate2 Issue(X509Certificate2 issuer, string name, string purpose, bool ca)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = Request(name, key, purpose, ca);
        using X509Certificate2 issued = request.Create(issuer, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(ca ? 60 : 30), RandomNumberGenerator.GetBytes(16));
        using X509Certificate2 certificate = issued.CopyWithPrivateKey(key);
        return Portable(certificate);
    }

    private static CertificateRequest Request(string name, RSA key, string? purpose, bool ca)
    {
        CertificateRequest request = new("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(ca
            ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign
            : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        if (purpose is not null)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(purpose)], true));
        }

        if (!ca)
        {
            SubjectAlternativeNameBuilder names = new();
            names.AddDnsName(name);
            request.CertificateExtensions.Add(names.Build());
        }

        return request;
    }

    private static X509Certificate2 Portable(X509Certificate2 certificate) =>
#if NET9_0_OR_GREATER
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
#else
        new(certificate.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
#endif
}
