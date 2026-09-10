using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// Binding, counting and graceful shutdown (architecture §4): stop accepting, let what is in
/// flight finish inside the deadline, then close.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class ServerLifecycleTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>Every configured address is bound and published before serving begins.</summary>
    [Fact]
    public async Task EndpointsArePublishedOnceBound()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();

        Assert.Single(harness.Server.Endpoints);
        Assert.Equal(harness.Address, harness.Server.Endpoints[0]);
    }

    /// <summary>
    /// A graceful stop lets the session that is already running finish, then closes it. The
    /// deadline is what makes shutdown bounded: it returns whether or not the peer cooperates.
    /// </summary>
    [Fact]
    public async Task GracefulShutdownDrains()
    {
        ServerHarness harness = await ServerHarness.StartAsync();
        INinePConnection wire = await harness.DialAsync();

        await using (wire.ConfigureAwait(false))
        {
            // One connection is accepted and counted before the stop begins.
            await WaitForAcceptAsync(harness);
            Assert.Equal(1, harness.Server.Counters.ConnectionsOpen);

            await harness.Server.StopAsync(TimeSpan.FromSeconds(2), Ct);

            // Every session has been closed, and the peer sees the end of the stream.
            Assert.Equal(0, harness.Server.Counters.ConnectionsOpen);
            Assert.Equal(0, await wire.ReadAsync(new byte[1], Ct));
        }

        await harness.DisposeAsync();
    }

    /// <summary>Accepted, open and closed connections are counted (architecture §4).</summary>
    [Fact]
    public async Task ConnectionsAreCounted()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();

        INinePConnection first = await harness.DialAsync();
        await using (first.ConfigureAwait(false))
        {
            await WaitForAcceptAsync(harness);
            Assert.Equal(1, harness.Server.Counters.ConnectionsAccepted);
        }
    }

    /// <summary>
    /// Disposal completes even when the peer stopped reading. The reply path is bounded — a
    /// channel of <c>MaxInFlightPerConnection</c> in front of one writer — so a client that asks
    /// for more bytes than it collects fills the connection's send buffer and parks the writer
    /// inside <c>WriteAsync</c>. Disposal waits for that writer, and until this test existed it
    /// waited with no token at all: the server could not be stopped while such a peer was
    /// attached, and the process sat at 0% CPU for as long as anyone let it.
    /// <b>Mutation:</b> pass <see cref="CancellationToken.None"/> to the write loop's
    /// <c>WaitToReadAsync</c> and <c>WriteAsync</c> again and this test hangs rather than fails.
    /// </summary>
    [Fact]
    public async Task DisposalCompletesWhileThePeerHasStoppedReading()
    {
        MemoryFilesystem tree = new();
        MemoryFile big = tree.NewFile("big", 0x1A4);
        big.Data = new byte[64 * 1024];
        tree.Root.Add(big);

        // CA2000: the harness is disposed by the assertion below, which is the whole test.
#pragma warning disable CA2000
        ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
#pragma warning restore CA2000

        await using (client.ConfigureAwait(false))
        {
            await client.AttachAsync(1, Ct);
            await client.WalkAsync(2, 1, 2, ["big"], Ct);
            await client.SendAsync(new Tlopen(3, 2, 0), Ct);
            await client.ReceiveAsync<Rlopen>(Ct);

            // Far more bytes than the connection can buffer, and not one of them is read back.
            for (int i = 0; i < 100; i++)
            {
                await client.SendAsync(new Tread((ushort)(10 + i), 2, 0, 8000), Ct);
            }

            await WaitForRepliesAsync(harness);

            await harness.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
    }

    /// <summary>A server with no address to listen on is a configuration error, not a silent no-op.</summary>
    [Fact]
    public void NoListenAddressIsRefused()
    {
        ServerOptions options = new() { Listen = [] };

        Assert.Throws<ArgumentException>(() => new NinePServer(options));
    }

    /// <summary>A scheme no configured transport binds is refused when serving starts.</summary>
    [Fact]
    public async Task AnUnboundSchemeIsRefused()
    {
        ServerOptions options = new()
        {
            Listen = [new NinePAddress(NinePScheme.Memory, "nobody", 0, string.Empty)],
            Transports = [new TcpTransport()],
        };

        await using NinePServer server = new(options);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await server.ServeAsync(new TestSupport.MemoryFilesystem(), Ct));
    }

    /// <summary>Waits until the server has written all it can into a peer that is not reading.</summary>
    private static async Task WaitForRepliesAsync(ServerHarness harness)
    {
        long written = -1;

        for (int attempt = 0; attempt < 200; attempt++)
        {
            await Task.Delay(10, Ct);

            long now = harness.Server.Counters.BytesWritten;
            if (now > 0 && now == written)
            {
                return;
            }

            written = now;
        }
    }

    private static async Task WaitForAcceptAsync(ServerHarness harness)
    {
        for (int attempt = 0; attempt < 200 && harness.Server.Counters.ConnectionsAccepted == 0; attempt++)
        {
            await Task.Delay(10, TestDeadlines.Wrap(TestContext.Current.CancellationToken));
        }
    }
}
