using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests;

/// <summary>
/// Reference §8 rule 8 and §6.8: two in-flight bounds and a reserve. Beyond a bound the offending
/// connection receives EAGAIN for excess ordinary requests; the reader remains available for
/// Tflush, so a client that filled its window can still cancel pending work.
/// </summary>
public sealed class BackpressureTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 49: <c>Tflush</c> keeps being read and answered while the general window is full.
    /// <b>Mutation:</b> drawing every request from one semaphore instead of two makes the
    /// <c>Tflush</c> below wait behind the requests it is meant to cancel, and this test fails on
    /// its own ten-second timeout instead of passing.
    /// </summary>
    [Fact]
    public async Task FlushAnsweredWhileWindowFull()
    {
        (ServerHarness harness, MemoryFile gated) = await GatedAsync(general: 2, listener: 8);
        await using (harness.ConfigureAwait(false))
        {
            await using WireClient client = await OpenTwoAsync(harness);

            // Two fids, because operations on one fid are serialised by design; filling the
            // window needs two handlers running at once.
            await client.SendAsync(new Tread(10, 2, 0, 16), Ct);
            await client.SendAsync(new Tread(11, 3, 0, 16), Ct);
            await WaitForReadsAsync(gated, 2);

            // The general budget is spent: both slots are held by handlers that will not return
            // until the gate opens. The flush draws from the reserve instead.
            await client.SendAsync(new Tflush(13, 10), Ct);
            Rflush answered = await client.ReceiveAsync<Rflush>(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(13, answered.Tag);

            gated.ReadGate!.SetResult();
        }
    }

    /// <summary>
    /// Rule 50: listener saturation refuses ordinary work with EAGAIN but still allows negotiation
    /// and flush; clients can retry the refused request after capacity becomes available.
    /// </summary>
    [Fact]
    public async Task ListenerWideBoundHolds()
    {
        (ServerHarness harness, MemoryFile gated) = await GatedAsync(general: 1, listener: 2);
        await using (harness.ConfigureAwait(false))
        {
            await using WireClient first = await OpenOneAsync(harness);
            await using WireClient second = await OpenOneAsync(harness);

            await first.SendAsync(new Tread(70, 2, 0, 16), Ct);
            await second.SendAsync(new Tread(71, 2, 0, 16), Ct);
            await WaitForReadsAsync(gated, 2);

            // Every listener slot is held. A third connection still negotiates and is still
            // answered on the reserve, so the bound stopped requests and not the session.
            await using WireClient third = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
            await third.SendAsync(new Tflush(80, 1), Ct);
            Assert.Equal(80, (await third.ReceiveAsync<Rflush>(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct)).Tag);

            await third.SendAsync(new Tattach(81, 1, Constants.NOFID, "glenda", "", Constants.NONUNAME), Ct);
            Rlerror refusal = await third.ReceiveAsync<Rlerror>(Ct);
            Assert.Equal(Errno.EAGAIN, refusal.Ecode);
            Assert.Equal(81, refusal.Tag);
            await third.SendAsync(new Tflush(82, 81), Ct);
            Assert.Equal(82, (await third.ReceiveAsync<Rflush>(Ct)).Tag);
            gated.ReadGate!.SetResult();
            await first.ReceiveAsync<Rread>(Ct);
            await second.ReceiveAsync<Rread>(Ct);
            Qid root = await third.AttachAsync(1, Ct);
            Assert.Equal(QidType.QTDIR, root.Type);
        }
    }

    /// <summary>
    /// §6.8: backpressure is per connection. A connection parked on a full window does not stop
    /// another connection being served — that is the whole reason the bound is per connection and
    /// not a global lock.
    /// </summary>
    [Fact]
    public async Task OneConnectionCannotStallAnother()
    {
        (ServerHarness harness, MemoryFile gated) = await GatedAsync(general: 1, listener: 8);
        await using (harness.ConfigureAwait(false))
        {
            await using WireClient stalled = await OpenTwoAsync(harness);

            await stalled.SendAsync(new Tread(60, 2, 0, 16), Ct);
            await WaitForReadsAsync(gated, 1);
            await stalled.SendAsync(new Tread(61, 3, 0, 16), Ct);

            await using WireClient healthy = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
            Qid root = await healthy.AttachAsync(1, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(QidType.QTDIR, root.Type);

            await healthy.SendAsync(new Tclunk(2, 1), Ct);
            byte[] reply = await healthy.ReceiveFrameAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(MessageType.Rclunk, MessageCodec.PeekType(reply));

            gated.ReadGate!.SetResult();
        }
    }

    private static async Task<(ServerHarness Harness, MemoryFile Gated)> GatedAsync(
        int general, int listener)
    {
        MemoryFilesystem tree = new();
        MemoryFile gated = tree.NewFile("slow", 0x1A4);
        gated.Data = "content"u8.ToArray();
        gated.ReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.Root.Add(gated);

        // The reserve is what a Tflush draws from, so the general window is the difference.
        const int reserve = 1;
        ServerHarness harness = await ServerHarness.StartAsync(
            options => options with
            {
                Limits = Limits.Default with
                {
                    MaxInFlightPerConnection = general + reserve,
                    FlushReservePerConnection = reserve,
                    MaxInFlightPerListener = listener,
                },
            },
            tree);

        return (harness, gated);
    }

    private static async Task<WireClient> OpenOneAsync(ServerHarness harness)
    {
        WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
        await client.AttachAsync(1, Ct);
        await OpenGatedAsync(client, 2);
        return client;
    }

    private static async Task<WireClient> OpenTwoAsync(ServerHarness harness)
    {
        WireClient client = await OpenOneAsync(harness);
        await OpenGatedAsync(client, 3);
        return client;
    }

    private static async Task OpenGatedAsync(WireClient client, uint fid)
    {
        await client.WalkAsync((ushort)fid, 1, fid, ["slow"], Ct);
        await client.SendAsync(new Tlopen((ushort)(fid + 100), fid, 0), Ct);
        await client.ReceiveAsync<Rlopen>(Ct);
    }

    private static async Task WaitForReadsAsync(MemoryFile file, int reads)
    {
        for (int attempt = 0; attempt < 500 && file.ReadsStarted < reads; attempt++)
        {
            await Task.Delay(10, TestDeadlines.Wrap(TestContext.Current.CancellationToken));
        }

        Assert.True(file.ReadsStarted >= reads, "the handlers never reached the gate");
    }
}
