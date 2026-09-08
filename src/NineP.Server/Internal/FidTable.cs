using System.Collections.Concurrent;
using NineP.Protocol;

namespace NineP.Server.Internal;

/// <summary>
/// One connection's fid table (§6.5, reference §8 rule 7). It is bounded because a fid costs a
/// handler and an open file on the server, and an unbounded table is a memory exhaustion a client
/// reaches with a loop; the cap is one condition with one answer, <c>"too many fids"</c> in
/// 9P2000 and .u and <c>ENFILE</c> in .L, and the connection stays up.
/// </summary>
internal sealed class FidTable : IAsyncDisposable
{
    private readonly ConcurrentDictionary<uint, FidEntry> _fids = new();
    private readonly int _capacity;
    private readonly PathState? _paths;
    private readonly object _bindingGate = new();

    /// <summary>Creates a table bounded by the configured cap.</summary>
    /// <param name="capacity">The most fids one connection may hold.</param>
    /// <param name="paths">Optional server-wide live ancestry registry.</param>
    public FidTable(int capacity, PathState? paths = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
        _paths = paths;
    }

    /// <summary>How many fids the connection currently holds.</summary>
    public int Count => _fids.Count;

    /// <summary>The most fids this connection may hold.</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Binds a fresh fid. <c>NOFID</c> is never a fid a client may bind: it is the value that
    /// means "no fid at all" in <c>Tattach</c>, and accepting it would make an attach that
    /// authenticates indistinguishable from one that does not.
    /// </summary>
    /// <param name="entry">The entry to bind, carrying the number the client chose.</param>
    /// <returns>The entry, once it is in the table.</returns>
    /// <exception cref="NinePException">The number is in use, is NOFID, or the cap is reached.</exception>
    public FidEntry Bind(FidEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Fid == Constants.NOFID)
        {
            throw new NinePException(NinePError.FromEname("duplicate fid"));
        }

        lock (_bindingGate)
        {
            if (_fids.ContainsKey(entry.Fid))
            {
                throw new NinePException(NinePError.FromEname("duplicate fid"));
            }

            if (_fids.Count >= _capacity)
            {
                throw new NinePException(NinePError.FromErrno(Errno.ENFILE));
            }

            _fids[entry.Fid] = entry;
            entry.Paths = _paths;
            _paths?.Add(entry);
            return entry;
        }
    }

    /// <summary>Locks all existing operands, rejecting a fid retired while this request waited.</summary>
    /// <param name="fids">The existing fid numbers touched by a request.</param>
    /// <param name="cancellationToken">Cancels waiting without invalidating a live operation.</param>
    /// <returns>A lease released when the request finishes.</returns>
    public async ValueTask<FidLease> AcquireAsync(uint[] fids, CancellationToken cancellationToken)
    {
        FidEntry[] entries = fids.Distinct().Order().Select(Get).ToArray();
        int held = 0;
        try
        {
            foreach (FidEntry entry in entries)
            {
                await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                held++;
                if (!_fids.TryGetValue(entry.Fid, out FidEntry? current) || !ReferenceEquals(current, entry))
                {
                    throw new NinePException(NinePError.FromErrno(Errno.EBADF));
                }
            }

            return new FidLease(entries);
        }
        catch
        {
            for (int at = held - 1; at >= 0; at--)
            {
                entries[at].Gate.Release();
            }

            throw;
        }
    }

    /// <summary>Looks a fid up.</summary>
    /// <param name="fid">The number the client sent.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="NinePException">The connection holds no such fid.</exception>
    public FidEntry Get(uint fid) =>
        _fids.TryGetValue(fid, out FidEntry? entry)
            ? entry
            : throw new NinePException(NinePError.FromErrno(Errno.EBADF));

    /// <summary>Looks a fid up without failing.</summary>
    /// <param name="fid">The number the client sent.</param>
    /// <param name="entry">The entry, when there is one.</param>
    /// <returns>True when the connection holds that fid.</returns>
    public bool TryGet(uint fid, out FidEntry? entry) => _fids.TryGetValue(fid, out entry);

    /// <summary>
    /// Refuses a number that must be fresh. A <c>newfid</c> equal to <c>fid</c> is the one legal
    /// exception, which walk(5) states explicitly.
    /// </summary>
    /// <param name="newFid">The number that must be free.</param>
    /// <param name="fid">The fid being walked from.</param>
    /// <exception cref="NinePException">The number is already in use.</exception>
    public void RequireFree(uint newFid, uint fid)
    {
        if (newFid != fid && _fids.ContainsKey(newFid))
        {
            throw new NinePException(NinePError.FromEname("duplicate fid"));
        }
    }

    /// <summary>Takes a fid out of the table without releasing it.</summary>
    /// <param name="fid">The number to forget.</param>
    /// <param name="entry">The entry that was removed, which the caller then releases.</param>
    /// <returns>True when the fid was there.</returns>
    // CA2000: the point of this method is to hand ownership to the caller, which releases the
    // entry through ReleaseAsync — disposing it here would close the open file the caller is
    // about to answer a Tremove from.
#pragma warning disable CA2000
    public bool Remove(uint fid, out FidEntry? entry)
    {
        if (!_fids.TryRemove(fid, out entry))
        {
            return false;
        }

        return true;
    }
#pragma warning restore CA2000

    /// <summary>
    /// Clunks every fid, which is what a mid-session <c>Tversion</c> and a closing connection both
    /// do (reference §5.1). There is no client waiting on any of these clunks, so a handler that
    /// refuses one has nobody to tell: the refusal is dropped here rather than left to abandon the
    /// fids after it, which is the one place reference §8 rule 26 has no reply to put it in.
    /// </summary>
    /// <param name="cancellationToken">Cancels the release.</param>
    /// <param name="finalize">The server finalizer, or ordinary handler release for standalone tables.</param>
    /// <returns>A task that completes when every handler has been released.</returns>
    public async ValueTask ClearAsync(
        CancellationToken cancellationToken, Func<FidEntry, CancellationToken, ValueTask>? finalize = null)
    {
        foreach (uint fid in _fids.Keys)
        {
            // CA2000: ReleaseAsync disposes the entry after telling the handler, which is the
            // order clunk(5) requires; the analyzer cannot see through the call.
#pragma warning disable CA2000
            if (!Remove(fid, out FidEntry? entry))
#pragma warning restore CA2000
            {
                continue;
            }

            try
            {
                if (finalize is null)
                {
                    await ReleaseAsync(entry!, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await finalize(entry!, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (NinePException)
            {
                // The fid is gone and the handler has been told; there is no reply to carry this.
            }
        }
    }

    /// <summary>
    /// Releases one entry: the handler is told, then the entry's own resources go. A handler that
    /// refuses the clunk does not keep the fid alive — clunk(5) frees it whatever the reply says —
    /// but its error <b>is</b> the reply (reference §8 rule 26): it is rethrown once the entry has
    /// been disposed, and the dispatcher answers <c>Rerror</c> / <c>Rlerror</c> with it instead of
    /// the <c>Rclunk</c> that used to swallow it. A handler whose flush of the last write failed
    /// has to be able to say so.
    /// </summary>
    /// <param name="entry">The entry to release.</param>
    /// <param name="cancellationToken">Cancels the release.</param>
    /// <returns>A task that completes when the entry is gone.</returns>
    /// <exception cref="NinePException">The handler refused the clunk; the fid is freed anyway.</exception>
    public static async ValueTask ReleaseAsync(FidEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        bool wasOpen = entry.State == FidState.Open;
        NinePException? refusal = null;

        try
        {
            await entry.Handler.ClunkAsync(wasOpen, cancellationToken).ConfigureAwait(false);
        }
        catch (NinePException failure)
        {
            refusal = failure;
        }
        finally
        {
            await entry.DisposeAsync().ConfigureAwait(false);
        }

        if (refusal is not null)
        {
            throw refusal;
        }
    }

    /// <summary>Releases every fid the connection still holds.</summary>
    /// <returns>A task that completes when the table is empty.</returns>
    public ValueTask DisposeAsync() => ClearAsync(CancellationToken.None);
}
