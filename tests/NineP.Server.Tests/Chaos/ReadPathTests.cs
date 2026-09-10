using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Chaos;

/// <summary>
/// The <c>Rread</c> hot path, against architecture §9's binding rule: "no per-message allocation of
/// payload copies in the hot path". A client asks for a whole <c>iounit</c> on every read — the
/// shipped client does, and so does Linux v9fs — so a payload buffer sized by the count rather than
/// by the file cost one msize-sized allocation to carry ten bytes, 1 MiB per read at the <c>.L</c>
/// default. Neither of the two benchmark rows could see it: one reads a 1 GiB file, where the
/// buffer is used in full, and the other has no read at all.
/// <para>
/// The allocation test measures <c>GC.GetTotalAllocatedBytes</c>, which is process-wide, so this
/// class runs alone: under the Microsoft.Testing.Platform runner every test that finishes while
/// the rest of the assembly runs in parallel is serialised to the <c>dotnet test</c> bridge, and
/// that traffic landed in every measured window (83-102 KB per read in a Debug run against a
/// 64 KB bound, with the same test at 6 KB on its own).
/// </para>
/// </summary>
[Collection(QuietCollection.Name)]
[Trait("Category", "Chaos")]
public sealed class ReadPathTests
{
    /// <summary>The msize these tests negotiate; the server's maximum, and v9fs's default.</summary>
    private const uint Msize = 1024 * 1024;

    /// <summary>How many reads one measured cycle makes.</summary>
    private const int Reads = 16;

    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// The core hands the handler no more buffer than the file has left to give, whatever the
    /// client asked for. <b>Mutation:</b> read into a buffer of <c>budget</c> — what shipped —
    /// and the handler is handed 1 048 552 bytes for a ten-byte file.
    /// </summary>
    [Fact]
    public async Task AReadIsHandedNoMoreBufferThanTheFileHasLeft()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        MemoryFile file = (MemoryFile)harness.Tree.Root.Children["hello.txt"];

        await using WireClient client = await OpenAsync(harness);

        await client.SendAsync(new Tread(4, 2, 0, Msize), Ct);
        Rread reply = await client.ReceiveAsync<Rread>(Ct);

        Assert.Equal("hello, 9P\n"u8.ToArray(), reply.Data.ToArray());
        Assert.Equal(file.Data.Length, file.LargestReadBuffer);

        // And a read that starts inside the file is bounded by what is left of it, not by the file.
        await client.SendAsync(new Tread(5, 2, 7, Msize), Ct);
        Assert.Equal("9P\n"u8.ToArray(), (await client.ReceiveAsync<Rread>(Ct)).Data.ToArray());
        Assert.Equal(file.Data.Length, file.LargestReadBuffer);
    }

    /// <summary>
    /// And what that costs the allocator: reading a ten-byte file at an msize of 1 MiB allocates a
    /// small multiple of the file, not a multiple of the msize. <b>Mutation:</b> put
    /// <c>new byte[budget]</c> back and each of these reads allocates 1 MiB, which is sixteen
    /// times the whole bound below in one cycle.
    /// </summary>
    [Fact]
    public async Task ReadingASmallFileAtALargeMsizeDoesNotAllocateTheMsize()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using WireClient client = await OpenAsync(harness);

        // Warm the path before it is measured: the first read of a run pays for the JIT and for
        // the pools behind it, and that is not what a read costs.
        await CycleAsync(client);

        // GC.GetTotalAllocatedBytes is process-wide and the rest of this assembly runs in
        // parallel, so a single window measures the machine. What is asserted is the quietest
        // cycle — the one whose window nothing else landed in — as HostileClientTests does for the
        // same reason.
        long quietest = long.MaxValue;
        for (int cycle = 0; cycle < 12; cycle++)
        {
            quietest = Math.Min(quietest, await CycleAsync(client));
        }

        long perRead = quietest / Reads;

        // Measured on an M4 Pro at 0.1.0: 5 813 bytes per read — this client's own frames and the
        // server's reply rental together, in one process — against 1 054 899 before the fix. The
        // bound is a sixteenth of the msize, which no pooled read comes near and which no
        // budget-sized allocation can stay under.
        Assert.True(
            perRead < Msize / 16,
            $"{perRead} bytes allocated per read of a ten-byte file at an msize of {Msize}");
    }

    private static async Task<long> CycleAsync(WireClient client)
    {
        long before = GC.GetTotalAllocatedBytes(precise: true);

        for (int i = 0; i < Reads; i++)
        {
            await client.SendAsync(new Tread(4, 2, 0, Msize), Ct);
            Assert.Equal(10, (await client.ReceiveAsync<Rread>(Ct)).Data.Length);
        }

        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    private static async Task<WireClient> OpenAsync(ServerHarness harness)
    {
        WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, Msize, Ct);

        await client.AttachAsync(1, Ct);
        await client.WalkAsync(2, 1, 2, ["hello.txt"], Ct);
        await client.SendAsync(new Tlopen(3, 2, 0), Ct);
        await client.ReceiveAsync<Rlopen>(Ct);

        return client;
    }
}

/// <summary>
/// A collection with parallelisation off, for the tests that measure the process rather than a
/// value: they run after the parallel ones, with nothing else allocating.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class QuietCollection
{
    /// <summary>The collection's name, for <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "quiet: process-wide measurements";
}
