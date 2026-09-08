using System.Collections.Concurrent;
using NineP.Protocol;

namespace NineP.Client.Internal;

/// <summary>
/// The bounded tag pool of the architecture §6: 65535 tags, because <c>NOTAG</c> is reserved for
/// <c>Tversion</c>. A tag is not returned to the pool when its request is cancelled — only when the
/// server has confirmed with <c>Rflush</c> that it is finished with it (reference §8 rule 14).
/// </summary>
internal sealed class TagPool
{
    /// <summary>The number of tags a connection may have outstanding at once.</summary>
    public const int Capacity = Constants.NOTAG;

    private readonly ConcurrentQueue<ushort> _free = new();
    private int _outstanding;

    /// <summary>Creates a full pool.</summary>
    public TagPool()
    {
        for (int tag = 0; tag < Capacity; tag++)
        {
            _free.Enqueue((ushort)tag);
        }
    }

    /// <summary>How many tags are currently rented.</summary>
    public int Outstanding => Volatile.Read(ref _outstanding);

    /// <summary>Takes a free tag.</summary>
    /// <param name="tag">The tag, when one was free.</param>
    /// <returns>False when all 65535 tags are outstanding.</returns>
    public bool TryRent(out ushort tag)
    {
        if (!_free.TryDequeue(out tag))
        {
            return false;
        }

        Interlocked.Increment(ref _outstanding);
        return true;
    }

    /// <summary>Gives a tag back, so a later request may use it.</summary>
    /// <param name="tag">The tag to release.</param>
    public void Return(ushort tag)
    {
        Interlocked.Decrement(ref _outstanding);
        _free.Enqueue(tag);
    }
}
