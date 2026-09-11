using System.Buffers.Binary;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.Protocol.Transports.Internal;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Security;

/// <summary>
/// AC-b and conformance Part D: a client that lies about a size, walks seventeen elements, floods
/// the fid table, floods the tag space, or begins a frame and goes silent must cost the server
/// nothing but that one connection. Every case here asserts the same two things — the offending
/// connection is closed or refused, and a <b>second</b> connection is answered throughout — because
/// a limit that also takes down the innocent traffic is not a limit, it is an outage.
/// </summary>
[Trait("Category", "Security")]
public sealed class HostileClientTests
{
    private const uint Msize = 8192;

    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 1 (reference §8): a frame claiming <c>0xFFFFFFFF</c> bytes is a size violation, the
    /// connection is closed with <see cref="CloseReason.MessageTooLarge"/>, and the connection
    /// beside it neither stalls nor dies.
    /// <b>Mutation:</b> compare <c>size</c> against <see cref="Limits.MaxMsize"/> instead of the
    /// active bound, or drop the comparison altogether, and this test fails.
    /// </summary>
    [Fact]
    public async Task SizeLieClosesOnlyThatConnection()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using WireClient healthy = await AttachedAsync(harness);

        await using (WireClient hostile = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, Msize, Ct))
        {
            byte[] lie = [0xFF, 0xFF, 0xFF, 0xFF, (byte)MessageType.Tstat, 0x01, 0x00];
            await hostile.SendRawAsync(lie, Ct);

            Assert.Equal(CloseReason.MessageTooLarge, await ClosedAsync(hostile));
            Assert.Empty(await hostile.ReceiveFrameAsync(Ct));
        }

        await AssertHealthyAsync(healthy);
    }

    /// <summary>
    /// Rule 1 again, before a dialect exists: the bound is the constant 8192 and not the configured
    /// maximum, so a frame the maximum would allow is refused pre-<c>Tversion</c>.
    /// <b>Mutation:</b> let <c>FrameReader.ActiveBound</c> fall back to <c>Limits.MaxMsize</c>
    /// before negotiation and the 9000-byte frame below is happily buffered.
    /// </summary>
    [Fact]
    public async Task PreVersionOversizeFrameClosesTheConnection()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using WireClient healthy = await AttachedAsync(harness);

        INinePConnection hostile = await harness.DialAsync();
        await using (hostile.ConfigureAwait(false))
        {
            const uint Claimed = 9000;
            Assert.True(Claimed > Limits.Default.PreNegotiationFrameCap);
            Assert.True(Claimed < Limits.Default.MaxMsize);

            byte[] frame = new byte[Constants.HDRSZ];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, Claimed);
            frame[4] = (byte)MessageType.Tversion;

            await hostile.WriteAsync(frame, Ct);
            Assert.Equal(CloseReason.MessageTooLarge, await ClosedAsync(hostile));
        }

        await AssertHealthyAsync(healthy);
    }

    /// <summary>
    /// Rule 2 and reference §8.3: <c>nwname</c> above <c>MAXWELEM</c> is a typed codec failure, so
    /// the walk is answered <c>EPROTO</c> and the connection closed — a stream whose counted field
    /// was a lie cannot be resynced.
    /// <b>Mutation:</b> remove the <c>nwname &gt; MAXWELEM</c> check in <c>WireReader</c> and the
    /// seventeenth element is read as if it were legal, so nothing here fails.
    /// </summary>
    [Fact]
    public async Task WalkOfSeventeenElementsIsRefused()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using WireClient healthy = await AttachedAsync(harness);

        await using (WireClient hostile = await AttachedAsync(harness))
        {
            await hostile.SendRawAsync(OverlongWalk(tag: 9, fid: 1, newFid: 2, elements: 17), Ct);

            byte[] reply = await hostile.ReceiveFrameAsync(Ct);
            Assert.Equal(MessageType.Rlerror, MessageCodec.PeekType(reply));
            Assert.Equal(Errno.EPROTO, MessageCodec.Decode<Rlerror>(reply, Dialect.P9_2000_L).Ecode);
            Assert.Equal(CloseReason.ProtocolViolation, await ClosedAsync(hostile));
        }

        await AssertHealthyAsync(healthy);
    }

    /// <summary>
    /// Rule 7 and §6.8: 70 000 fids on one connection meet the cap, not the machine's memory. The
    /// walks past the cap are answered <c>ENFILE</c> and the connection stays up, because a fid
    /// flood is a refusal and not a framing error.
    /// <b>Mutation:</b> delete the capacity check in <c>FidTable.Bind</c> and every one of the
    /// 70 000 walks succeeds.
    /// </summary>
    [Fact]
    public async Task FidFloodHitsCapNotMemory()
    {
        const int Cap = 4096;
        const int Attempts = 70_000;

        await using ServerHarness harness = await ServerHarness.StartAsync(
            options => options with { Limits = Limits.Default with { MaxFidsPerConnection = Cap } });
        await using WireClient healthy = await AttachedAsync(harness);

        await using (WireClient hostile = await AttachedAsync(harness))
        {
            (int bound, int refused) = await FloodFidsAsync(hostile, Attempts);

            // The attach fid is one of them, so the cap admits one fewer clone than its number.
            Assert.Equal(Cap - 1, bound);
            Assert.Equal(Attempts - bound, refused);

            // The connection is still a connection: a clunk frees a fid and the next walk fits.
            await hostile.SendAsync(new Tclunk(1, 2), Ct);
            Assert.Equal(MessageType.Rclunk, MessageCodec.PeekType(await hostile.ReceiveFrameAsync(Ct)));

            Rwalk again = await hostile.WalkAsync(2, 1, 2, [], Ct);
            Assert.Empty(again.Wqids);
        }

        await AssertHealthyAsync(healthy);
    }

    /// <summary>
    /// Rule 8 and §6.8: excess tags receive EAGAIN while the bounded admitted work remains
    /// pending. Every request receives exactly one reply, and another connection stays usable.
    /// </summary>
    [Fact]
    public async Task TagFloodBackpressures()
    {
        const int Window = 8;
        const int Reserve = 2;
        const int Tags = 300;
        const int Fids = 16;

        MemoryFilesystem tree = new();
        MemoryFile gated = tree.NewFile("slow", Perms.P0644);
        gated.Data = "content"u8.ToArray();
        gated.ReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.Root.Add(gated);

        // The healthy connection reads this one, so it must exist beside the gated file.
        MemoryFile greeting = tree.NewFile("hello.txt", Perms.P0644);
        greeting.Data = "hello, 9P\n"u8.ToArray();
        tree.Root.Add(greeting);

        await using ServerHarness harness = await ServerHarness.StartAsync(
            options => options with
            {
                Limits = Limits.Default with
                {
                    MaxInFlightPerConnection = Window + Reserve,
                    FlushReservePerConnection = Reserve,
                },
            },
            tree);
        await using WireClient healthy = await AttachedAsync(harness);

        await using (WireClient hostile = await AttachedAsync(harness))
        {
            for (uint fid = 2; fid < 2 + Fids; fid++)
            {
                await hostile.WalkAsync((ushort)fid, 1, fid, ["slow"], Ct);
                await hostile.SendAsync(new Tlopen((ushort)(fid + 500), fid, 0), Ct);
                await hostile.ReceiveAsync<Rlopen>(Ct);
            }

            for (int i = 0; i < Tags; i++)
            {
                await hostile.SendAsync(new Tread((ushort)(i + 1), (uint)(2 + (i % Fids)), 0, 4), Ct);
            }

            await WaitForReadsAsync(gated, Window);
            await AssertHealthyAsync(healthy);
            Assert.Equal(Window, gated.ReadsStarted);
            HashSet<ushort> answered = [];
            for (int i = 0; i < Tags - Window; i++)
            {
                Rlerror refused = await hostile.ReceiveAsync<Rlerror>(Ct);
                Assert.Equal(Errno.EAGAIN, refused.Ecode);
                Assert.True(answered.Add(refused.Tag));
            }

            gated.ReadGate.SetResult();
            for (int i = 0; i < Window; i++)
            {
                Rread reply = await hostile.ReceiveAsync<Rread>(Ct);
                Assert.Equal(4, reply.Data.Length);
                Assert.True(answered.Add(reply.Tag));
            }

            Assert.Equal(Tags, answered.Count);
        }

        await AssertHealthyAsync(healthy);
    }

    /// <summary>
    /// §6.8 and RK-62: half a header and then silence is a slowloris. The deadline runs from the
    /// first byte of the frame, so the connection is closed with <see cref="CloseReason.Timeout"/>
    /// while the connection beside it is still answered.
    /// <b>Mutation:</b> never arm <c>FrameReader</c>'s partial-frame deadline — the state this
    /// repository shipped before task 40 — and the hostile connection is held open for ever.
    /// </summary>
    [Fact]
    public async Task HalfHeaderTimesOut()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync(
            options => options with
            {
                Limits = Limits.Default with { ReadHeaderTimeout = TimeSpan.FromMilliseconds(500) },
            });
        await using WireClient healthy = await AttachedAsync(harness);

        INinePConnection hostile = await harness.DialAsync();
        await using (hostile.ConfigureAwait(false))
        {
            // Two bytes of a four-byte size field: a frame has begun and will never finish.
            await hostile.WriteAsync(new byte[] { 0x13, 0x00 }, Ct);

            Assert.Equal(CloseReason.Timeout, await ClosedAsync(hostile));
        }

        await AssertHealthyAsync(healthy);
    }

    /// <summary>
    /// Reference §8 rule 4: a <c>count</c> the client made up is clamped to what the negotiated
    /// msize can carry, not honoured and not answered with an oversize frame.
    /// <b>Mutation:</b> pass <c>Tread.Count</c> to the handler unclamped and the reply below is
    /// larger than the msize both sides agreed on.
    /// </summary>
    [Fact]
    public async Task OversizeReadCountIsClamped()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using WireClient healthy = await AttachedAsync(harness);

        await using (WireClient hostile = await AttachedAsync(harness))
        {
            await hostile.WalkAsync(2, 1, 2, ["hello.txt"], Ct);
            await hostile.SendAsync(new Tlopen(3, 2, 0), Ct);
            await hostile.ReceiveAsync<Rlopen>(Ct);

            await hostile.SendAsync(new Tread(4, 2, 0, uint.MaxValue), Ct);
            byte[] frame = await hostile.ReceiveFrameAsync(Ct);

            Assert.Equal(MessageType.Rread, MessageCodec.PeekType(frame));
            Assert.True((uint)frame.Length <= Msize, "the reply outgrew the negotiated msize");
            Assert.Equal("hello, 9P\n"u8.ToArray(), MessageCodec.Decode<Rread>(frame, Dialect.P9_2000_L).Data.ToArray());
        }

        await AssertHealthyAsync(healthy);
    }

    /// <summary>
    /// S-1: the msize clamp on its own. <see cref="OversizeReadCountIsClamped"/> reads a ten-byte
    /// file, and the read path also clamps the payload to what the file has left to give, so with
    /// the msize clamp deleted that test stayed green — the file-size clamp was doing its work for
    /// it. Here the file is three times the negotiated msize, so only the msize clamp can bound
    /// the reply, and the payload must be exactly <c>msize − IOHDRSZ</c>: the first slice of the
    /// file, not the whole of it and not an error.
    /// <b>Mutation:</b> drop <c>Math.Min(request.Count, MaxPayload)</c> from the read path in
    /// <c>Dispatcher</c> and the payload below is the whole file, or nothing.
    /// </summary>
    [Fact]
    public async Task OversizeReadCountOnAFileLargerThanMsizeIsClampedToMsize()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();

        byte[] contents = new byte[3 * Msize];
        for (int i = 0; i < contents.Length; i++)
        {
            contents[i] = (byte)i;
        }

        MemoryFile large = harness.Tree.NewFile("large.bin", Perms.P0644);
        large.Data = contents;
        harness.Tree.Root.Add(large);

        await using WireClient healthy = await AttachedAsync(harness);

        await using (WireClient hostile = await AttachedAsync(harness))
        {
            await hostile.WalkAsync(2, 1, 2, ["large.bin"], Ct);
            await hostile.SendAsync(new Tlopen(3, 2, 0), Ct);
            await hostile.ReceiveAsync<Rlopen>(Ct);

            await hostile.SendAsync(new Tread(4, 2, 0, uint.MaxValue), Ct);
            byte[] frame = await hostile.ReceiveFrameAsync(Ct);

            Assert.Equal(MessageType.Rread, MessageCodec.PeekType(frame));
            Assert.True((uint)frame.Length <= Msize, "the reply outgrew the negotiated msize");

            int expected = (int)Msize - Constants.IOHDRSZ;
            ReadOnlyMemory<byte> payload = MessageCodec.Decode<Rread>(frame, Dialect.P9_2000_L).Data;
            Assert.Equal(expected, payload.Length);
            Assert.Equal(contents.AsSpan(0, expected).ToArray(), payload.ToArray());
        }

        await AssertHealthyAsync(healthy);
    }

    /// <summary>AC-b and rule-index row 61: allocations remain bounded by msize, not the claimed size.</summary>
    [Fact]
    public async Task AllocationStaysWithinMsize() => await AssertAllocationBoundedAsync();

    private static async Task AssertAllocationBoundedAsync()
    {
        const int Cycles = 32;

        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using WireClient healthy = await AttachedAsync(harness);

        // Warm both paths before either is measured: the first connection of a run pays for the
        // JIT and for the pools behind it, and that is not what a connection costs.
        await CycleAsync(harness, lie: false);
        await CycleAsync(harness, lie: true);

        // GC.GetTotalAllocatedBytes is process-wide, and the rest of this assembly is running in
        // parallel, so a single window measures the machine. The two paths are therefore measured
        // alternately — which shares the ambient noise between them — and what is compared is the
        // quietest cycle of each, which is the one whose window nothing else landed in.
        long legal = long.MaxValue;
        long hostile = long.MaxValue;

        for (int i = 0; i < Cycles; i++)
        {
            legal = Math.Min(legal, await MeasureAsync(harness, lie: false));
            hostile = Math.Min(hostile, await MeasureAsync(harness, lie: true));
        }

        // The claim is 4 GiB and the msize is 8 KiB: if the size field decided anything at all
        // about what the server reserves, this ratio would be half a million rather than one.
        Assert.True(
            hostile < (legal * 2) + Msize,
            $"a 4 GiB size lie cost {hostile} bytes against {legal} for an ordinary frame");

        // And the absolute claim of AC-b: what one connection costs is a small multiple of the
        // msize it negotiated, not of the size it asked for. Measured on an M4 Pro at 0.1.0:
        // about 23 KiB for a lying connection and about 22 KiB for an ordinary one.
        const long BudgetPerConnection = 64 * Msize;
        Assert.True(
            hostile < BudgetPerConnection,
            $"{hostile} bytes for one connection at an msize of {Msize}");

        await AssertHealthyAsync(healthy);
    }

    /// <summary>Bytes the process allocates while serving one connection's whole life.</summary>
    private static async Task<long> MeasureAsync(ServerHarness harness, bool lie)
    {
        long before = GC.GetTotalAllocatedBytes(precise: true);
        await CycleAsync(harness, lie);
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    /// <summary>
    /// One connection's whole life: negotiate, send one frame, and be closed. The frame is either a
    /// legal <c>Tclunk</c> of a fid the connection does not hold, or a <c>size</c> of 0xFFFFFFFF.
    /// </summary>
    private static async Task CycleAsync(ServerHarness harness, bool lie)
    {
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, Msize, Ct);

        if (!lie)
        {
            await client.SendAsync(new Tclunk(1, 7), Ct);
            Assert.Equal(MessageType.Rlerror, MessageCodec.PeekType(await client.ReceiveFrameAsync(Ct)));
            return;
        }

        await client.SendRawAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, Ct);

        // Reading to the end of the stream is one await; polling for the close reason would put
        // the test's own timers inside the window being measured.
        Assert.Empty(await client.ReceiveFrameAsync(Ct));
    }

    private static async Task<WireClient> AttachedAsync(ServerHarness harness)
    {
        WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, Msize, Ct);
        await client.AttachAsync(1, Ct);
        return client;
    }

    /// <summary>Reads a file over a connection that is meant to be unaffected by the hostile one.</summary>
    private static async Task AssertHealthyAsync(WireClient client)
    {
        Rwalk walked = await client.WalkAsync(1000, 1, 900, ["hello.txt"], Ct)
            .WaitAsync(HealthTimeout, Ct);
        Assert.Single(walked.Wqids);

        await client.SendAsync(new Tlopen(1001, 900, 0), Ct);
        await client.ReceiveAsync<Rlopen>(Ct).WaitAsync(HealthTimeout, Ct);

        await client.SendAsync(new Tread(1002, 900, 0, 64), Ct);
        Rread read = await client.ReceiveAsync<Rread>(Ct).WaitAsync(HealthTimeout, Ct);
        Assert.Equal("hello, 9P\n"u8.ToArray(), read.Data.ToArray());

        await client.SendAsync(new Tclunk(1003, 900), Ct);
        await client.ReceiveAsync<Rclunk>(Ct).WaitAsync(HealthTimeout, Ct);
    }

    /// <summary>Waits for the reason the server closed with to reach this end of the wire.</summary>
    private static Task<CloseReason?> ClosedAsync(WireClient client) => ClosedAsync(client.Connection);

    private static async Task<CloseReason?> ClosedAsync(INinePConnection connection)
    {
        MemoryConnection memory = Assert.IsType<MemoryConnection>(connection);

        for (int attempt = 0; attempt < 600 && memory.PeerCloseReason is null; attempt++)
        {
            await Task.Delay(10, Ct);
        }

        return memory.PeerCloseReason;
    }

    /// <summary>Walks a fresh fid as many times as it takes, and counts what the cap refused.</summary>
    private static async Task<(int Bound, int Refused)> FloodFidsAsync(WireClient client, int attempts)
    {
        const int Batch = 128;
        int bound = 0;
        int refused = 0;

        for (int start = 0; start < attempts; start += Batch)
        {
            int count = Math.Min(Batch, attempts - start);

            for (int i = 0; i < count; i++)
            {
                await client.SendAsync(new Twalk((ushort)(i + 1), 1, (uint)(start + i + 2), []), Ct);
            }

            for (int i = 0; i < count; i++)
            {
                byte[] reply = await client.ReceiveFrameAsync(Ct);
                if (MessageCodec.PeekType(reply) == MessageType.Rwalk)
                {
                    bound++;
                    continue;
                }

                Assert.Equal(Errno.ENFILE, MessageCodec.Decode<Rlerror>(reply, Dialect.P9_2000_L).Ecode);
                refused++;
            }
        }

        return (bound, refused);
    }

    /// <summary>A <c>Twalk</c> the encoder would refuse to build: seventeen path elements.</summary>
    private static byte[] OverlongWalk(ushort tag, uint fid, uint newFid, int elements)
    {
        int size = Constants.HDRSZ + 4 + 4 + 2 + (elements * (2 + 1));
        byte[] frame = new byte[size];

        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)size);
        frame[4] = (byte)MessageType.Twalk;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), fid);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(11), newFid);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(15), (ushort)elements);

        for (int i = 0; i < elements; i++)
        {
            int at = 17 + (i * 3);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(at), 1);
            frame[at + 2] = (byte)'a';
        }

        return frame;
    }

    private static async Task WaitForReadsAsync(MemoryFile file, int reads)
    {
        // Bounded by the per-test deadline rather than a fixed count: a loaded CI runner can take
        // well over five seconds to schedule the handlers, and the deadline is the named failure.
        while (file.ReadsStarted < reads)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(file.ReadsStarted >= reads, "the handlers never reached the gate");
    }
}
