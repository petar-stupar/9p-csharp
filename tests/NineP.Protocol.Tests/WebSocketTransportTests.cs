using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using NineP.Protocol.Codec;
using NineP.Protocol.Internal;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.Protocol.Transports.Internal;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>
/// The WebSocket transport of §5.5 and S-1: one 9P message per binary message, a size cap enforced
/// while a message is still arriving, a text message refused, and an origin allow-list checked
/// before the upgrade completes.
/// </summary>
public sealed class WebSocketTransportTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// The positive handshake, over TLS and with a real <see cref="ClientWebSocket"/>: the
    /// subprotocol is negotiated and a Tversion frame comes back byte-identical (E-3 as a test).
    /// </summary>
    [Fact]
    public async Task ClientWebSocketHandshakeSucceedsOverTestCertificate()
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("localhost");
        WebSocketTransport transport = new(new WebSocketTransportOptions
        {
            Tls = new TlsTransportOptions
            {
                ServerCertificate = certificate,
                TrustedRoots = CertificateFactory.AsCollection(certificate),
            },
        });

        await using INinePListener listener =
            await transport.ListenAsync(NinePAddress.Parse("wss://localhost:0/9p"), Ct);
        Task<INinePConnection?> accepting = AcceptOneAsync(listener);

        await using INinePConnection client = await transport.ConnectAsync(listener.LocalAddress, Ct);
        INinePConnection? server = await accepting;
        Assert.NotNull(server);

        await using (server)
        {
            Assert.Equal(Constants.WebSocketSubprotocol, ((WebSocketConnection)client).Subprotocol);
            Assert.Equal(Constants.WebSocketSubprotocol, ((WebSocketConnection)server).Subprotocol);
            Assert.NotNull(server.PeerIdentity);
            Assert.Equal(NinePScheme.Wss, listener.LocalAddress.Scheme);

            byte[] frame = Frame(new Tversion(Constants.NOTAG, 8192, Constants.Version9P2000L));
            await client.WriteAsync(frame, Ct);

            byte[] received = await ReadExactlyAsync(server, frame.Length, Ct);

            Assert.Equal(frame, received);
        }
    }

    /// <summary>A plain ws:// endpoint round-trips a frame in both directions.</summary>
    [Fact]
    public async Task PlainWebSocketRoundTripsFrames()
    {
        WebSocketTransport transport = new();
        await using INinePListener listener =
            await transport.ListenAsync(NinePAddress.Parse("ws://localhost:0/9p"), Ct);
        Task<INinePConnection?> accepting = AcceptOneAsync(listener);

        await using INinePConnection client = await transport.ConnectAsync(listener.LocalAddress, Ct);
        INinePConnection? server = await accepting;
        Assert.NotNull(server);

        await using (server)
        {
            byte[] reply = Frame(new Rversion(Constants.NOTAG, 8192, Constants.Version9P2000));

            await server.WriteAsync(reply, Ct);

            Assert.Equal(reply, await ReadExactlyAsync(client, reply.Length, Ct));
        }
    }

    /// <summary>
    /// A message split across continuation frames whose running total passes the cap closes 1009
    /// while it is still arriving — the last fragment never needs to be buffered.
    /// </summary>
    [Fact]
    public async Task FragmentedOversizeCloses1009()
    {
        WebSocketTransport transport = new(new WebSocketTransportOptions { MaxMessageSize = 4096 });
        await using INinePListener listener =
            await transport.ListenAsync(NinePAddress.Parse("ws://localhost:0/9p"), Ct);
        Task<INinePConnection?> accepting = AcceptOneAsync(listener);

        using ClientWebSocket client = await RawClientAsync(listener.LocalAddress, origin: null);
        INinePConnection? server = await accepting;
        Assert.NotNull(server);

        Task<int> reading = ReadOnceAsync(server);

        byte[] fragment = new byte[2048];
        for (int i = 0; i < 4; i++)
        {
            await client.SendAsync(fragment, WebSocketMessageType.Binary, endOfMessage: false, Ct);
        }

        await Assert.ThrowsAsync<NinePProtocolException>(async () => await reading);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, await CloseStatusAsync(client));
    }

    /// <summary>A fragmented message under the cap is reassembled and delivered whole.</summary>
    [Fact]
    public async Task FragmentedUnderCapAccepted()
    {
        WebSocketTransport transport = new(new WebSocketTransportOptions { MaxMessageSize = 65536 });
        await using INinePListener listener =
            await transport.ListenAsync(NinePAddress.Parse("ws://localhost:0/9p"), Ct);
        Task<INinePConnection?> accepting = AcceptOneAsync(listener);

        using ClientWebSocket client = await RawClientAsync(listener.LocalAddress, origin: null);
        INinePConnection? server = await accepting;
        Assert.NotNull(server);

        await using (server)
        {
            byte[] frame = Frame(new Tversion(Constants.NOTAG, 8192, Constants.Version9P2000L));

            for (int i = 0; i < frame.Length; i++)
            {
                await client.SendAsync(
                    frame.AsMemory(i, 1), WebSocketMessageType.Binary, i == frame.Length - 1, Ct);
            }

            Assert.Equal(frame, await ReadExactlyAsync(server, frame.Length, Ct));
        }
    }

    /// <summary>A text message is a protocol error: 9P is binary, and the socket closes 1002.</summary>
    [Fact]
    public async Task TextFrameCloses()
    {
        WebSocketTransport transport = new();
        await using INinePListener listener =
            await transport.ListenAsync(NinePAddress.Parse("ws://localhost:0/9p"), Ct);
        Task<INinePConnection?> accepting = AcceptOneAsync(listener);

        using ClientWebSocket client = await RawClientAsync(listener.LocalAddress, origin: null);
        INinePConnection? server = await accepting;
        Assert.NotNull(server);

        Task<int> reading = ReadOnceAsync(server);
        await client.SendAsync(
            Encoding.ASCII.GetBytes("Tversion"), WebSocketMessageType.Text, endOfMessage: true, Ct);

        await Assert.ThrowsAsync<NinePProtocolException>(async () => await reading);
        Assert.Equal(WebSocketCloseStatus.ProtocolError, await CloseStatusAsync(client));
    }

    /// <summary>An Origin outside the allow-list is refused before the upgrade completes.</summary>
    [Fact]
    public async Task DisallowedOriginRefused()
    {
        RecordingLogger logger = new();
        WebSocketTransport transport = new(new WebSocketTransportOptions
        {
            AllowedOrigins = ["https://good.example"],
            Logger = logger,
        });

        await using INinePListener listener =
            await transport.ListenAsync(NinePAddress.Parse("ws://localhost:0/9p"), Ct);
        Task<INinePConnection?> accepting = AcceptOneAsync(listener);

        await Assert.ThrowsAsync<WebSocketException>(
            async () => await RawClientAsync(listener.LocalAddress, "https://evil.example"));

        Assert.Contains(
            logger.Records,
            record => record.Message.Contains("evil.example", StringComparison.Ordinal));
        Assert.False(accepting.IsCompleted);
    }

    /// <summary>An allowed Origin is accepted, and it reaches the server as the peer identity.</summary>
    [Fact]
    public async Task AllowedOriginIsAcceptedAndExposed()
    {
        WebSocketTransport transport = new(new WebSocketTransportOptions
        {
            AllowedOrigins = ["https://good.example"],
        });

        await using INinePListener listener =
            await transport.ListenAsync(NinePAddress.Parse("ws://localhost:0/9p"), Ct);
        Task<INinePConnection?> accepting = AcceptOneAsync(listener);

        using ClientWebSocket client = await RawClientAsync(listener.LocalAddress, "https://good.example");
        INinePConnection? server = await accepting;
        Assert.NotNull(server);

        await using (server)
        {
            Assert.NotNull(server.PeerIdentity);
            Assert.Equal("https://good.example", server.PeerIdentity.Origin);
            Assert.Equal("https://good.example", server.PeerIdentity.Headers["origin"]);
        }
    }

    /// <summary>A request with no Origin is refused once an allow-list has been configured.</summary>
    [Fact]
    public async Task AMissingOriginIsRefusedWhenAnAllowListExists()
    {
        WebSocketTransport transport = new(new WebSocketTransportOptions
        {
            AllowedOrigins = ["https://good.example"],
        });

        await using INinePListener listener =
            await transport.ListenAsync(NinePAddress.Parse("ws://localhost:0/9p"), Ct);
        Task<INinePConnection?> accepting = AcceptOneAsync(listener);

        await Assert.ThrowsAsync<WebSocketException>(
            async () => await RawClientAsync(listener.LocalAddress, origin: null));

        Assert.False(accepting.IsCompleted);
    }

    /// <summary>An empty allow-list accepts any Origin and says so at listen time.</summary>
    [Fact]
    public async Task AnEmptyAllowListLogsAWarningAtListenTime()
    {
        RecordingLogger logger = new();
        WebSocketTransport transport = new(new WebSocketTransportOptions { Logger = logger });

        await using INinePListener listener =
            await transport.ListenAsync(NinePAddress.Parse("ws://localhost:0/9p"), Ct);

        (LogLevel level, string message) = Assert.Single(logger.Records);

        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("any Origin", message, StringComparison.Ordinal);
    }

    /// <summary>Something that is not an RFC 6455 upgrade gets an HTTP refusal, not a 9P session.</summary>
    [Fact]
    public async Task APlainHttpRequestIsRefused()
    {
        WebSocketTransport transport = new();
        await using INinePListener listener =
            await transport.ListenAsync(NinePAddress.Parse("ws://localhost:0/9p"), Ct);
        Task<INinePConnection?> accepting = AcceptOneAsync(listener);

        using Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync("localhost", listener.LocalAddress.Port, Ct);
        await socket.SendAsync(Encoding.ASCII.GetBytes("GET /9p HTTP/1.1\r\nHost: x\r\n\r\n"), Ct);

        byte[] response = new byte[64];
        int read = await socket.ReceiveAsync(response, Ct);

        Assert.Contains("400", Encoding.ASCII.GetString(response, 0, read), StringComparison.Ordinal);
        Assert.False(accepting.IsCompleted);
    }

    /// <summary>The accept token is the RFC 6455 §1.3 example, computed the RFC's way.</summary>
    [Fact]
    public void TheAcceptTokenFollowsTheRfc()
    {
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", WebSocketHandshake.AcceptFor("dGhlIHNhbXBsZSBub25jZQ=="));
        Assert.Equal("258EAFA5-E914-47DA-95CA-C5AB0DC85B11", WebSocketHandshake.AcceptGuid);
    }

    /// <summary>A key that is not sixteen base64 bytes is not a key.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("dGhlIHNhbXBsZSBub25jZQ==", true)]
    [InlineData("c2hvcnQ=", false)]
    [InlineData("not base64 at all", false)]
    public void OnlyASixteenByteKeyIsLegal(string? key, bool expected) =>
        Assert.Equal(expected, WebSocketHandshake.IsLegalKey(key));

    /// <summary>Each close reason travels as the RFC 6455 status code that means it.</summary>
    [Theory]
    [InlineData(CloseReason.MessageTooLarge, WebSocketCloseStatus.MessageTooBig)]
    [InlineData(CloseReason.ProtocolViolation, WebSocketCloseStatus.ProtocolError)]
    [InlineData(CloseReason.ResourceLimit, WebSocketCloseStatus.PolicyViolation)]
    [InlineData(CloseReason.Shutdown, WebSocketCloseStatus.EndpointUnavailable)]
    [InlineData(CloseReason.Timeout, WebSocketCloseStatus.EndpointUnavailable)]
    [InlineData(CloseReason.TransportError, WebSocketCloseStatus.InternalServerError)]
    [InlineData(CloseReason.Normal, WebSocketCloseStatus.NormalClosure)]
    [InlineData(CloseReason.PeerClosed, WebSocketCloseStatus.NormalClosure)]
    public void CloseReasonsMapToStatusCodes(CloseReason reason, WebSocketCloseStatus expected) =>
        Assert.Equal(expected, WebSocketConnection.StatusFor(reason));

    /// <summary>Only ws:// and wss:// reach this transport, and wss needs a certificate.</summary>
    [Fact]
    public async Task OnlyWebSocketAddressesAreAccepted()
    {
        WebSocketTransport transport = new();
        NinePAddress tcp = NinePAddress.Parse("tcp://127.0.0.1:1");

        Assert.Equal([NinePScheme.Ws, NinePScheme.Wss], transport.Schemes);
        await Assert.ThrowsAsync<ArgumentException>(async () => await transport.ConnectAsync(tcp, Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await transport.ListenAsync(tcp, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transport.ListenAsync(NinePAddress.Parse("wss://localhost:0/9p"), Ct));
    }

    /// <summary>
    /// E-4, recorded rather than remembered: on macOS an <c>HttpListener</c> with an https prefix
    /// does start, and a TLS handshake against it is still reset by the peer — which is why S-2
    /// builds the wss listener on a raw socket instead.
    /// </summary>
    [Fact]
    public async Task HttpListenerHttpsIsUnusableHere()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("E-4 was measured on macOS; HttpListener's https prefix behaves differently elsewhere.");
        }

        int port = FreePort();
        using HttpListener listener = new();
        listener.Prefixes.Add(string.Format(CultureInfo.InvariantCulture, "https://127.0.0.1:{0}/", port));

        try
        {
            listener.Start();
        }
        catch (HttpListenerException)
        {
            // Even less usable than measured: the prefix does not start at all here.
            return;
        }

        Assert.True(listener.IsListening);

        using Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync("127.0.0.1", port, Ct);
        await using NetworkStream network = new(socket, ownsSocket: false);

        // CA5359: nothing is trusted here; the point is that the handshake never gets that far.
#pragma warning disable CA5359
        await using SslStream ssl = new(network, leaveInnerStreamOpen: false, (_, _, _, _) => true);
#pragma warning restore CA5359

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = "127.0.0.1" }, Ct));

        listener.Stop();
    }

    private static int FreePort()
    {
        using Socket probe = new(SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static byte[] Frame<TMessage>(TMessage message)
        where TMessage : struct, IMessage
    {
        ArrayBufferWriter<byte> writer = new();
        MessageCodec.Encode(writer, in message, Dialect.P9_2000_L);
        return writer.WrittenSpan.ToArray();
    }

    private static async Task<ClientWebSocket> RawClientAsync(NinePAddress address, string? origin)
    {
        ClientWebSocket client = new();
        try
        {
            client.Options.AddSubProtocol(Constants.WebSocketSubprotocol);
            if (origin is not null)
            {
                client.Options.SetRequestHeader("Origin", origin);
            }

            Uri uri = new(string.Format(
                CultureInfo.InvariantCulture, "ws://localhost:{0}{1}", address.Port, address.Path));

            await client.ConnectAsync(uri, CancellationToken.None);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<WebSocketCloseStatus?> CloseStatusAsync(ClientWebSocket client)
    {
        byte[] scratch = new byte[256];
        while (client.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            WebSocketReceiveResult result = await client.ReceiveAsync(scratch, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }
        }

        return client.CloseStatus;
    }

    private static Task<int> ReadOnceAsync(INinePConnection connection) =>
        Task.Run(async () => await connection.ReadAsync(new byte[64], CancellationToken.None));

    private static Task<INinePConnection?> AcceptOneAsync(INinePListener listener) =>
        Task.Run(async () =>
        {
            try
            {
                return await listener.AcceptAsync(CancellationToken.None);
            }
            catch (Exception failure) when (failure is ObjectDisposedException or SocketException
                or WebSocketException or IOException or OperationCanceledException)
            {
                return null;
            }
        });

    private static async Task<byte[]> ReadExactlyAsync(
        INinePConnection connection, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int filled = 0;
        while (filled < count)
        {
            int read = await connection.ReadAsync(buffer.AsMemory(filled), cancellationToken);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return buffer.AsSpan(0, filled).ToArray();
    }

}
