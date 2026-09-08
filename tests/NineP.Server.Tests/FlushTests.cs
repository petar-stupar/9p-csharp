using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests;

/// <summary>
/// flush(5) and §6.6, driven with a handler the test holds open: the compare-and-swap that lets
/// exactly one of "answer it" and "suppress it" win, the <c>Rflush</c> that is sent in every case,
/// and arrival order between several flushes of one tag.
/// </summary>
public sealed class FlushTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// §6.6 rule 2: a reply must never appear after its own <c>Rflush</c>. The handler is held in
    /// flight, flushed, and then released; the only frame that ever arrives is the
    /// <c>Rflush</c>.
    /// <b>Mutation:</b> removing the CAS from <c>PendingRequest.TryComplete</c> — making it always
    /// return true — lets the released handler answer and this test fails.
    /// </summary>
    [Fact]
    public async Task NoReplyAfterRflush()
    {
        (ServerHarness harness, MemoryFile gated) = await GatedAsync();
        await using (harness.ConfigureAwait(false))
        {
            await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
            uint fid = await OpenGatedAsync(client);

            await client.SendAsync(new Tread(7, fid, 0, 16), Ct);
            await WaitForReadAsync(gated, 1);

            await client.SendAsync(new Tflush(8, 7), Ct);
            Rflush flushed = await client.ReceiveAsync<Rflush>(Ct);
            Assert.Equal(8, flushed.Tag);

            // The handler finishes only now; its reply is the one that must never be sent.
            gated.ReadGate!.SetResult();

            await client.SendAsync(new Tclunk(9, fid), Ct);
            byte[] next = await client.ReceiveFrameAsync(Ct);

            Assert.Equal(MessageType.Rclunk, MessageCodec.PeekType(next));
            Assert.Equal(9, MessageCodec.PeekTag(next));
        }
    }

    /// <summary>
    /// flush(5): <c>Rflush</c> is the answer in every case — an unknown tag, an already answered
    /// tag, and a <c>Tflush</c> that flushes another <c>Tflush</c>. It is never an error.
    /// </summary>
    [Fact]
    public async Task RflushAlwaysSent()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
        await client.AttachAsync(1, Ct);

        // An unknown tag.
        await client.SendAsync(new Tflush(2, 4242), Ct);
        Assert.Equal(2, (await client.ReceiveAsync<Rflush>(Ct)).Tag);

        // A tag that was answered long ago.
        await client.SendAsync(new Tclunk(3, 1), Ct);
        await client.ReceiveAsync<Rclunk>(Ct);
        await client.SendAsync(new Tflush(4, 3), Ct);
        Assert.Equal(4, (await client.ReceiveAsync<Rflush>(Ct)).Tag);

        // A Tflush flushing a Tflush.
        await client.SendAsync(new Tflush(5, 4), Ct);
        Assert.Equal(5, (await client.ReceiveAsync<Rflush>(Ct)).Tag);
    }

    /// <summary>
    /// flush(5): several flushes of one tag are answered in arrival order, and answering the last
    /// implies all the earlier ones.
    /// </summary>
    [Fact]
    public async Task MultipleFlushesAnsweredInOrder()
    {
        (ServerHarness harness, MemoryFile gated) = await GatedAsync();
        await using (harness.ConfigureAwait(false))
        {
            await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
            uint fid = await OpenGatedAsync(client);

            await client.SendAsync(new Tread(20, fid, 0, 16), Ct);
            await WaitForReadAsync(gated, 1);

            await client.SendAsync(new Tflush(21, 20), Ct);
            await client.SendAsync(new Tflush(22, 20), Ct);
            await client.SendAsync(new Tflush(23, 20), Ct);

            Assert.Equal(21, (await client.ReceiveAsync<Rflush>(Ct)).Tag);
            Assert.Equal(22, (await client.ReceiveAsync<Rflush>(Ct)).Tag);
            Assert.Equal(23, (await client.ReceiveAsync<Rflush>(Ct)).Tag);

            gated.ReadGate!.SetResult();
        }
    }

    private static async Task<(ServerHarness Harness, MemoryFile Gated)> GatedAsync(
        Func<ServerOptions, ServerOptions>? tune = null)
    {
        MemoryFilesystem tree = new();
        MemoryFile gated = tree.NewFile("slow", 0x1A4);
        gated.Data = "content"u8.ToArray();
        gated.ReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.Root.Add(gated);

        return (await ServerHarness.StartAsync(tune, tree), gated);
    }

    private static async Task<uint> OpenGatedAsync(WireClient client)
    {
        await client.AttachAsync(1, Ct);
        await client.WalkAsync(2, 1, 2, ["slow"], Ct);
        await client.SendAsync(new Tlopen(3, 2, 0), Ct);
        await client.ReceiveAsync<Rlopen>(Ct);

        return 2;
    }

    private static async Task WaitForReadAsync(MemoryFile file, int reads)
    {
        for (int attempt = 0; attempt < 500 && file.ReadsStarted < reads; attempt++)
        {
            await Task.Delay(10, TestDeadlines.Wrap(TestContext.Current.CancellationToken));
        }

        Assert.True(file.ReadsStarted >= reads, "the handler never reached the gate");
    }
}
