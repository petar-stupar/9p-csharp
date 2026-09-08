using NineP.Protocol;
using NineP.Server.Internal;
using Xunit;

namespace NineP.Server.Tests;

/// <summary>
/// Reference §8 rule 6 and §6.6: what a tag means while a request is in flight, and the compare
/// and swap that lets exactly one of "answer it" and "suppress it" win.
/// </summary>
public sealed class TagTableTests
{
    /// <summary>
    /// Rule 46: a tag that is already pending draws <c>"duplicate tag"</c> and the <b>new</b>
    /// request is dropped — the pending one keeps the tag and is untouched.
    /// </summary>
    [Fact]
    public void DuplicateTagRejected()
    {
        TagTable table = new();
        PendingRequest first = table.Begin(3, MessageType.Tclunk);

        NinePException duplicate =
            Assert.Throws<NinePException>(() => table.Begin(3, MessageType.Tstat));

        Assert.Equal("duplicate tag", duplicate.Error.Ename);
        Assert.Equal(Errno.EINVAL, duplicate.Error.Errno);

        // The pending request is untouched: it still holds the tag and is still running.
        Assert.True(table.TryGet(3, out PendingRequest? pending));
        Assert.Same(first, pending);
        Assert.Equal(RequestState.Running, first.State);
    }

    /// <summary>Rule 47: <c>NOTAG</c> outside <c>Tversion</c> is accepted as an ordinary tag.</summary>
    [Fact]
    public void NotagIsOrdinaryOutsideVersion()
    {
        TagTable table = new();
        PendingRequest request = table.Begin(Constants.NOTAG, MessageType.Tclunk);

        Assert.Equal(Constants.NOTAG, request.Tag);
        Assert.True(table.TryGet(Constants.NOTAG, out _));

        // And it is a tag like any other, so a second one under it is still a duplicate.
        Assert.Throws<NinePException>(() => table.Begin(Constants.NOTAG, MessageType.Tstat));
    }

    /// <summary>
    /// §6.6: exactly one of "send the reply" and "suppress it" wins. Both are compare-and-swap on
    /// the same word, so a flush that arrives after the reply was claimed changes nothing.
    /// </summary>
    [Fact]
    public void CompletionIsExactlyOnce()
    {
        TagTable table = new();
        PendingRequest answered = table.Begin(1, MessageType.Tread);

        Assert.True(answered.TryComplete());
        Assert.False(answered.TryComplete());
        Assert.False(answered.TryFlush());
        Assert.Equal(RequestState.Completed, answered.State);

        PendingRequest flushed = table.Begin(2, MessageType.Tread);

        Assert.True(table.Flush(2));
        Assert.False(flushed.TryComplete());
        Assert.Equal(RequestState.Flushed, flushed.State);
        Assert.True(flushed.Cts.IsCancellationRequested);
    }

    /// <summary>flush(5): an unknown tag is legal, suppresses nothing, and is not an error here.</summary>
    [Fact]
    public void FlushingAnUnknownTagSuppressesNothing()
    {
        TagTable table = new();

        Assert.False(table.Flush(42));
        Assert.Equal(0, table.Count);
    }

    /// <summary>A tag is free only once its reply has been handed to the writer (reference §5.3).</summary>
    [Fact]
    public void ReleaseFreesTheTagForReuse()
    {
        TagTable table = new();
        PendingRequest pending = table.Begin(5, MessageType.Tstat);

        Assert.True(table.Release(pending));
        Assert.Equal(0, table.Count);

        table.Begin(5, MessageType.Tstat);
        Assert.Equal(1, table.Count);
    }

    /// <summary>
    /// A release frees the tag <b>that request</b> holds and never whatever has claimed the number
    /// since. Every request is released twice — once where its reply is queued, once as the
    /// worker's backstop — and the client may reuse the tag between the two, because a tag is free
    /// the moment its reply is on the wire (reference §5.3). Releasing by number instead evicted
    /// the new request and disposed its <c>Cts</c> underneath its own handler, which then died on
    /// an <see cref="ObjectDisposedException"/> the worker reads as a cancelled session: no reply
    /// was ever sent, and a client that had one outstanding waited for ever. That is what a full
    /// test run of this repository once did for 1 h 36 min at 0% CPU.
    /// <b>Mutation:</b> remove by tag rather than by identity and the second request below is gone
    /// from the table and its <c>Cts</c> unusable.
    /// </summary>
    [Fact]
    public void ALateReleaseDoesNotEvictTheNextRequestOnTheSameTag()
    {
        TagTable table = new();

        PendingRequest first = table.Begin(9, MessageType.Twalk);
        Assert.True(table.Release(first));

        // The client saw the reply and reused the tag; the finished request's backstop has not run.
        PendingRequest second = table.Begin(9, MessageType.Twalk);
        Assert.False(table.Release(first));

        Assert.True(table.TryGet(9, out PendingRequest? still));
        Assert.Same(second, still);

        // The surviving request's cancellation source is the one its handler is running under.
        using CancellationTokenSource work = CancellationTokenSource.CreateLinkedTokenSource(second.Cts.Token);
        Assert.False(work.IsCancellationRequested);

        Assert.True(table.Release(second));
        Assert.Equal(0, table.Count);
    }

    /// <summary>
    /// A flush that suppresses a reply frees the tag <b>there and then</b>, because that is the
    /// instant reference §5.3 hands it back to the client: "the client must wait until it gets the
    /// <c>Rflush</c>… at which point <c>oldtag</c> may be reused". Nothing will ever be answered
    /// under it again, so holding it until the cancelled handler unwound refused the client's own
    /// legal reuse as a duplicate (§8 rule 6).
    /// <b>Mutation:</b> drop the <c>TryRemove</c> from <c>TagTable.Flush</c> and the
    /// <c>Begin</c> below throws <c>"duplicate tag"</c>.
    /// </summary>
    [Fact]
    public void AFlushThatSuppressesAReplyFreesTheTagAtOnce()
    {
        TagTable table = new();
        PendingRequest flushed = table.Begin(3, MessageType.Tread);

        Assert.True(table.Flush(3));
        Assert.Equal(0, table.Count);
        Assert.Equal(RequestState.Flushed, flushed.State);

        // The client had its Rflush and reused the tag while the flushed handler was still
        // running: the worker's backstop must not evict the successor or disturb its handler.
        PendingRequest reused = table.Begin(3, MessageType.Twalk);

        Assert.False(table.Release(flushed));
        Assert.True(table.TryGet(3, out PendingRequest? still));
        Assert.Same(reused, still);

        using CancellationTokenSource work = CancellationTokenSource.CreateLinkedTokenSource(reused.Cts.Token);
        Assert.False(work.IsCancellationRequested);
    }

    /// <summary>A mid-session <c>Tversion</c> cancels and forgets everything in flight.</summary>
    [Fact]
    public void ClearCancelsEverythingInFlight()
    {
        TagTable table = new();
        PendingRequest running = table.Begin(9, MessageType.Tread);

        table.Clear();

        Assert.Equal(0, table.Count);
        Assert.Equal(RequestState.Flushed, running.State);
    }
}
