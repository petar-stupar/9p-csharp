using System.Buffers;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using NineP.Protocol.Transports;
using NineP.Protocol.Transports.Internal;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

/// <summary>The in-process transport of S-31: the seam every test that can use it does.</summary>
[Trait("Category", "Conformance")]
public sealed class MemoryTransportTests
{
    private static readonly NinePAddress Alpha = NinePAddress.Parse("memory://alpha");

    /// <summary>The runner's token, so a hung transport test is cancelled rather than waited on.</summary>
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>A frame written at one end arrives byte-identically at the other.</summary>
    [Fact]
    public async Task RoundTripsFrames()
    {
        (INinePConnection client, INinePConnection server) = MemoryTransport.CreatePair();
        await using (client)
        await using (server)
        {
            byte[] frame = Frame(new Tversion(Constants.NOTAG, 8192, Constants.Version9P2000L));

            await client.WriteAsync(frame, Ct);
            byte[] received = await ReadExactlyAsync(server, frame.Length, Ct);

            Assert.Equal(frame, received);
            Assert.Equal(Constants.Version9P2000L, MessageCodec.Decode<Tversion>(received, Dialect.P9_2000_L).Version);
        }
    }

    /// <summary>A listener hands a dialer's connection to whoever is accepting.</summary>
    [Fact]
    public async Task ListenAcceptAndDialMeet()
    {
        MemoryTransport transport = new();
        await using INinePListener listener = await transport.ListenAsync(Alpha, Ct);

        ValueTask<INinePConnection?> accepting = listener.AcceptAsync(Ct);
        await using INinePConnection client = await transport.ConnectAsync(Alpha, Ct);
        INinePConnection? server = await accepting;

        Assert.NotNull(server);
        await using (server)
        {
            Assert.Equal(Alpha, listener.LocalAddress);
            Assert.Equal(Alpha, client.RemoteAddress);
            Assert.Null(client.PeerIdentity);
        }
    }

    /// <summary>The close reason one end gives is the reason the other end reads.</summary>
    [Theory]
    [InlineData(CloseReason.MessageTooLarge)]
    [InlineData(CloseReason.ProtocolViolation)]
    [InlineData(CloseReason.Shutdown)]
    [InlineData(CloseReason.ResourceLimit)]
    public async Task CloseReasonReachesThePeer(CloseReason reason)
    {
        (INinePConnection client, INinePConnection server) = MemoryTransport.CreatePair();
        await using (client)
        await using (server)
        {
            await server.CloseAsync(reason, Ct);

            byte[] buffer = new byte[16];
            Assert.Equal(0, await client.ReadAsync(buffer, Ct));
            Assert.Equal(reason, ((MemoryConnection)client).PeerCloseReason);
            Assert.Null(((MemoryConnection)client).LocalCloseReason);
        }
    }

    /// <summary>Disposing an open connection closes it normally, and twice is not an error.</summary>
    [Fact]
    public async Task DisposeIsANormalCloseAndIsIdempotent()
    {
        (INinePConnection client, INinePConnection server) = MemoryTransport.CreatePair();

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.Equal(CloseReason.Normal, ((MemoryConnection)server).PeerCloseReason);
        await server.DisposeAsync();
    }

    /// <summary>The first reason wins: a close does not get rewritten by a later dispose.</summary>
    [Fact]
    public async Task TheFirstCloseReasonWins()
    {
        (INinePConnection client, INinePConnection server) = MemoryTransport.CreatePair();
        await using (server)
        {
            await client.CloseAsync(CloseReason.Timeout, Ct);
            await client.DisposeAsync();

            Assert.Equal(CloseReason.Timeout, ((MemoryConnection)server).PeerCloseReason);
        }
    }

    /// <summary>Writing after a close is a caller's bug, not a silent discard.</summary>
    [Fact]
    public async Task WritingAfterCloseThrows()
    {
        (INinePConnection client, INinePConnection server) = MemoryTransport.CreatePair();
        await using (server)
        {
            await client.CloseAsync(CloseReason.Normal, Ct);

            await Assert.ThrowsAsync<ObjectDisposedException>(
                async () => await client.WriteAsync(new byte[] { 1, 2, 3 }, Ct));
        }
    }

    /// <summary>A disposed listener stops accepting and returns null rather than hanging.</summary>
    [Fact]
    public async Task ADisposedListenerAcceptsNull()
    {
        MemoryTransport transport = new();
        INinePListener listener = await transport.ListenAsync(Alpha, Ct);

        await listener.DisposeAsync();

        Assert.Null(await listener.AcceptAsync(Ct));
    }

    /// <summary>Dialling a name nothing is bound to fails rather than hanging forever.</summary>
    [Fact]
    public async Task DiallingAnUnboundNameFails()
    {
        MemoryTransport transport = new();

        NinePException failure = await Assert.ThrowsAsync<NinePException>(
            async () => await transport.ConnectAsync(Alpha, Ct));

        Assert.Contains("memory://alpha", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Two transports are isolated: a name bound in one is not reachable from the other.</summary>
    [Fact]
    public async Task TransportsAreIsolatedFromEachOther()
    {
        MemoryTransport first = new();
        MemoryTransport second = new();
        await using INinePListener listener = await first.ListenAsync(Alpha, Ct);

        await Assert.ThrowsAsync<NinePException>(async () => await second.ConnectAsync(Alpha, Ct));
    }

    /// <summary>One name binds once; a second bind on the same transport is a configuration error.</summary>
    [Fact]
    public async Task ANameBindsOnce()
    {
        MemoryTransport transport = new();
        await using INinePListener listener = await transport.ListenAsync(Alpha, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.ListenAsync(Alpha, Ct));
    }

    /// <summary>Disposing a listener unbinds its name, so the same name can be bound again.</summary>
    [Fact]
    public async Task DisposingAListenerUnbindsTheName()
    {
        MemoryTransport transport = new();
        INinePListener first = await transport.ListenAsync(Alpha, Ct);
        await first.DisposeAsync();

        await using INinePListener second = await transport.ListenAsync(Alpha, Ct);

        Assert.Equal(Alpha, second.LocalAddress);
    }

    /// <summary>Only memory:// addresses reach this transport; anything else is a caller's bug.</summary>
    [Fact]
    public async Task OnlyMemoryAddressesAreAccepted()
    {
        MemoryTransport transport = new();
        NinePAddress tcp = NinePAddress.Parse("tcp://127.0.0.1:564");

        Assert.Equal([NinePScheme.Memory], transport.Schemes);
        await Assert.ThrowsAsync<ArgumentException>(async () => await transport.ConnectAsync(tcp, Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await transport.ListenAsync(tcp, Ct));
    }

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
