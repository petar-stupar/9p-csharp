using System.Diagnostics;
using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using Xunit;

namespace NineP.Client.Tests;

/// <summary>
/// The client half of flush(5) and reference §8 rule 14, against a fake server that decides when
/// the <c>Rflush</c> arrives and what arrives before it.
/// </summary>
public sealed class ClientFlushTests
{
    /// <summary>How long a bounded wait is given before the test calls it a hang.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 57: after sending <c>Tflush</c> the client waits for the <c>Rflush</c> before reusing
    /// the old tag. Until then the server may still answer it, and a reused tag would collide with
    /// that answer.
    /// </summary>
    [Fact]
    public async Task WaitsForRflushBeforeTagReuse()
    {
        await using Harness harness = await Harness.StartAsync();

        using CancellationTokenSource caller = new();
        Task<Rclunk> request = harness.Session.Messages
            .ClunkAsync(new Tclunk(0, 1), caller.Token).AsTask();

        Tclunk sent = await harness.Server.ReadAsync<Tclunk>(Ct);
        Assert.Equal(1, harness.Session.Multiplexer.TagsInUse);

        await caller.CancelAsync();

        Tflush flush = await harness.Server.ReadAsync<Tflush>(Ct);

        Assert.Equal(sent.Tag, flush.OldTag);
        Assert.NotEqual(sent.Tag, flush.Tag);

        // The Rflush has not arrived, so the old tag is still spent: two tags are outstanding.
        Assert.Equal(2, harness.Session.Multiplexer.TagsInUse);
        Assert.False(request.IsCompleted);

        await harness.Server.WriteAsync(new Rflush(flush.Tag), Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        Assert.Equal(0, harness.Session.Multiplexer.TagsInUse);
    }

    /// <summary>
    /// Rule 58: a reply that arrives before the <c>Rflush</c> is delivered to the caller normally.
    /// The request may have had an effect — a <c>Tcreate</c> may have created, a <c>Twalk</c> may
    /// have bound a fid — and discarding the reply would leak whatever it named.
    /// </summary>
    [Fact]
    public async Task DeliversRaceyReply()
    {
        await using Harness harness = await Harness.StartAsync();

        using CancellationTokenSource caller = new();
        Task<Rwalk> request = harness.Session.Messages
            .WalkAsync(new Twalk(0, 1, 2, ["dir"]), caller.Token).AsTask();

        Twalk sent = await harness.Server.ReadAsync<Twalk>(Ct);
        await caller.CancelAsync();
        Tflush flush = await harness.Server.ReadAsync<Tflush>(Ct);

        // The server had already answered before it saw the flush; that answer still counts.
        Qid bound = new(QidType.QTDIR, 1, 42);
        await harness.Server.WriteAsync(new Rwalk(sent.Tag, [bound]), Ct);
        await harness.Server.WriteAsync(new Rflush(flush.Tag), Ct);

        Rwalk reply = await request;

        Assert.Equal(sent.Tag, reply.Tag);
        Assert.Equal(bound, Assert.Single(reply.Wqids));
        Assert.Equal(0, harness.Session.Multiplexer.TagsInUse);
    }

    /// <summary>An error that raced the flush reaches the caller as the error it is.</summary>
    [Fact]
    public async Task ARaceyErrorReplyIsDeliveredToo()
    {
        await using Harness harness = await Harness.StartAsync();

        using CancellationTokenSource caller = new();
        Task<Rclunk> request = harness.Session.Messages
            .ClunkAsync(new Tclunk(0, 1), caller.Token).AsTask();

        Tclunk sent = await harness.Server.ReadAsync<Tclunk>(Ct);
        await caller.CancelAsync();
        Tflush flush = await harness.Server.ReadAsync<Tflush>(Ct);

        await harness.Server.WriteAsync(new Rlerror(sent.Tag, Errno.EBADF), Ct);
        await harness.Server.WriteAsync(new Rflush(flush.Tag), Ct);

        NinePException failure = await Assert.ThrowsAsync<NinePException>(async () => await request);

        Assert.Equal(Errno.EBADF, failure.Error.Errno);
    }

    /// <summary>A <c>Tflush</c> for a tag nothing is pending on is answered with <c>Rflush</c>.</summary>
    [Fact]
    public async Task FlushOfUnknownTagIsAnswered()
    {
        await using Harness harness = await Harness.StartAsync();

        Task<Rflush> request = harness.Session.Messages
            .FlushAsync(new Tflush(0, 4242), Ct).AsTask();

        Tflush sent = await harness.Server.ReadAsync<Tflush>(Ct);

        Assert.Equal((ushort)4242, sent.OldTag);

        await harness.Server.WriteAsync(new Rflush(sent.Tag), Ct);
        Rflush reply = await request;

        Assert.Equal(sent.Tag, reply.Tag);
        Assert.Equal(0, harness.Session.Multiplexer.TagsInUse);
        Assert.Null(harness.Session.Multiplexer.Termination);
    }

    /// <summary>
    /// A request that outlives its timeout is flushed exactly as a cancelled one is. The wait for
    /// the <c>Rflush</c> carries the same timeout, so the budget is one a loaded runner's harness
    /// round trip always meets: at 50 ms a slow net8.0 start missed it once, the client then kept
    /// both tags on purpose and threw the other timeout, and the count below read two. The
    /// message pins which timeout fired, so that case is a named failure and not a coincidence.
    /// </summary>
    [Fact]
    public async Task ARequestTimeoutIsFlushedToo()
    {
        await using Harness harness = await Harness.StartAsync(
            options => options with { RequestTimeout = TimeSpan.FromSeconds(1) });

        Task<Rclunk> request = harness.Session.Messages.ClunkAsync(new Tclunk(0, 1), Ct).AsTask();
        Tclunk sent = await harness.Server.ReadAsync<Tclunk>(Ct);

        Tflush flush = await harness.Server.ReadAsync<Tflush>(Ct);

        Assert.Equal(sent.Tag, flush.OldTag);

        await harness.Server.WriteAsync(new Rflush(flush.Tag), Ct);

        TimeoutException timeout = await Assert.ThrowsAsync<TimeoutException>(async () => await request);
        Assert.Contains("was flushed", timeout.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Session.Multiplexer.TagsInUse);
    }

    /// <summary>
    /// The wait for the <c>Rflush</c> carries the session's request timeout. A server that is
    /// connected but never answers used to hold the caller in that wait for ever, because it was
    /// made with <c>CancellationToken.None</c> and no deadline.
    /// </summary>
    [Fact]
    public async Task AnUnansweredRflushTimesOutRatherThanHanging()
    {
        await using Harness harness = await Harness.StartAsync(
            options => options with { RequestTimeout = TimeSpan.FromMilliseconds(50) });

        Task<Rclunk> request = harness.Session.Messages.ClunkAsync(new Tclunk(0, 1), Ct).AsTask();

        // The server reads both frames and answers neither.
        Tclunk sent = await harness.Server.ReadAsync<Tclunk>(Ct);
        Tflush flush = await harness.Server.ReadAsync<Tflush>(Ct);

        Assert.Equal(sent.Tag, flush.OldTag);

        Task finished = await Task.WhenAny(request, Task.Delay(Bound, Ct));

        Assert.Same(request, finished);
        await Assert.ThrowsAsync<TimeoutException>(async () => await request);

        // The Rflush never came, so the server never said it had finished with either tag: the
        // flushed request's stays rented rather than being handed to a request whose reply it
        // could collide with, and the Tflush's own is quarantined for the same reason.
        Assert.Equal(2, harness.Session.Multiplexer.TagsInUse);
    }

    /// <summary>
    /// A server that is merely slow may answer a <c>Tflush</c> after the client has given up on
    /// it. That late <c>Rflush</c> must be recognised and dropped, not met by the unknown-tag rule
    /// that terminates the session — which is what returning the <c>Tflush</c>'s own tag to the
    /// pool on timeout led to, although the server had not confirmed that one either.
    /// <b>Mutation:</b> release the tag in the non-flushable path's cancellation catch and this
    /// test fails: the session terminates and the request after the late <c>Rflush</c> throws.
    /// <para>
    /// The late <c>Rflush</c> also <b>ends</b> the quarantine. It is the server's word that it is
    /// finished with the <c>Tflush</c>'s tag and with <c>oldtag</c>, so both go back to the pool
    /// rather than staying rented for the life of the session.
    /// <b>Mutation:</b> drop the <c>Flushed</c> branch from <c>TagMultiplexer.DeliverAsync</c> and
    /// the tag count below is 2, not 0.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ALateRflushDoesNotTerminateTheSession()
    {
        await using Harness harness = await Harness.StartAsync(
            options => options with { RequestTimeout = TimeSpan.FromMilliseconds(200) });

        Task<Rclunk> abandoned = harness.Session.Messages.ClunkAsync(new Tclunk(0, 1), Ct).AsTask();

        Tclunk sent = await harness.Server.ReadAsync<Tclunk>(Ct);
        Tflush flush = await harness.Server.ReadAsync<Tflush>(Ct);

        Assert.Equal(sent.Tag, flush.OldTag);
        await Assert.ThrowsAsync<TimeoutException>(async () => await abandoned);

        // The server gets round to the Tflush after the client has stopped waiting for it.
        await harness.Server.WriteAsync(new Rflush(flush.Tag), Ct);

        // The reader delivers frames in order, so a request answered after that late Rflush
        // proves the Rflush did not take the session down on its way past.
        Task<Rclunk> next = harness.Session.Messages.ClunkAsync(new Tclunk(0, 2), Ct).AsTask();
        Tclunk second = await harness.Server.ReadAsync<Tclunk>(Ct);

        await harness.Server.WriteAsync(new Rclunk(second.Tag), Ct);
        await next;

        Assert.Null(harness.Session.Multiplexer.Termination);

        // The Rflush confirmed both quarantined tags, so neither is still spent: only this
        // request's own tag was outstanding while it ran, and nothing is outstanding now.
        Assert.Equal(0, harness.Session.Multiplexer.TagsInUse);
    }

    /// <summary>
    /// A server that answers a <c>Tflush</c> with <c>Rerror</c>/<c>Rlerror</c> is not conformant
    /// — flush(5) knows no such reply — but it has answered the <c>Tflush</c>, and it is seen in
    /// the wild. That answer used to release the <c>Tflush</c>'s own tag only, through the
    /// ordinary error path, and leave <c>oldtag</c> rented for the life of the session; and the
    /// caller was handed the server's error in place of its own cancellation. Both tags now go
    /// back to the pool exactly as on an <c>Rflush</c>, the caller sees what an <c>Rflush</c>
    /// would have given it, and the session goes on.
    /// <b>Mutation:</b> drop the <c>NinePException</c> catch from <c>TagMultiplexer.FlushAsync</c>
    /// and the caller sees a <c>NinePException</c> here rather than its cancellation; drop the
    /// error case from the <c>Flushed</c> branch of <c>DeliverAsync</c> as well and the tag count
    /// is 1, not 0.
    /// </summary>
    [Fact]
    public async Task ATflushAnsweredWithAnErrorFreesBothTags()
    {
        await using Harness harness = await Harness.StartAsync();

        using CancellationTokenSource caller = new();
        Task<Rclunk> request = harness.Session.Messages
            .ClunkAsync(new Tclunk(0, 1), caller.Token).AsTask();

        Tclunk sent = await harness.Server.ReadAsync<Tclunk>(Ct);
        await caller.CancelAsync();

        Tflush flush = await harness.Server.ReadAsync<Tflush>(Ct);
        Assert.Equal(sent.Tag, flush.OldTag);
        Assert.Equal(2, harness.Session.Multiplexer.TagsInUse);

        await harness.Server.WriteAsync(new Rlerror(flush.Tag, Errno.EINVAL), Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);

        Assert.Equal(0, harness.Session.Multiplexer.TagsInUse);
        Assert.Equal(0, harness.Session.Multiplexer.Outstanding);
        Assert.Null(harness.Session.Multiplexer.Termination);

        // The session is intact and both numbers are rentable again.
        Task<Rclunk> next = harness.Session.Messages.ClunkAsync(new Tclunk(0, 2), Ct).AsTask();
        Tclunk second = await harness.Server.ReadAsync<Tclunk>(Ct);
        await harness.Server.WriteAsync(new Rclunk(second.Tag), Ct);
        await next;

        Assert.Equal(0, harness.Session.Multiplexer.TagsInUse);
    }

    /// <summary>
    /// The late form of the same non-conformance: the <c>Tflush</c> went unanswered inside the
    /// deadline, both tags were quarantined, and then the server answers it with an error rather
    /// than an <c>Rflush</c>. Like a late <c>Rflush</c>, that is the server's word that it is
    /// finished with both tags, so both are reclaimed and the session is not terminated.
    /// <b>Mutation:</b> drop the error case from the <c>Flushed</c> branch of
    /// <c>TagMultiplexer.DeliverAsync</c> and the tag count below is 2, not 0.
    /// </summary>
    [Fact]
    public async Task ALateErrorReplyToATflushReclaimsBothTags()
    {
        await using Harness harness = await Harness.StartAsync(
            options => options with { RequestTimeout = TimeSpan.FromMilliseconds(200) });

        Task<Rclunk> abandoned = harness.Session.Messages.ClunkAsync(new Tclunk(0, 1), Ct).AsTask();

        Tclunk sent = await harness.Server.ReadAsync<Tclunk>(Ct);
        Tflush flush = await harness.Server.ReadAsync<Tflush>(Ct);

        Assert.Equal(sent.Tag, flush.OldTag);
        await Assert.ThrowsAsync<TimeoutException>(async () => await abandoned);
        Assert.Equal(2, harness.Session.Multiplexer.TagsInUse);

        await harness.Server.WriteAsync(new Rlerror(flush.Tag, Errno.EINVAL), Ct);

        Task<Rclunk> next = harness.Session.Messages.ClunkAsync(new Tclunk(0, 2), Ct).AsTask();
        Tclunk second = await harness.Server.ReadAsync<Tclunk>(Ct);
        await harness.Server.WriteAsync(new Rclunk(second.Tag), Ct);
        await next;

        Assert.Null(harness.Session.Multiplexer.Termination);
        Assert.Equal(0, harness.Session.Multiplexer.TagsInUse);
    }

    /// <summary>
    /// Disposal clunks every fid first, and a clunk that a silent server never answers must not
    /// strand the reader task and the socket. The teardown runs in a <c>finally</c> for that.
    /// </summary>
    [Fact]
    public async Task DisposalCompletesAgainstASilentServer()
    {
        // Long enough that the attach below is not itself flushed when the whole suite is running
        // beside this test, short enough that the disposal it bounds is over in about a second.
        await using Harness harness = await Harness.StartAsync(
            options => options with { RequestTimeout = TimeSpan.FromMilliseconds(500) });

        Task<NinePFid> attaching = harness.Session.AttachAsync(Ct).AsTask();
        Tattach attach = await harness.Server.ReadAsync<Tattach>(Ct);
        await harness.Server.WriteAsync(new Rattach(attach.Tag, new Qid(QidType.QTDIR, 1, 1)), Ct);
        await attaching;

        // From here the server answers nothing at all: not the Tclunk, not the Tflush that
        // cancels it.
        Task disposing = harness.Session.DisposeAsync().AsTask();
        Task finished = await Task.WhenAny(disposing, Task.Delay(Bound, Ct));

        Assert.Same(disposing, finished);
        await disposing;
        Assert.NotNull(harness.Session.Multiplexer.Termination);
    }

    /// <summary>
    /// Disposal against a silent server must cost one deadline, not one per fid. The clunks used
    /// to go out one at a time, each paying <c>2 × RequestTimeout</c> (the clunk, then the
    /// <c>Tflush</c> that cancels it), so nine open fids cost eighteen request timeouts —
    /// eighteen minutes at the defaults.
    /// <para>
    /// The two deadlines are deliberately far apart — <c>RequestTimeout</c> 30 s,
    /// <see cref="ClientOptions.DisposeTimeout"/> 1 s — so that the bound is the only thing that
    /// can end this disposal. With them equal, nine concurrent clunks finished on the request
    /// timeout anyway and the test passed with the bound removed, pinning nothing.
    /// </para>
    /// <b>Mutation:</b> drop the <c>WaitAsync(bound)</c> in <c>NinePSession.DisposeAsync</c> and
    /// the disposal below takes 60 s (a request timeout for each clunk, another for the
    /// <c>Tflush</c> that cancels it) instead of the 1 s the bound gives it.
    /// </summary>
    [Fact]
    public async Task DisposalAgainstASilentServerIsBoundedWhateverTheFidCount()
    {
        // Long enough that nothing here can finish on the request timeout by accident.
        await using Harness harness = await Harness.StartAsync(
            options => options with
            {
                RequestTimeout = TimeSpan.FromSeconds(30),
                DisposeTimeout = TimeSpan.FromSeconds(1),
            });

        Task<NinePFid> attaching = harness.Session.AttachAsync(Ct).AsTask();
        Tattach attach = await harness.Server.ReadAsync<Tattach>(Ct);
        await harness.Server.WriteAsync(new Rattach(attach.Tag, new Qid(QidType.QTDIR, 1, 1)), Ct);
        NinePFid root = await attaching;

        const int Clones = 8;
        for (int made = 0; made < Clones; made++)
        {
            Task<NinePFid> cloning = root.CloneAsync(Ct).AsTask();
            Twalk clone = await harness.Server.ReadAsync<Twalk>(Ct);
            await harness.Server.WriteAsync(new Rwalk(clone.Tag, []), Ct);
            await cloning;
        }

        Assert.Equal(Clones + 1, harness.Session.LiveFids);

        // From here the server answers nothing: neither the nine clunks nor the flushes.
        long started = Stopwatch.GetTimestamp();
        Task disposing = harness.Session.DisposeAsync().AsTask();
        Task finished = await Task.WhenAny(disposing, Task.Delay(Bound, Ct));

        Assert.Same(disposing, finished);
        await disposing;

        TimeSpan spent = Stopwatch.GetElapsedTime(started);

        // Nine clunks that will never be answered, against a 30 s request timeout: without the
        // dispose bound this is 60 s whether they go out together or one at a time. The margin is
        // wide enough for a loaded machine and still an order of magnitude below either.
        Assert.True(spent < TimeSpan.FromSeconds(5), "disposal took " + spent.ToString());
        Assert.NotNull(harness.Session.Multiplexer.Termination);
    }

    /// <summary>
    /// A walk that fails clunks the fid it had cloned, and that cleanup runs against the same
    /// server that just refused the walk. When the peer answers neither the <c>Tclunk</c> nor the
    /// <c>Tflush</c> that cancels it, the cleanup's <see cref="TimeoutException"/> must not take
    /// the place of the error the caller is entitled to — the walk's own <c>ENOENT</c>.
    /// <b>Mutation:</b> remove <c>WalkAsync</c>'s own <c>try</c>/<c>catch</c> around the cleanup
    /// and this test sees the cleanup's <c>TimeoutException</c> instead of the walk's
    /// <c>ENOENT</c>. It does <b>not</b> die when <c>NinePFid.DisposeAsync</c>'s catch is narrowed
    /// — this catch masks that one — so the disposal contract a caller reaches through
    /// <c>await using</c> is pinned by
    /// <see cref="DisposingAFidAgainstASilentServerThrowsNothing"/> instead.
    /// </summary>
    [Fact]
    public async Task AFailedWalkReportsItsOwnErrorWhenTheCleanupClunkTimesOut()
    {
        await using Harness harness = await Harness.StartAsync(
            options => options with { RequestTimeout = TimeSpan.FromMilliseconds(500) });

        Task<NinePFid> attaching = harness.Session.AttachAsync(Ct).AsTask();
        Tattach attach = await harness.Server.ReadAsync<Tattach>(Ct);
        await harness.Server.WriteAsync(new Rattach(attach.Tag, new Qid(QidType.QTDIR, 1, 1)), Ct);
        NinePFid root = await attaching;

        Task<NinePFid> walking = root.WalkAsync(["missing"], Ct).AsTask();

        // The clone is answered; the walk itself is refused.
        Twalk clone = await harness.Server.ReadAsync<Twalk>(Ct);
        Assert.Empty(clone.Wnames);
        await harness.Server.WriteAsync(new Rwalk(clone.Tag, []), Ct);

        Twalk walk = await harness.Server.ReadAsync<Twalk>(Ct);
        Assert.Equal("missing", Assert.Single(walk.Wnames));
        await harness.Server.WriteAsync(new Rlerror(walk.Tag, Errno.ENOENT), Ct);

        // From here the server answers nothing at all, so the cleanup clunk times out and is
        // flushed, and that flush times out too.
        Tclunk clunk = await harness.Server.ReadAsync<Tclunk>(Ct);
        Assert.Equal(clone.NewFid, clunk.Fid);

        NinePException failure = await Assert.ThrowsAsync<NinePException>(async () => await walking);

        Assert.Equal(Errno.ENOENT, failure.Error.Errno);
    }

    /// <summary>
    /// The cleanup clunk of a failed walk is bounded. A peer that has gone silent is the usual
    /// reason a walk fails in the first place, and clunking against it costs a request timeout
    /// plus the <c>Tflush</c> that cancels it — so the caller waited <c>4 × RequestTimeout</c>,
    /// four minutes at the defaults, to be told what the client had known for half of it. The
    /// bound is the session's disposal bound, because this is the same situation seen from one
    /// fid: a clunk nobody is waiting on, against a peer that may not answer.
    /// <b>Mutation:</b> await <c>walked.DisposeAsync()</c> directly instead of
    /// <c>CleanUpAsync</c> and the walk's error arrives about 4 s later, not 200 ms.
    /// </summary>
    [Fact]
    public async Task AFailedWalksCleanupClunkIsBounded()
    {
        await using Harness harness = await Harness.StartAsync(
            options => options with
            {
                RequestTimeout = TimeSpan.FromSeconds(2),
                DisposeTimeout = TimeSpan.FromMilliseconds(200),
            });

        Task<NinePFid> attaching = harness.Session.AttachAsync(Ct).AsTask();
        Tattach attach = await harness.Server.ReadAsync<Tattach>(Ct);
        await harness.Server.WriteAsync(new Rattach(attach.Tag, new Qid(QidType.QTDIR, 1, 1)), Ct);
        NinePFid root = await attaching;

        Task<NinePFid> walking = root.WalkAsync(["missing"], Ct).AsTask();

        Twalk clone = await harness.Server.ReadAsync<Twalk>(Ct);
        await harness.Server.WriteAsync(new Rwalk(clone.Tag, []), Ct);

        Twalk walk = await harness.Server.ReadAsync<Twalk>(Ct);
        await harness.Server.WriteAsync(new Rlerror(walk.Tag, Errno.ENOENT), Ct);

        // From here the server answers nothing: neither the cleanup clunk nor its Tflush.
        Tclunk clunk = await harness.Server.ReadAsync<Tclunk>(Ct);
        Assert.Equal(clone.NewFid, clunk.Fid);

        long started = Stopwatch.GetTimestamp();
        NinePException failure = await Assert.ThrowsAsync<NinePException>(async () => await walking);
        TimeSpan spent = Stopwatch.GetElapsedTime(started);

        // The walk's own error, and promptly: unbounded this is 2 x RequestTimeout = 4 s.
        Assert.Equal(Errno.ENOENT, failure.Error.Errno);
        Assert.True(spent < TimeSpan.FromSeconds(1), "the walk's error arrived after " + spent.ToString());
    }

    /// <summary>
    /// <c>NinePFid.DisposeAsync</c> is documented as safe to call from a <c>finally</c>, and an
    /// <c>await using</c> block is exactly that: a disposal that throws there replaces whatever
    /// the block was really reporting. Against a peer that answers neither the <c>Tclunk</c> nor
    /// the <c>Tflush</c> that cancels it, the disposal must return quietly. Nothing here catches,
    /// so the contract is the assertion — <c>WalkAsync</c>'s own guard cannot mask it, which is
    /// what left this pinned by nothing.
    /// <b>Mutation:</b> narrow the catch in <c>NinePFid.DisposeAsync</c> to
    /// <c>catch (NinePException)</c> and this test fails with the <c>TimeoutException</c> that
    /// catch is there to swallow.
    /// </summary>
    [Fact]
    public async Task DisposingAFidAgainstASilentServerThrowsNothing()
    {
        await using Harness harness = await Harness.StartAsync(
            options => options with { RequestTimeout = TimeSpan.FromMilliseconds(200) });

        Task<NinePFid> attaching = harness.Session.AttachAsync(Ct).AsTask();
        Tattach attach = await harness.Server.ReadAsync<Tattach>(Ct);
        await harness.Server.WriteAsync(new Rattach(attach.Tag, new Qid(QidType.QTDIR, 1, 1)), Ct);
        NinePFid root = await attaching;

        Task<NinePFid> cloning = root.CloneAsync(Ct).AsTask();
        Twalk clone = await harness.Server.ReadAsync<Twalk>(Ct);
        await harness.Server.WriteAsync(new Rwalk(clone.Tag, []), Ct);
        NinePFid cloned = await cloning;

        Assert.Equal(2, harness.Session.LiveFids);

        // From here the server answers nothing: not the Tclunk, not the Tflush that cancels it.
        await using (cloned.ConfigureAwait(false))
        {
        }

        // The disposal returned rather than throwing, and gave the fid number back on its way.
        Assert.Equal(1, harness.Session.LiveFids);
    }

    /// <summary>A token that has already fired never puts the request on the wire at all.</summary>
    [Fact]
    public async Task AnAlreadyCancelledTokenSendsNothing()
    {
        await using Harness harness = await Harness.StartAsync();

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await harness.Session.Messages.ClunkAsync(new Tclunk(0, 1), cancelled.Token));

        Assert.Equal(0, harness.Session.Multiplexer.TagsInUse);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(NinePSession session, FakeNinePServer server)
        {
            Session = session;
            Server = server;
        }

        public NinePSession Session { get; }

        public FakeNinePServer Server { get; }

        public static async Task<Harness> StartAsync(Func<ClientOptions, ClientOptions>? tune = null)
        {
            (INinePConnection wire, FakeNinePServer server) = FakeNinePServer.CreatePair();
            ClientOptions options = new() { Dialects = [Dialect.P9_2000_L] };

            Task<NinePSession> connecting = NinePClient
                .ConnectAsync(wire, tune is null ? options : tune(options), CancellationToken.None)
                .AsTask();

            await server.NegotiateAsync(Constants.Version9P2000L);
            return new Harness(await connecting, server);
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
