using NineP.Client.Tests;
using NineP.Client.Tests.Conformance;
using NineP.Protocol;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.Server;
using NineP.TestSupport;
using Xunit;

namespace NineP.Client.Tests.Regression;

/// <summary>Wire-level regressions: fid recycling, close draining, chunked transfers, xattr commits and the transfer window.</summary>
[Trait("Category", "Regression")]
public sealed class ClientLifetimeRegressionTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Disposing a session whose peer has already gone away is quiet. The courtesy clunks cannot
    /// reach a dead connection, and the send path rethrows whatever the transport raised without
    /// wrapping it, so <c>ClunkQuietlyAsync</c> used to catch only <c>NinePException</c> and let an
    /// <c>IOException</c> — or, over the in-memory pipe, an <c>InvalidOperationException</c> —
    /// escape <c>DisposeAsync</c>. A caller unwinding from a crashed server is exactly who can
    /// least afford a new exception out of <c>await using</c>.
    /// <b>Mutation:</b> narrow the catch in <c>NinePSession.ClunkQuietlyAsync</c> back to
    /// <c>NinePException</c> and this test fails.
    /// </summary>
    [Fact]
    public async Task DisposingASessionWhoseServerIsGoneIsQuiet()
    {
        (INinePConnection client, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using FakeNinePServer _ = server;
        await using DeadWriteConnection dead = new(client);

        Task<NinePSession> connecting = NinePClient.ConnectAsync(dead, new ClientOptions
        {
            Dialects = [Dialect.P9_2000_L],
        }, Ct).AsTask();
        await server.NegotiateAsync(
            NineP.Protocol.Negotiation.Negotiator.VersionString(Dialect.P9_2000_L), cancellationToken: Ct);
        NinePSession session = await connecting;

        Task<NinePFid> attaching = session.AttachAsync(Ct).AsTask();
        Tattach attach = await server.ReadAsync<Tattach>(Ct);
        await server.WriteAsync(new Rattach(attach.Tag, new Qid(QidType.QTFILE, 0, attach.Fid + 1)), Ct);
        await attaching;

        // The socket breaks under the writer while the reader is still parked, which is the order
        // the courtesy clunk actually meets: the session has not yet been told it is terminated,
        // so the clunk is attempted and the transport raises straight out of the send path.
        dead.BreakWrites();

        await session.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasedHandlesCannotUseRecycledFids(bool remove)
    {
        await using Harness harness = await Harness.StartAsync();
        NinePFid stale = await harness.AttachAsync();
        Task closing = remove ? stale.RemoveAsync(Ct).AsTask() : stale.DisposeAsync().AsTask();
        await harness.AnswerCloseAsync(remove);
        await closing;
        NinePFid replacement = await harness.AttachAsync();
        Assert.Equal(stale.Fid, replacement.Fid);
        Assert.Same(stale, harness.Session.Root);

        LockRequest request = new(LockType.WriteLock, LockFlags.None, 0, 0, 1, "review");
        Func<Task>[] operations =
        [
            async () => await stale.CloneAsync(Ct),
            async () => await stale.WalkAsync(["child"], Ct),
            async () => await stale.OpenAsync(OpenMode.Read, cancellationToken: Ct),
            async () => await stale.CreateAsync("child", FileKind.File, Perms.P0666, OpenMode.Write, cancellationToken: Ct),
            async () => await stale.ReadAsync(0, new byte[1], Ct),
            async () => await stale.WriteAsync(0, new byte[1], Ct),
            async () => await stale.ReadAllAsync(Ct),
            async () => await stale.WriteAllAsync(new byte[1], Ct),
            async () => await stale.GetAttrAsync(Ct),
            async () => await stale.SetAttrAsync(new SetAttr { Size = 0 }, Ct),
            async () => await stale.RemoveAsync(Ct),
            async () => await stale.FsyncAsync(cancellationToken: Ct),
            async () => await stale.LockAsync(request, Ct),
            async () => await stale.GetLockAsync(request, Ct),
            async () => await stale.GetXattrAsync("user.x", Ct),
            async () => await stale.SetXattrAsync("user.x", new byte[1], cancellationToken: Ct),
            async () => { await foreach (DirEntry _ in stale.ReadDirAsync(Ct)) { } },
            async () => await harness.Session.WalkAsync("child", Ct),
        ];
        foreach (Func<Task> operation in operations)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(operation);
        }

        // A sentinel proves none of the rejected operations placed a request on the wire.
        Task<int> readTask = replacement.ReadAsync(0, new byte[1], Ct).AsTask();
        Tread read = await harness.Server.ReadAsync<Tread>(Ct);
        Assert.Equal(replacement.Fid, read.Fid);
        await harness.Server.WriteAsync(new Rread(read.Tag, new byte[] { 42 }), Ct);
        Assert.Equal(1, await readTask);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CloseWaitsForInFlightReadOrWrite(bool write, bool remove)
    {
        await using Harness harness = await Harness.StartAsync();
        NinePFid fid = await harness.AttachAsync();
        Task<int> operation = write
            ? fid.WriteAsync(0, new byte[] { 1 }, Ct).AsTask()
            : fid.ReadAsync(0, new byte[1], Ct).AsTask();
        ushort tag = write
            ? (await harness.Server.ReadAsync<Twrite>(Ct)).Tag
            : (await harness.Server.ReadAsync<Tread>(Ct)).Tag;
        Task closing = remove ? fid.RemoveAsync(Ct).AsTask() : fid.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        NinePFid other = await harness.AttachAsync();
        Assert.NotEqual(fid.Fid, other.Fid); // The sentinel attach must precede any close frame.
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await fid.ReadAsync(0, new byte[1], Ct));
        if (write)
        {
            await harness.Server.WriteAsync(new Rwrite(tag, 1), Ct);
        }
        else
        {
            await harness.Server.WriteAsync(new Rread(tag, new byte[] { 1 }), Ct);
        }

        Assert.Equal(1, await operation);
        await harness.AnswerCloseAsync(remove);
        await closing;
        Assert.Equal(fid.Fid, (await harness.AttachAsync()).Fid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalWaitsForTheEntireChunkedTransfer(bool write)
    {
        await using Harness harness = await Harness.StartAsync();
        NinePFid fid = await harness.AttachAsync();
        fid.MarkOpened(fid.Qid, 2);
        Task transfer = write ? fid.WriteAllAsync("abcdef"u8.ToArray(), Ct).AsTask() : fid.ReadAllAsync(Ct).AsTask();
        if (!write)
        {
            await harness.AnswerReadSizeAsync();
        }

        ushort first = write ? (await harness.Server.ReadAsync<Twrite>(Ct)).Tag : (await harness.Server.ReadAsync<Tread>(Ct)).Tag;
        ushort second = write ? (await harness.Server.ReadAsync<Twrite>(Ct)).Tag : (await harness.Server.ReadAsync<Tread>(Ct)).Tag;
        Task closing = fid.DisposeAsync().AsTask();
        await harness.AttachAsync(); // No close may overtake this sentinel while replies are pending.
        if (write)
        {
            await harness.Server.WriteAsync(new Rwrite(second, 2), Ct);
            await harness.Server.WriteAsync(new Rwrite(first, 2), Ct);
            Twrite final = await harness.Server.ReadAsync<Twrite>(Ct);
            Assert.Equal("ef"u8.ToArray(), final.Data.ToArray());
            await harness.Server.WriteAsync(new Rwrite(final.Tag, 2), Ct);
        }
        else
        {
            await harness.Server.WriteAsync(new Rread(second, "cd"u8.ToArray()), Ct);
            await harness.Server.WriteAsync(new Rread(first, "ab"u8.ToArray()), Ct);
            Tread next = await harness.Server.ReadAsync<Tread>(Ct);
            Tread last = await harness.Server.ReadAsync<Tread>(Ct);
            await harness.Server.WriteAsync(new Rread(last.Tag, ReadOnlyMemory<byte>.Empty), Ct);
            await harness.Server.WriteAsync(new Rread(next.Tag, ReadOnlyMemory<byte>.Empty), Ct);
        }

        await transfer;
        await harness.AnswerCloseAsync(false);
        await closing;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalDuringCloneOrWalkWaitsWithoutNestedLeaseFailure(bool walk)
    {
        await using Harness harness = await Harness.StartAsync();
        NinePFid fid = await harness.AttachAsync();
        Task<NinePFid> operation = walk ? fid.WalkAsync(["child"], Ct).AsTask() : fid.CloneAsync(Ct).AsTask();
        Twalk clone = await harness.Server.ReadAsync<Twalk>(Ct);
        Task closing = fid.DisposeAsync().AsTask();
        await harness.AttachAsync();
        await harness.Server.WriteAsync(new Rwalk(clone.Tag, []), Ct);
        if (walk)
        {
            Twalk step = await harness.Server.ReadAsync<Twalk>(Ct);
            await harness.Server.WriteAsync(new Rwalk(step.Tag, [new Qid(QidType.QTFILE, 0, 2)]), Ct);
        }

        NinePFid result = await operation;
        Assert.False(result.IsReleased);
        await harness.AnswerCloseAsync(false);
        await closing;
    }

    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    [InlineData(Dialect.P9_2000_L)]
    public async Task DirectoryFetchDrainsBeforeCloseAndResumedEnumerationCannotUseRecycledFid(Dialect dialect)
    {
        await using Harness harness = await Harness.StartAsync(dialect: dialect);
        NinePFid fid = await harness.AttachAsync();
        await using IAsyncEnumerator<DirEntry> entries = fid.ReadDirAsync(Ct).GetAsyncEnumerator(Ct);
        Task<bool> next = entries.MoveNextAsync().AsTask();
        ushort tag = dialect == Dialect.P9_2000_L
            ? (await harness.Server.ReadAsync<Treaddir>(Ct)).Tag
            : (await harness.Server.ReadAsync<Tread>(Ct)).Tag;
        Task closing = fid.DisposeAsync().AsTask();
        await harness.AttachAsync();
        if (dialect == Dialect.P9_2000_L)
        {
            byte[] payload = new byte[128];
            int length = DirEntryCodec.Pack(payload, [new DirEntry("child", new Qid(QidType.QTFILE, 0, 2), FileKind.File, 1)], out _);
            await harness.Server.WriteAsync(new Rreaddir(tag, payload.AsMemory(0, length)), Ct);
        }
        else
        {
            await harness.Server.WriteAsync(new Rread(tag, LegacyDirectoryRecord(dialect)), Ct);
        }
        Assert.True(await next);
        await harness.AnswerCloseAsync(false);
        await closing;
        Assert.Equal(fid.Fid, (await harness.AttachAsync()).Fid);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await entries.MoveNextAsync());
        await harness.AttachAsync(); // No stale Treaddir precedes the sentinel.
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AppendTransferRetriesOnlyTheUnacknowledgedSuffix(bool metadataAppend, bool repeatedShort)
    {
        await using Harness harness = await Harness.StartAsync();
        NinePFid fid = await harness.AttachAsync();
        Task opening = fid.OpenAsync(OpenMode.Write, metadataAppend ? OpenFlags.None : OpenFlags.Append, Ct).AsTask();
        Tlopen open = await harness.Server.ReadAsync<Tlopen>(Ct);
        await harness.Server.WriteAsync(new Rlopen(open.Tag, new Qid(metadataAppend ? QidType.QTAPPEND : QidType.QTFILE, 0, 1), 2), Ct);
        await opening;
        Task transfer = fid.WriteAllAsync("abcdef"u8.ToArray(), Ct).AsTask();
        List<byte> stored = [];
        int requests = 0;
        while (stored.Count < 6)
        {
            Twrite request = await harness.Server.ReadAsync<Twrite>(Ct);
            Assert.Equal((ulong)stored.Count, request.Offset);
            Assert.InRange(request.Data.Length, 1, 2);
            // A sentinel attach must be the next frame: a second pipelined append would reorder
            // on peers dispatching requests concurrently, even if all replies acknowledge fully.
            await harness.AttachAsync();
            int accepted = repeatedShort || requests++ == 0 ? 1 : request.Data.Length;
            stored.AddRange(request.Data.Span[..accepted].ToArray());
            await harness.Server.WriteAsync(new Rwrite(request.Tag, (uint)accepted), Ct);
        }

        await transfer;
        Assert.Equal("abcdef"u8.ToArray(), stored);
    }

    [Fact]
    public async Task OrdinaryWritesStayPipelinedAndRepairShortWriteGapsWithReorderedReplies()
    {
        await using Harness harness = await Harness.StartAsync();
        NinePFid fid = await harness.AttachAsync();
        fid.MarkOpened(fid.Qid, 2);
        Task transfer = fid.WriteAllAsync("abcdef"u8.ToArray(), Ct).AsTask();
        byte[] stored = new byte[6];
        Twrite first = await harness.Server.ReadAsync<Twrite>(Ct);
        Twrite second = await harness.Server.ReadAsync<Twrite>(Ct); // Both sent before either reply.
        stored[0] = first.Data.Span[0];
        second.Data.CopyTo(stored.AsMemory((int)second.Offset));
        await harness.Server.WriteAsync(new Rwrite(second.Tag, 2), Ct);
        await harness.Server.WriteAsync(new Rwrite(first.Tag, 1), Ct);
        Twrite retry = await harness.Server.ReadAsync<Twrite>(Ct);
        Twrite later = await harness.Server.ReadAsync<Twrite>(Ct);
        retry.Data.CopyTo(stored.AsMemory((int)retry.Offset));
        later.Data.CopyTo(stored.AsMemory((int)later.Offset));
        await harness.Server.WriteAsync(new Rwrite(later.Tag, (uint)later.Data.Length), Ct);
        await harness.Server.WriteAsync(new Rwrite(retry.Tag, (uint)retry.Data.Length), Ct);
        Twrite last = await harness.Server.ReadAsync<Twrite>(Ct);
        last.Data.CopyTo(stored.AsMemory((int)last.Offset));
        await harness.Server.WriteAsync(new Rwrite(last.Tag, (uint)last.Data.Length), Ct);
        await transfer;
        Assert.Equal("abcdef"u8.ToArray(), stored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEarlyShortReplyCannotHideALaterOvercount(bool write)
    {
        await using Harness harness = await Harness.StartAsync();
        NinePFid fid = await harness.AttachAsync();
        fid.MarkOpened(fid.Qid, 2);
        Task transfer = write ? fid.WriteAllAsync(new byte[4], Ct).AsTask() : fid.ReadAllAsync(Ct).AsTask();
        if (!write)
        {
            await harness.AnswerReadSizeAsync();
        }

        if (write)
        {
            Twrite first = await harness.Server.ReadAsync<Twrite>(Ct);
            Twrite second = await harness.Server.ReadAsync<Twrite>(Ct);
            await harness.Server.WriteAsync(new Rwrite(second.Tag, 3), Ct);
            await harness.Server.WriteAsync(new Rwrite(first.Tag, 1), Ct);
        }
        else
        {
            Tread first = await harness.Server.ReadAsync<Tread>(Ct);
            Tread second = await harness.Server.ReadAsync<Tread>(Ct);
            await harness.Server.WriteAsync(new Rread(second.Tag, new byte[3]), Ct);
            await harness.Server.WriteAsync(new Rread(first.Tag, new byte[1]), Ct);
        }

        await Assert.ThrowsAsync<NinePProtocolException>(async () => await transfer);
    }

    [Fact]
    public async Task AppendZeroProgressFailsRatherThanLooping()
    {
        await using Harness harness = await Harness.StartAsync();
        NinePFid fid = await harness.AttachAsync();
        fid.MarkOpened(new Qid(QidType.QTAPPEND, 0, 1), 2);
        Task transfer = fid.WriteAllAsync(new byte[3], Ct).AsTask();
        Twrite request = await harness.Server.ReadAsync<Twrite>(Ct);
        await harness.Server.WriteAsync(new Rwrite(request.Tag, 0), Ct);
        NinePException failure = await Assert.ThrowsAsync<NinePException>(async () => await transfer);
        Assert.Equal(Errno.EIO, failure.Error.Errno);
    }

    [Theory]
    [InlineData(false, Errno.ENOSPC)]
    [InlineData(false, Errno.EEXIST)]
    [InlineData(true, Errno.ENODATA)]
    public async Task XattrCommitErrorsArePropagatedAndSinkIsReleased(bool empty, int errno)
    {
        await using Harness harness = await Harness.StartAsync();
        NinePFid fid = await harness.AttachAsync();
        Task setting = fid.SetXattrAsync("user.x", empty ? ReadOnlyMemory<byte>.Empty : new byte[] { 1 }, cancellationToken: Ct).AsTask();
        await harness.AnswerXattrSetupAsync(empty);
        Tclunk commit = await harness.Server.ReadAsync<Tclunk>(Ct);
        await harness.Server.WriteAsync(new Rlerror(commit.Tag, errno), Ct);
        NinePException failure = await Assert.ThrowsAsync<NinePException>(async () => await setting);
        Assert.Equal(errno, failure.Error.Errno);
        Assert.Equal(1, harness.Session.LiveFids);
        Assert.False(fid.IsReleased);
        Assert.Equal(commit.Fid, (await harness.AttachAsync()).Fid); // No duplicate clunk.
        Task<int> reading = fid.ReadAsync(0, new byte[1], Ct).AsTask();
        Tread read = await harness.Server.ReadAsync<Tread>(Ct);
        await harness.Server.WriteAsync(new Rread(read.Tag, new byte[] { 1 }), Ct);
        Assert.Equal(1, await reading);
    }

    [Fact]
    public async Task EarlierXattrWriteErrorSurvivesCleanupError()
    {
        await using Harness harness = await Harness.StartAsync();
        NinePFid fid = await harness.AttachAsync();
        Task setting = fid.SetXattrAsync("user.x", new byte[] { 1 }, cancellationToken: Ct).AsTask();
        await harness.AnswerXattrSetupAsync(true);
        Twrite write = await harness.Server.ReadAsync<Twrite>(Ct);
        await harness.Server.WriteAsync(new Rlerror(write.Tag, Errno.EIO), Ct);
        Tclunk cleanup = await harness.Server.ReadAsync<Tclunk>(Ct);
        await harness.Server.WriteAsync(new Rlerror(cleanup.Tag, Errno.ENOSPC), Ct);
        NinePException failure = await Assert.ThrowsAsync<NinePException>(async () => await setting);
        Assert.Equal(Errno.EIO, failure.Error.Errno);
        Assert.Equal(1, harness.Session.LiveFids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task XattrCommitMustBeAcknowledged(bool timeout)
    {
        await using Harness harness = await Harness.StartAsync(timeout ? TimeSpan.FromMilliseconds(100) : null);
        NinePFid fid = await harness.AttachAsync();
        Task setting = fid.SetXattrAsync("user.x", ReadOnlyMemory<byte>.Empty, cancellationToken: Ct).AsTask();
        await harness.AnswerXattrSetupAsync(true);
        Tclunk commit = await harness.Server.ReadAsync<Tclunk>(Ct);
        Assert.False(setting.IsCompleted);
        if (timeout)
        {
            Tflush flush = await harness.Server.ReadAsync<Tflush>(Ct);
            Assert.Equal(commit.Tag, flush.OldTag);
            await harness.Server.WriteAsync(new Rflush(flush.Tag), Ct);
            await Assert.ThrowsAsync<TimeoutException>(async () => await setting);
        }
        else
        {
            await harness.Server.WriteAsync(new Rclunk(commit.Tag), Ct);
            await setting;
        }

        Assert.Equal(1, harness.Session.LiveFids);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task InvalidWindowFailsBeforeDialOrNegotiation(int window)
    {
        DialSpy transport = new();
        ClientOptions options = new() { InFlightWindow = window, ConnectTimeout = TimeSpan.FromMilliseconds(100) };
        NinePAddress address = new(NinePScheme.Memory, "review", 0, string.Empty);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await NinePClient.ConnectAsync(transport, address, options, Ct));
        Assert.Equal(0, transport.Dials);
        // Even the address-only overload must reject options before selecting a transport.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await NinePClient.ConnectAsync(address, options, Ct));
        (INinePConnection client, INinePConnection server) = MemoryTransport.CreatePair();
        await using (client.ConfigureAwait(false))
        await using (server.ConfigureAwait(false))
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await NinePClient.ConnectAsync(client, options, Ct));
        }
    }

    [Fact]
    public async Task MinimumWindowWorksAndCancelledTransfersFailBeforeSending()
    {
        await using Harness harness = await Harness.StartAsync(window: 1);
        NinePFid fid = await harness.AttachAsync();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fid.ReadAllAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fid.WriteAllAsync(new byte[1], cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fid.WriteAllAsync(ReadOnlyMemory<byte>.Empty, cancelled.Token));
        Task transfer = fid.WriteAllAsync(new byte[1], Ct).AsTask();
        Twrite write = await harness.Server.ReadAsync<Twrite>(Ct);
        await harness.Server.WriteAsync(new Rwrite(write.Tag, 1), Ct);
        await transfer;
    }

    [Theory]
    [InlineData(false, Errno.ENOSPC)]
    [InlineData(false, Errno.EEXIST)]
    [InlineData(true, Errno.EIO)]
    public async Task RealServerXattrHandlerCommitRefusalReachesCaller(bool empty, int errno)
    {
        MemoryFilesystem tree = new();
        RefusingXattrFile file = tree.Root.Add(new RefusingXattrFile(errno));
        MemoryTransport transport = new();
        NinePAddress address = new(NinePScheme.Memory, "commit-" + Guid.NewGuid().ToString("N"), 0, string.Empty);
        await using NinePServer server = new(new ServerOptions { Listen = [address], Transports = [transport] });
        Task serving = server.ServeAsync(tree, Ct);
        await server.Listening;
        await using NinePSession session = await NinePClient.ConnectAsync(transport, address,
            new ClientOptions { Dialects = [Dialect.P9_2000_L], Uname = "glenda" }, Ct);
        await session.AttachAsync(Ct);
        await using NinePFid fid = await session.WalkAsync("refusing", Ct);
        NinePException failure = await Assert.ThrowsAsync<NinePException>(async () =>
            await fid.SetXattrAsync("user.x", empty ? ReadOnlyMemory<byte>.Empty : new byte[] { 1 }, cancellationToken: Ct));
        Assert.Equal(errno, failure.Error.Errno);
        Assert.Equal(1, file.Commits);
        Assert.Equal(2, session.LiveFids);
        Assert.Equal(file.Qid, (await fid.GetAttrAsync(Ct)).Qid);
        await session.DisposeAsync();
        await server.DisposeAsync();
        await serving;
    }

    private static ReadOnlyMemory<byte> LegacyDirectoryRecord(Dialect dialect)
    {
        byte[] buffer = new byte[256];
        WireWriter writer = new(buffer);
        StatRecord record = new() { Name = "child", Qid = new Qid(QidType.QTFILE, 0, 2) };
        StatCodec.WriteRecord(ref writer, in record, dialect);
        return buffer.AsMemory(0, writer.Position);
    }

    private sealed class RefusingXattrFile(int errno)
        : MemoryNode("refusing", FileKind.File, Perms.P0666, 20), IXattrHandler
    {
        public int Commits { get; private set; }
        public ValueTask<ReadOnlyMemory<byte>> ListXattrAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
        public ValueTask<ReadOnlyMemory<byte>> GetXattrAsync(string name, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
        public ValueTask SetXattrAsync(string name, ReadOnlyMemory<byte> value, XattrFlags flags, CancellationToken cancellationToken = default)
        {
            Commits++;
            throw new NinePException(NinePError.FromErrno(errno));
        }
        public ValueTask RemoveXattrAsync(string name, CancellationToken cancellationToken = default)
        {
            Commits++;
            throw new NinePException(NinePError.FromErrno(errno));
        }
    }

    private sealed class DialSpy : ITransport
    {
        public int Dials { get; private set; }
        public IReadOnlyCollection<NinePScheme> Schemes => [NinePScheme.Memory];
        public ValueTask<INinePConnection> ConnectAsync(NinePAddress address, CancellationToken cancellationToken = default)
        {
            Dials++;
            throw new InvalidOperationException("Invalid options must be rejected before dialing");
        }
        public ValueTask<INinePListener> ListenAsync(NinePAddress address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// A connection whose writes fail on demand while its reads stay parked, so a caller meets a
    /// broken socket before anything has declared the session terminated.
    /// </summary>
    private sealed class DeadWriteConnection(INinePConnection inner) : INinePConnection
    {
        private volatile bool _broken;

        public NinePAddress RemoteAddress => inner.RemoteAddress;

        public PeerIdentity? PeerIdentity => inner.PeerIdentity;

        public void BreakWrites() => _broken = true;

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
            _broken
                ? throw new IOException("the socket is gone")
                : inner.WriteAsync(message, cancellationToken);

        public ValueTask CloseAsync(CloseReason reason, CancellationToken cancellationToken = default) =>
            inner.CloseAsync(reason, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class Harness(NinePSession session, FakeNinePServer server) : IAsyncDisposable
    {
        public NinePSession Session { get; } = session;
        public FakeNinePServer Server { get; } = server;

        public static async Task<Harness> StartAsync(TimeSpan? requestTimeout = null, int window = 2, Dialect dialect = Dialect.P9_2000_L)
        {
            (INinePConnection client, FakeNinePServer server) = FakeNinePServer.CreatePair();
            Task<NinePSession> connecting = NinePClient.ConnectAsync(client, new ClientOptions
            {
                Dialects = [dialect],
                InFlightWindow = window,
                RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10),
            }, Ct).AsTask();
            await server.NegotiateAsync(NineP.Protocol.Negotiation.Negotiator.VersionString(dialect), cancellationToken: Ct);
            return new Harness(await connecting, server);
        }

        public async Task<NinePFid> AttachAsync()
        {
            Task<NinePFid> attaching = Session.AttachAsync(Ct).AsTask();
            Tattach attach = await Server.ReadAsync<Tattach>(Ct);
            await Server.WriteAsync(new Rattach(attach.Tag, new Qid(QidType.QTFILE, 0, attach.Fid + 1)), Ct);
            return await attaching;
        }

        public async Task AnswerReadSizeAsync()
        {
            Tgetattr request = await Server.ReadAsync<Tgetattr>(Ct);
            await Server.WriteAsync(new Rgetattr { Tag = request.Tag, Valid = GetAttrMask.Size }, Ct);
        }

        public async Task AnswerCloseAsync(bool remove)
        {
            if (remove)
            {
                Tremove request = await Server.ReadAsync<Tremove>(Ct);
                await Server.WriteAsync(new Rremove(request.Tag), Ct);
            }
            else
            {
                Tclunk request = await Server.ReadAsync<Tclunk>(Ct);
                await Server.WriteAsync(new Rclunk(request.Tag), Ct);
            }
        }

        public async Task AnswerXattrSetupAsync(bool skipWrite)
        {
            Twalk clone = await Server.ReadAsync<Twalk>(Ct);
            await Server.WriteAsync(new Rwalk(clone.Tag, []), Ct);
            Txattrcreate create = await Server.ReadAsync<Txattrcreate>(Ct);
            await Server.WriteAsync(new Rxattrcreate(create.Tag), Ct);
            if (!skipWrite)
            {
                Twrite write = await Server.ReadAsync<Twrite>(Ct);
                await Server.WriteAsync(new Rwrite(write.Tag, (uint)write.Data.Length), Ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Session.DisposeAsync();
        }
    }
}
