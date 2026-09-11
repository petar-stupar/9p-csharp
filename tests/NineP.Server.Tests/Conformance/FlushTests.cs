using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// flush(5) and §6.6, driven with a handler the test holds open: the compare-and-swap that lets
/// exactly one of "answer it" and "suppress it" win, the <c>Rflush</c> that is sent in every case,
/// and arrival order between several flushes of one tag.
/// </summary>
[Trait("Category", "Conformance")]
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

    /// <summary>
    /// Rule 27 (§5.3): a flushed request is answered exactly once. Either its own reply went out
    /// before the flush, in which case the <c>Rflush</c> simply follows it, or it never goes out
    /// at all — never both.
    /// </summary>
    [Fact]
    public async Task FlushedRequestAnsweredOnce()
    {
        (ServerHarness harness, MemoryFile gated) = await GatedAsync();
        await using (harness.ConfigureAwait(false))
        {
            await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);
            uint fid = await OpenGatedAsync(client);

            await client.SendAsync(new Tread(30, fid, 0, 16), Ct);
            await WaitForReadAsync(gated, 1);
            await client.SendAsync(new Tflush(31, 30), Ct);

            byte[] first = await client.ReceiveFrameAsync(Ct);
            gated.ReadGate!.SetResult();

            // Whatever came first, the flushed tag is answered no more than once in total.
            List<ushort> answered = [MessageCodec.PeekTag(first)];
            await client.SendAsync(new Tclunk(32, fid), Ct);

            while (true)
            {
                byte[] frame = await client.ReceiveFrameAsync(Ct);
                ushort tag = MessageCodec.PeekTag(frame);
                if (tag == 32)
                {
                    break;
                }

                answered.Add(tag);
            }

            Assert.Equal(answered.Count, answered.Distinct().Count());
            Assert.Contains<ushort>(31, answered);
        }
    }

    /// <summary>
    /// Reference §5.3: "the client must wait until it gets the <c>Rflush</c>… at which point
    /// <c>oldtag</c> may be reused." Linux v9fs reuses it on the very next request, lowest number
    /// first, so the reuse below is what a real mount does after a Ctrl-C. It must be served, not
    /// refused as a duplicate — and the flushed request must still never produce a second answer
    /// for that tag once its handler unwinds.
    /// <para>
    /// The handler here waits on a gate that ignores its cancellation token, which is the whole
    /// point: it models a handler that has not yet noticed the flush. Freeing the tag only in the
    /// worker's <c>finally</c> left the tag held for exactly that window, and the reuse drew
    /// <c>Rlerror EINVAL</c> / <c>Rerror "duplicate tag"</c> and was dropped (§8 rule 6) — 32 of
    /// 104 legal reuses against the shipped jsonfs with a millisecond-scale handler.
    /// </para>
    /// <b>Mutation:</b> drop the <c>_pending.TryRemove</c> from <c>TagTable.Flush</c> and the
    /// reused tag below is answered with the dialect's error type instead of <c>Rwalk</c>.
    /// </summary>
    [Fact]
    public async Task ATagReusedTheInstantItsRflushArrivesIsServed()
    {
        MemoryFilesystem tree = new();
        MemoryFile stuck = tree.NewFile("stuck", Perms.P0644);
        stuck.Data = "content"u8.ToArray();
        stuck.DeafReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.Root.Add(stuck);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, 8192, Ct);

        await client.AttachAsync(1, Ct);
        await client.WalkAsync(2, 1, 2, ["stuck"], Ct);
        await client.SendAsync(new Tlopen(3, 2, 0), Ct);
        await client.ReceiveAsync<Rlopen>(Ct);

        // Tag 7 goes into a handler that will not return until this test says so.
        await client.SendAsync(new Tread(7, 2, 0, 16), Ct);
        await WaitForReadAsync(stuck, 1);

        await client.SendAsync(new Tflush(8, 7), Ct);
        Rflush confirmed = await client.ReceiveAsync<Rflush>(Ct);

        Assert.Equal((ushort)8, confirmed.Tag);

        // The Rflush is in hand, so tag 7 is the client's again -- while the flushed handler is
        // still sitting in the gate.
        await client.SendAsync(new Twalk(7, 1, 9, []), Ct);
        byte[] answer = await client.ReceiveFrameAsync(Ct);

        Assert.Equal((ushort)7, MessageCodec.PeekTag(answer));
        Assert.Equal(MessageType.Rwalk, MessageCodec.PeekType(answer));

        // The flushed handler now unwinds. §5.3 forbids any further answer under tag 7, and the
        // worker's backstop must not disturb the request that holds it now.
        stuck.DeafReadGate!.SetResult();
        await client.SendAsync(new Tclunk(10, 9), Ct);

        List<ushort> uninvited = [];
        while (true)
        {
            byte[] frame = await client.ReceiveFrameAsync(Ct);
            ushort tag = MessageCodec.PeekTag(frame);
            if (tag == 10)
            {
                break;
            }

            uninvited.Add(tag);
        }

        Assert.Empty(uninvited);
    }

    private static async Task<(ServerHarness Harness, MemoryFile Gated)> GatedAsync(
        Func<ServerOptions, ServerOptions>? tune = null)
    {
        MemoryFilesystem tree = new();
        MemoryFile gated = tree.NewFile("slow", Perms.P0644);
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
        while (file.ReadsStarted < reads)
        {
            await Task.Delay(10, TestDeadlines.Wrap(TestContext.Current.CancellationToken));
        }

        Assert.True(file.ReadsStarted >= reads, "the handler never reached the gate");
    }
}
