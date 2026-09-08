using System.Buffers;
using NineP.Protocol;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Client.Tests;

public sealed class AdditionalClientTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task F16_InvalidReadAllCapFailsBeforeNegotiation(int maximum)
    {
        var (connection, server) = FakeNinePServer.CreatePair();
        await using (server)
        await using (connection)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await NinePClient.ConnectAsync(connection, new ClientOptions { MaxReadAll = maximum }, Ct));
        }
    }

    [Fact]
    public async Task E6_F7c_InvalidAndConfiguredNamesSendNoOperation()
    {
        await using Harness h = await Harness.StartAsync(new ClientOptions { Limits = Limits.Default with { MaxNameLength = 64 } });
        foreach (string name in new[] { new string('x', 256), new string('é', 128) })
        {
            await Assert.ThrowsAsync<NinePProtocolException>(async () => await h.Fid.CreateAsync(name, 0x1A4, OpenMode.Read, OpenFlags.None, Ct));
        }
        await Error(Errno.ENAMETOOLONG, async () => await h.Fid.WalkAsync([new string('é', 32) + "x"], Ct));
        await Error(Errno.ENAMETOOLONG, async () => await h.Fid.CreateAsync(new string('x', 65), 0x1A4, OpenMode.Read, OpenFlags.None, Ct));
        await Error(Errno.ENAMETOOLONG, async () => await h.Session.Messages.RenameatAsync(new Trenameat(0, 1, "old", 1, new string('x', 65)), Ct));
        await h.SentinelAsync();
        Assert.Equal(1, h.Session.LiveFids);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(31u)]
    [InlineData(uint.MaxValue)]
    public async Task F20_IounitIsClampedAndZeroUsesMsize(uint advertised)
    {
        await using Harness h = await Harness.StartAsync(new ClientOptions { Msize = 4096 });
        Task opening = h.Fid.OpenAsync(OpenMode.ReadWrite, OpenFlags.None, Ct).AsTask();
        Tlopen open = await h.Server.ReadAsync<Tlopen>(Ct);
        await h.Server.WriteAsync(new Rlopen(open.Tag, h.Fid.Qid, advertised), Ct);
        await opening;
        int expected = advertised == 0 ? 4096 - Constants.IOHDRSZ : (int)Math.Min(advertised, 4096 - Constants.IOHDRSZ);
        Assert.Equal(expected, h.Fid.Iounit);
        Task<int> reading = h.Fid.ReadAsync(0, new byte[8192], Ct).AsTask();
        Tread read = await h.Server.ReadAsync<Tread>(Ct);
        Assert.Equal((uint)expected, read.Count);
        await h.Server.WriteAsync(new Rread(read.Tag, new byte[expected]), Ct);
        Assert.Equal(expected, await reading);
        Task<int> writing = h.Fid.WriteAsync(0, new byte[8192], Ct).AsTask();
        Twrite write = await h.Server.ReadAsync<Twrite>(Ct);
        Assert.Equal(expected, write.Data.Length);
        await h.Server.WriteAsync(new Rwrite(write.Tag, (uint)expected), Ct);
        Assert.Equal(expected, await writing);
    }

    [Fact]
    public async Task F17_FileGrowingAfterStatIsStillCapped()
    {
        await using Harness h = await Harness.StartAsync(new ClientOptions { MaxReadAll = 4, InFlightWindow = 1 });
        h.Fid.MarkOpened(h.Fid.Qid, 2);
        Task<byte[]> transfer = h.Fid.ReadAllAsync(Ct).AsTask();
        await h.SizeAsync(2);
        for (int i = 0; i < 2; i++)
        {
            Tread read = await h.Server.ReadAsync<Tread>(Ct);
            Assert.Equal((ulong)(i * 2), read.Offset);
            await h.Server.WriteAsync(new Rread(read.Tag, "ab"u8.ToArray()), Ct);
        }
        Tread probe = await h.Server.ReadAsync<Tread>(Ct);
        Assert.Equal(4ul, probe.Offset);
        Assert.Equal(1u, probe.Count);
        await h.Server.WriteAsync(new Rread(probe.Tag, "x"u8.ToArray()), Ct);
        await Error(Errno.EFBIG, async () => await transfer);
        await h.SentinelAsync();
    }

    [Fact]
    public async Task F15_OutOfOrderShortReadsRepairTheGap()
    {
        await using Harness h = await Harness.StartAsync(new ClientOptions { InFlightWindow = 2 });
        h.Fid.MarkOpened(h.Fid.Qid, 4);
        Task<byte[]> transfer = h.Fid.ReadAllAsync(Ct).AsTask();
        await h.SizeAsync(8);
        Tread first = await h.Server.ReadAsync<Tread>(Ct);
        Tread second = await h.Server.ReadAsync<Tread>(Ct);
        await h.Server.WriteAsync(new Rread(second.Tag, "efgh"u8.ToArray()), Ct);
        await h.Server.WriteAsync(new Rread(first.Tag, "ab"u8.ToArray()), Ct);
        Tread gap = await h.Server.ReadAsync<Tread>(Ct);
        Tread tail = await h.Server.ReadAsync<Tread>(Ct);
        Assert.Equal(2ul, gap.Offset);
        Assert.Equal(6ul, tail.Offset);
        await h.Server.WriteAsync(new Rread(tail.Tag, "gh"u8.ToArray()), Ct);
        await h.Server.WriteAsync(new Rread(gap.Tag, "cdef"u8.ToArray()), Ct);
        Tread eof = await h.Server.ReadAsync<Tread>(Ct);
        Tread beyond = await h.Server.ReadAsync<Tread>(Ct);
        await h.Server.WriteAsync(new Rread(beyond.Tag, ReadOnlyMemory<byte>.Empty), Ct);
        await h.Server.WriteAsync(new Rread(eof.Tag, ReadOnlyMemory<byte>.Empty), Ct);
        Assert.Equal("abcdefgh"u8.ToArray(), await transfer);
    }

    [Fact]
    public async Task F24_LargeOpaqueCookieRoundTripsAndRepeatedCookieFails()
    {
        await using Harness h = await Harness.StartAsync();
        const ulong Cookie = (1ul << 40) + 17;
        await using IAsyncEnumerator<DirEntry> iterator = h.Fid.ReadDirAsync(Ct).GetAsyncEnumerator(Ct);
        Task<bool> first = iterator.MoveNextAsync().AsTask();
        Treaddir read = await h.Server.ReadAsync<Treaddir>(Ct);
        await h.Server.WriteAsync(new Rreaddir(read.Tag, Entries(Cookie)), Ct);
        Assert.True(await first);
        Assert.Equal(Cookie, iterator.Current.Cursor);
        Task<bool> next = iterator.MoveNextAsync().AsTask();
        read = await h.Server.ReadAsync<Treaddir>(Ct);
        Assert.Equal(Cookie, read.Offset);
        await h.Server.WriteAsync(new Rreaddir(read.Tag, Entries(Cookie)), Ct);
        await Assert.ThrowsAsync<NinePProtocolException>(async () => await next);
        await h.SentinelAsync();
    }

    [Fact]
    public async Task F23_EarlyExitDoesNotFetchAnotherDirectoryPage()
    {
        await using Harness h = await Harness.StartAsync();
        IAsyncEnumerator<DirEntry> iterator = h.Fid.ReadDirAsync(Ct).GetAsyncEnumerator(Ct);
        Task<bool> first = iterator.MoveNextAsync().AsTask();
        Treaddir read = await h.Server.ReadAsync<Treaddir>(Ct);
        await h.Server.WriteAsync(new Rreaddir(read.Tag, Entries(1)), Ct);
        Assert.True(await first);
        await iterator.DisposeAsync();
        await h.SentinelAsync();
        Task closing = h.Fid.DisposeAsync().AsTask();
        Tclunk clunk = await h.Server.ReadAsync<Tclunk>(Ct);
        await h.Server.WriteAsync(new Rclunk(clunk.Tag), Ct);
        await closing;
        Assert.Equal(0, h.Session.LiveFids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F23_CancelledOrFailedDirectoryPageStopsAndSessionSurvives(bool cancel)
    {
        await using Harness h = await Harness.StartAsync();
        using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using IAsyncEnumerator<DirEntry> iterator = h.Fid.ReadDirAsync(stopped.Token).GetAsyncEnumerator(stopped.Token);
        Task<bool> first = iterator.MoveNextAsync().AsTask();
        Treaddir initial = await h.Server.ReadAsync<Treaddir>(Ct);
        await h.Server.WriteAsync(new Rreaddir(initial.Tag, Entries(1)), Ct);
        Assert.True(await first);
        Task<bool> next = iterator.MoveNextAsync().AsTask();
        Treaddir read = await h.Server.ReadAsync<Treaddir>(Ct);
        if (cancel)
        {
            await stopped.CancelAsync();
            Tflush flush = await h.Server.ReadAsync<Tflush>(Ct);
            Assert.Equal(read.Tag, flush.OldTag);
            await h.Server.WriteAsync(new Rflush(flush.Tag), Ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await next);
        }
        else
        {
            await h.Server.WriteAsync(new Rlerror(read.Tag, Errno.EIO), Ct);
            await Error(Errno.EIO, async () => await next);
        }
        await h.SentinelAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task F27_MidTransferErrorOrDisconnectNeverReportsSuccess(bool write, bool disconnect)
    {
        await using Harness h = await Harness.StartAsync(new ClientOptions { InFlightWindow = 1 });
        h.Fid.MarkOpened(h.Fid.Qid, 2);
        Task transfer = write ? h.Fid.WriteAllAsync("abcd"u8.ToArray(), Ct).AsTask() : h.Fid.ReadAllAsync(Ct).AsTask();
        if (!write)
        {
            await h.SizeAsync(4);
        }

        if (write)
        {
            Twrite first = await h.Server.ReadAsync<Twrite>(Ct);
            await h.Server.WriteAsync(new Rwrite(first.Tag, 2), Ct);
        }
        else
        {
            Tread first = await h.Server.ReadAsync<Tread>(Ct);
            await h.Server.WriteAsync(new Rread(first.Tag, "ab"u8.ToArray()), Ct);
        }
        ushort tag = write ? (await h.Server.ReadAsync<Twrite>(Ct)).Tag : (await h.Server.ReadAsync<Tread>(Ct)).Tag;
        if (disconnect)
        {
            await h.Server.DisposeAsync();
            await Assert.ThrowsAnyAsync<NinePException>(async () => await transfer.WaitAsync(Ct));
        }
        else
        {
            await h.Server.WriteAsync(new Rlerror(tag, Errno.ENOSPC), Ct);
            await Error(Errno.ENOSPC, async () => await transfer);
            await h.SentinelAsync();
        }
    }

    [Fact]
    public async Task F27_FailedBatchDrainsOtherOutstandingReplies()
    {
        await using Harness h = await Harness.StartAsync(new ClientOptions { InFlightWindow = 2 });
        h.Fid.MarkOpened(h.Fid.Qid, 2);
        Task<byte[]> transfer = h.Fid.ReadAllAsync(Ct).AsTask();
        await h.SizeAsync(4);
        Tread first = await h.Server.ReadAsync<Tread>(Ct);
        Tread second = await h.Server.ReadAsync<Tread>(Ct);
        await h.Server.WriteAsync(new Rlerror(first.Tag, Errno.EIO), Ct);
        // A round trip proves the error has been received while the other read is outstanding.
        await h.SentinelAsync();
        Assert.False(transfer.IsCompleted);
        await h.Server.WriteAsync(new Rread(second.Tag, "cd"u8.ToArray()), Ct);
        await Error(Errno.EIO, async () => await transfer);
        await h.SentinelAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F27_MidTransferCancellationFlushesPendingRequest(bool write)
    {
        await using Harness h = await Harness.StartAsync(new ClientOptions { InFlightWindow = 1 });
        h.Fid.MarkOpened(h.Fid.Qid, 2);
        using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task transfer = write ? h.Fid.WriteAllAsync("abcd"u8.ToArray(), stopped.Token).AsTask()
            : h.Fid.ReadAllAsync(stopped.Token).AsTask();
        if (!write) { await h.SizeAsync(4); }
        if (write)
        {
            Twrite first = await h.Server.ReadAsync<Twrite>(Ct);
            await h.Server.WriteAsync(new Rwrite(first.Tag, 2), Ct);
        }
        else
        {
            Tread first = await h.Server.ReadAsync<Tread>(Ct);
            await h.Server.WriteAsync(new Rread(first.Tag, "ab"u8.ToArray()), Ct);
        }
        ushort pending = write ? (await h.Server.ReadAsync<Twrite>(Ct)).Tag : (await h.Server.ReadAsync<Tread>(Ct)).Tag;
        await stopped.CancelAsync();
        Tflush flush = await h.Server.ReadAsync<Tflush>(Ct);
        Assert.Equal(pending, flush.OldTag);
        await h.Server.WriteAsync(new Rflush(flush.Tag), Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await transfer);
        await h.SentinelAsync();
        Task closing = h.Fid.DisposeAsync().AsTask();
        Tclunk clunk = await h.Server.ReadAsync<Tclunk>(Ct);
        await h.Server.WriteAsync(new Rclunk(clunk.Tag), Ct);
        await closing;
        Assert.Equal(0, h.Session.LiveFids);
    }

    [Fact]
    public async Task F17_MissingSizeMaskStillEnforcesActualBytes()
    {
        await using Harness h = await Harness.StartAsync(new ClientOptions { MaxReadAll = 2, InFlightWindow = 1 });
        Task<byte[]> reading = h.Fid.ReadAllAsync(Ct).AsTask();
        Tgetattr stat = await h.Server.ReadAsync<Tgetattr>(Ct);
        await h.Server.WriteAsync(new Rgetattr { Tag = stat.Tag, Valid = GetAttrMask.None, Size = ulong.MaxValue }, Ct);
        Tread read = await h.Server.ReadAsync<Tread>(Ct);
        Assert.Equal(3u, read.Count);
        await h.Server.WriteAsync(new Rread(read.Tag, "abc"u8.ToArray()), Ct);
        await Error(Errno.EFBIG, async () => await reading);
        await h.SentinelAsync();
    }

    private static byte[] Entries(ulong cookie)
    {
        DirEntry entry = new("entry", new Qid(QidType.QTFILE, 0, 5), FileKind.File, cookie);
        byte[] bytes = new byte[DirEntryCodec.GetEncodedSize(entry)];
        DirEntryCodec.Pack(bytes, [entry], out int count);
        Assert.Equal(1, count);
        return bytes;
    }

    private static async Task Error(int errno, Func<Task> action)
    {
        NinePException error = await Assert.ThrowsAsync<NinePException>(action);
        Assert.Equal(errno, error.Error.Errno);
    }

    private sealed class Harness(NinePSession session, FakeNinePServer server, NinePFid fid) : IAsyncDisposable
    {
        public NinePSession Session { get; } = session;
        public FakeNinePServer Server { get; } = server;
        public NinePFid Fid { get; } = fid;

        public static async Task<Harness> StartAsync(ClientOptions? options = null)
        {
            var (connection, server) = FakeNinePServer.CreatePair();
            Task<NinePSession> connecting = NinePClient.ConnectAsync(connection, options ?? new ClientOptions(), Ct).AsTask();
            await server.NegotiateAsync(Constants.Version9P2000L, cancellationToken: Ct);
            NinePSession session = await connecting;
            // Session.Track transfers this handle to the session, which the harness disposes.
#pragma warning disable CA2000
            NinePFid fid = new(session, session.RentFid(), new Qid(QidType.QTFILE, 0, 2));
#pragma warning restore CA2000
            session.Track(fid);
            fid.MarkOpened(fid.Qid, 4096);
            return new Harness(session, server, fid);
        }

        public async Task SizeAsync(ulong size)
        {
            Tgetattr request = await Server.ReadAsync<Tgetattr>(Ct);
            await Server.WriteAsync(new Rgetattr { Tag = request.Tag, Valid = GetAttrMask.Size, Size = size }, Ct);
        }

        public async Task SentinelAsync()
        {
            Task<Attr> request = Fid.GetAttrAsync(Ct).AsTask();
            await SizeAsync(0);
            await request;
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Session.DisposeAsync();
        }
    }
}
