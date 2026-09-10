namespace NineP.Server.Internal;

/// <summary>
/// Per-address budget of failed or abandoned authentications (reference §8 rule 40). Past it a
/// <c>Tauth</c> from that address is refused before the authenticator is asked, so the credential
/// exchange — a PBKDF2 derivation for <c>PasswordAuthenticator</c>, deliberately as expensive for
/// an unknown user as for a known one — is never paid for by a peer that is guessing.
/// </summary>
/// <remarks>
/// The table is server-wide, because a budget one connection can reset by reconnecting is no
/// budget. It is bounded twice: an entry older than the window is pruned when the address is next
/// seen or when the table is swept, and a full table evicts its oldest entry, so the tracking
/// cannot itself become the memory a flood is aiming at. A connection whose transport reports no
/// address — the in-memory transport — is not throttled: there is nothing to attribute failures to.
/// </remarks>
internal sealed class AuthThrottle(int budget, TimeSpan window, TimeProvider clock)
{
    /// <summary>Addresses tracked at once; beyond it the oldest entry is evicted.</summary>
    public const int MaxTrackedAddresses = 16384;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock
        ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>True when this throttle refuses nothing.</summary>
    public bool Disabled => budget <= 0 || window <= TimeSpan.Zero;

    /// <summary>Whether an address may attempt an authentication now.</summary>
    /// <param name="address">The peer's address; null is never throttled.</param>
    /// <returns>True when the attempt may proceed.</returns>
    public bool MayAttempt(string? address)
    {
        if (Disabled || address is null)
        {
            return true;
        }

        lock (_gate)
        {
            return !IsOverBudget(address, _clock.GetUtcNow());
        }
    }

    /// <summary>
    /// Records one authentication begun. It is counted at <c>Tauth</c> rather than when the
    /// exchange fails, because an exchange can end in several places — a refused credential, a
    /// client that gives up, a connection that drops — and only some of them reach the server as
    /// an error it could attribute. An attempt that reaches a successful attach is cleared by
    /// <see cref="RecordSuccess"/>, so what the budget actually counts is attempts that did not
    /// succeed.
    /// </summary>
    /// <param name="address">The peer's address; null is not tracked.</param>
    public void RecordAttempt(string? address)
    {
        if (Disabled || address is null)
        {
            return;
        }

        lock (_gate)
        {
            DateTimeOffset now = _clock.GetUtcNow();
            if (_entries.TryGetValue(address, out Entry entry) && now - entry.First < window)
            {
                _entries[address] = entry with { Failures = entry.Failures + 1 };
                return;
            }

            Prune(now);
            _entries[address] = new Entry(now, 1);
        }
    }

    /// <summary>Clears an address's failures; a success is what ends a lockout.</summary>
    /// <param name="address">The peer's address; null is not tracked.</param>
    public void RecordSuccess(string? address)
    {
        if (Disabled || address is null)
        {
            return;
        }

        lock (_gate)
        {
            _entries.Remove(address);
        }
    }

    private bool IsOverBudget(string address, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(address, out Entry entry))
        {
            return false;
        }

        if (now - entry.First >= window)
        {
            _entries.Remove(address);
            return false;
        }

        return entry.Failures >= budget;
    }

    private void Prune(DateTimeOffset now)
    {
        if (_entries.Count < MaxTrackedAddresses)
        {
            return;
        }

        foreach (KeyValuePair<string, Entry> pair in _entries.ToArray())
        {
            if (now - pair.Value.First >= window)
            {
                _entries.Remove(pair.Key);
            }
        }

        // Every entry is inside its window, so the table is genuinely full of live offenders.
        // The oldest is the one closest to expiring anyway, and dropping it bounds the table
        // rather than letting the defence become the leak.
        if (_entries.Count >= MaxTrackedAddresses)
        {
            string oldest = _entries.MinBy(pair => pair.Value.First).Key;
            _entries.Remove(oldest);
        }
    }

    private readonly record struct Entry(DateTimeOffset First, int Failures);
}
