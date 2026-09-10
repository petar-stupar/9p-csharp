using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using NineP.Protocol.Transports;
using NineP.Protocol.Transports.Internal;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

/// <summary>
/// The TLS transport of §5.5. Every certificate is generated in this process and thrown away with
/// it; nothing is read from disk and nothing is committed (E-3).
/// </summary>
[Trait("Category", "Conformance")]
public sealed class TlsTransportTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    private static NinePAddress Loopback => NinePAddress.Parse("tls://localhost:0");




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
