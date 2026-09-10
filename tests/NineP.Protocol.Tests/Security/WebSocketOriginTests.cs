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
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using NineP.Protocol.Transports;
using NineP.Protocol.Transports.Internal;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests.Security;

/// <summary>
/// The WebSocket transport of §5.5 and S-1: one 9P message per binary message, a size cap enforced
/// while a message is still arriving, a text message refused, and an origin allow-list checked
/// before the upgrade completes.
/// </summary>
[Trait("Category", "Security")]
public sealed class WebSocketOriginTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);






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
