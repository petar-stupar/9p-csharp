using System.Buffers;
using System.Net.Sockets;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using NineP.Protocol.Transports;
using NineP.Protocol.Transports.Internal;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

/// <summary>The TCP transport of §5.5: Nagle off, keep-alive on, and a cap that stops accepting.</summary>
[Trait("Category", "Conformance")]
public sealed class TcpTransportTests
{
    /// <summary>The runner's token, so a hung socket test is cancelled rather than waited on.</summary>
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>Loopback, port 0: the kernel picks a port nothing else on the machine holds.</summary>
    private static NinePAddress Loopback => NinePAddress.Parse("tcp://127.0.0.1:0");

    /// <summary>
    /// The ticket's pitfall: Nagle must be off. The assertion is on the socket the listener
    /// accepted, not on the option that was passed in — an option nobody applies is not a setting.
    /// </summary>
    [Fact]
    public async Task NoDelayIsSet()
    {
        TcpTransport transport = new();
        await using INinePListener listener = await transport.ListenAsync(Loopback, Ct);

        ValueTask<INinePConnection?> accepting = listener.AcceptAsync(Ct);
        await using INinePConnection client = await transport.ConnectAsync(listener.LocalAddress, Ct);
        INinePConnection? server = await accepting;

        Assert.NotNull(server);
        await using (server)
        {
            Socket accepted = ((TcpTransportConnection)server).Socket;

            Assert.True(accepted.NoDelay);
            Assert.NotEqual(0, (int)accepted.GetSocketOption(
                SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
            Assert.True(((TcpTransportConnection)client).Socket.NoDelay);
        }
    }

    /// <summary>Both directions of a real socket carry a 9P frame byte-identically.</summary>
    [Fact]
    public async Task RoundTripsFrames()
    {
        TcpTransport transport = new();
        await using INinePListener listener = await transport.ListenAsync(Loopback, Ct);

        ValueTask<INinePConnection?> accepting = listener.AcceptAsync(Ct);
        await using INinePConnection client = await transport.ConnectAsync(listener.LocalAddress, Ct);
        INinePConnection? server = await accepting;
        Assert.NotNull(server);

        await using (server)
        {
            byte[] request = Frame(new Tversion(Constants.NOTAG, 8192, Constants.Version9P2000L));
            byte[] reply = Frame(new Rversion(Constants.NOTAG, 8192, Constants.Version9P2000L));

            await client.WriteAsync(request, Ct);
            Assert.Equal(request, await ReadExactlyAsync(server, request.Length, Ct));

            await server.WriteAsync(reply, Ct);
            Assert.Equal(reply, await ReadExactlyAsync(client, reply.Length, Ct));
        }
    }

    /// <summary>
    /// At the cap the listener stops accepting: the second dial is left in the kernel's backlog,
    /// not accepted and reset, and it is served the moment a slot frees.
    /// </summary>
    [Fact]
    public async Task ConnectionCapRefuses()
    {
        TcpTransport transport = new(new TcpTransportOptions { MaxConnections = 1 });
        await using INinePListener listener = await transport.ListenAsync(Loopback, Ct);

        INinePConnection first = await transport.ConnectAsync(listener.LocalAddress, Ct);
        INinePConnection? firstServer = await listener.AcceptAsync(Ct);
        Assert.NotNull(firstServer);

        await using INinePConnection second = await transport.ConnectAsync(listener.LocalAddress, Ct);
        ValueTask<INinePConnection?> blocked = listener.AcceptAsync(Ct);

        Assert.Equal(1, ((TcpTransportListener)listener).HeldConnections);
        Assert.False(blocked.IsCompleted);

        // The slot belongs to the accepted connection, so freeing it is what lets the waiting
        // connection through; the dialer's own end holds nothing of the listener's.
        await first.DisposeAsync();
        Assert.Equal(1, ((TcpTransportListener)listener).HeldConnections);
        Assert.False(blocked.IsCompleted);

        await firstServer.DisposeAsync();
        INinePConnection? secondServer = await blocked;

        Assert.NotNull(secondServer);
        await secondServer.DisposeAsync();
    }

    /// <summary>The bound address carries the port the kernel chose, not the 0 that was asked for.</summary>
    [Fact]
    public async Task TheBoundAddressCarriesTheRealPort()
    {
        TcpTransport transport = new();
        await using INinePListener listener = await transport.ListenAsync(Loopback, Ct);

        Assert.NotEqual(0, listener.LocalAddress.Port);
        Assert.Equal(NinePScheme.Tcp, listener.LocalAddress.Scheme);
        Assert.Equal("127.0.0.1", listener.LocalAddress.Host);
    }

    /// <summary>An accepted connection knows the peer's address, which is what the log records.</summary>
    [Fact]
    public async Task TheAcceptedConnectionKnowsThePeer()
    {
        TcpTransport transport = new();
        await using INinePListener listener = await transport.ListenAsync(Loopback, Ct);

        ValueTask<INinePConnection?> accepting = listener.AcceptAsync(Ct);
        await using INinePConnection client = await transport.ConnectAsync(listener.LocalAddress, Ct);
        INinePConnection? server = await accepting;
        Assert.NotNull(server);

        await using (server)
        {
            Assert.Equal(NinePScheme.Tcp, server.RemoteAddress.Scheme);
            Assert.Equal("127.0.0.1", server.RemoteAddress.Host);
            Assert.NotEqual(0, server.RemoteAddress.Port);
            Assert.NotNull(server.PeerIdentity);
            Assert.NotNull(server.PeerIdentity.RemoteAddress);
            Assert.Null(server.PeerIdentity.ClientCertificate);
        }
    }

    /// <summary>Closing one end is an end of stream at the other, and the reason is recorded here.</summary>
    [Fact]
    public async Task ClosingIsAnEndOfStreamAtThePeer()
    {
        TcpTransport transport = new();
        await using INinePListener listener = await transport.ListenAsync(Loopback, Ct);

        ValueTask<INinePConnection?> accepting = listener.AcceptAsync(Ct);
        INinePConnection client = await transport.ConnectAsync(listener.LocalAddress, Ct);
        INinePConnection? server = await accepting;
        Assert.NotNull(server);

        await using (server)
        {
            await client.CloseAsync(CloseReason.ProtocolViolation, Ct);

            Assert.Equal(CloseReason.ProtocolViolation, ((TcpTransportConnection)client).LocalCloseReason);
            Assert.Equal(0, await server.ReadAsync(new byte[16], Ct));
        }
    }

    /// <summary>A disposed listener stops accepting and answers null rather than hanging.</summary>
    [Fact]
    public async Task ADisposedListenerAcceptsNull()
    {
        TcpTransport transport = new();
        INinePListener listener = await transport.ListenAsync(Loopback, Ct);
        ValueTask<INinePConnection?> accepting = listener.AcceptAsync(Ct);

        await listener.DisposeAsync();

        Assert.Null(await accepting);
    }

    /// <summary>Only tcp:// addresses reach this transport; anything else is a caller's bug.</summary>
    [Fact]
    public async Task OnlyTcpAddressesAreAccepted()
    {
        TcpTransport transport = new();
        NinePAddress memory = NinePAddress.Parse("memory://alpha");

        Assert.Equal([NinePScheme.Tcp], transport.Schemes);
        await Assert.ThrowsAsync<ArgumentException>(async () => await transport.ConnectAsync(memory, Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await transport.ListenAsync(memory, Ct));
    }

    /// <summary>A bound that cannot be honoured is refused at construction, not at first use.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void ImpossibleBoundsAreRefused(int backlog, int maxConnections) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TcpTransport(new TcpTransportOptions { Backlog = backlog, MaxConnections = maxConnections }));

    private static byte[] Frame<TMessage>(TMessage message)
        where TMessage : struct, IMessage
    {
        ArrayBufferWriter<byte> writer = new();
        MessageCodec.Encode(writer, in message, Dialect.P9_2000_L);
        return writer.WrittenSpan.ToArray();
    }

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
