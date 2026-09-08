using System.Collections.Concurrent;
using NineP.Protocol;
using NineP.Protocol.Auth;

namespace NineP.Server.Internal;

/// <summary>
/// One connection's tag table (§6.5, reference §8 rule 6). A tag is the client's handle on an
/// outstanding request, so a duplicate is refused and the <b>new</b> request is dropped: the
/// pending one keeps the tag it was answered under, and answering both would give the client two
/// replies it cannot tell apart.
/// </summary>
internal sealed class TagTable
{
    private readonly ConcurrentDictionary<ushort, PendingRequest> _pending = new();

    /// <summary>Requests currently in flight on this connection.</summary>
    public int Count => _pending.Count;

    /// <summary>
    /// Registers a request under its tag. <c>NOTAG</c> outside <c>Tversion</c> is accepted as an
    /// ordinary tag, which reference §8 rule 6 requires and which costs nothing to honour.
    /// </summary>
    /// <param name="tag">The tag the client sent.</param>
    /// <param name="type">The T-message type.</param>
    /// <returns>The request, once it is registered.</returns>
    /// <exception cref="NinePException">That tag is already pending.</exception>
    public PendingRequest Begin(ushort tag, MessageType type)
    {
        PendingRequest request = new(tag, type);

        return _pending.TryAdd(tag, request)
            ? request
            : throw new NinePException(NinePError.FromEname("duplicate tag"));
    }

    /// <summary>Looks up an outstanding request.</summary>
    /// <param name="tag">The tag to look for.</param>
    /// <param name="request">The request, when it is still pending.</param>
    /// <returns>True when the tag names something in flight.</returns>
    public bool TryGet(ushort tag, out PendingRequest? request) => _pending.TryGetValue(tag, out request);

    /// <summary>
    /// Frees the tag <b>this</b> request holds and disposes its cancellation source. It happens
    /// once the request's fate is settled and not before: either its reply has been handed to the
    /// writer, or an <c>Rflush</c> has said the reply will never come (reference §5.3). Until one
    /// of those, the client may not reuse the tag and neither may this table.
    /// </summary>
    /// <remarks>
    /// The removal is by identity and not by number, which matters because a client may reuse a
    /// tag the instant it has the reply (reference §5.3) and this method is called twice for every
    /// request — once where the reply is queued and once as the worker's backstop. Removing by
    /// number let the late backstop of a finished request evict the <b>next</b> request that had
    /// already claimed the same tag, and dispose its <c>Cts</c> underneath it: that request's
    /// handler then died on an <c>ObjectDisposedException</c> the worker treats as a cancelled
    /// session, and no reply was ever sent for a tag the client was still waiting on. A client
    /// that pipelines and recycles tags — which is every client — hung there for good.
    /// </remarks>
    /// <param name="request">The request whose tag is being freed.</param>
    /// <returns>True when this call was the one that took the entry out of the table.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    public bool Release(PendingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool removed = _pending.TryRemove(new KeyValuePair<ushort, PendingRequest>(request.Tag, request));

        // The source is disposed whether or not this call is the one that removed the entry:
        // Flush deliberately leaves the entry's source alive for the worker still running under
        // it, and the worker's backstop -- which is this call -- is what finally lets it go.
        request.Cts.Dispose();
        return removed;
    }

    /// <summary>
    /// Flushes a tag: claims the right to suppress its reply, <b>frees the tag</b>, and cancels
    /// the handler, best effort.
    /// </summary>
    /// <remarks>
    /// The tag is freed here, before the caller queues the <c>Rflush</c>, and that order is the
    /// point of this method. Reference §5.3: "the client must wait until it gets the
    /// <c>Rflush</c>… at which point <c>oldtag</c> may be reused" — and Linux v9fs does reuse it,
    /// on the very next request, lowest number first. Once this call has won the CAS the flushed
    /// request will never be answered, so nothing is waiting for that tag any more; leaving the
    /// entry in the table until the cancelled handler had actually unwound made the client's legal
    /// reuse draw <c>"duplicate tag"</c> and be dropped (§8 rule 6) for as long as the handler
    /// took to notice. It is the same defect as the one the reply path was fixed for, on the other
    /// side of the same rule.
    /// <para>
    /// The entry is removed <b>by identity</b>, so a successor that has already claimed the number
    /// is never evicted, and the request's <c>Cts</c> is deliberately <b>not</b> disposed: the
    /// worker is still inside the handler and running under a token linked to it. The worker's
    /// <c>finally</c> disposes it and finds nothing left to remove, which is exactly what it is
    /// for.
    /// </para>
    /// </remarks>
    /// <param name="tag">The tag being flushed.</param>
    /// <returns>True when this call is the one that suppressed a reply that had not gone out.</returns>
    public bool Flush(ushort tag)
    {
        if (!_pending.TryGetValue(tag, out PendingRequest? request))
        {
            // flush(5): an unknown oldtag is legal and is answered with Rflush all the same.
            return false;
        }

        bool suppressed = request.TryFlush();

        if (suppressed)
        {
            _pending.TryRemove(new KeyValuePair<ushort, PendingRequest>(tag, request));
        }

        try
        {
            request.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request finished and freed its tag between the lookup and the cancel.
        }

        return suppressed;
    }

    /// <summary>Cancels and forgets everything in flight, which a mid-session <c>Tversion</c> does.</summary>
    public void Clear()
    {
        foreach (ushort tag in _pending.Keys)
        {
            if (!_pending.TryRemove(tag, out PendingRequest? request))
            {
                continue;
            }

            request.TryFlush();

            try
            {
                request.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished; nothing to cancel.
            }

            request.Cts.Dispose();
        }
    }
}
