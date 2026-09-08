namespace NineP.Server.Internal;

/// <summary>Holds the existing fids touched by a request in ascending numeric order.</summary>
internal sealed class FidLease(FidEntry[] entries) : IDisposable
{
    private int _disposed;

    /// <summary>The entries protected for the lifetime of this request.</summary>
    public IReadOnlyList<FidEntry> Entries => entries;

    /// <summary>Releases the request's gates in reverse order.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        for (int at = entries.Length - 1; at >= 0; at--)
        {
            entries[at].Gate.Release();
        }
    }
}
