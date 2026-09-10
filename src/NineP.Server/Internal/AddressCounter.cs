namespace NineP.Server.Internal;

/// <summary>
/// Live connections per peer address (reference §8 rule 40), so that one host cannot hold every
/// slot of <see cref="NineP.Protocol.Limits.MaxConnectionsPerListener"/> and lock everyone else
/// out. Unlike the listener-wide cap, which is taken before the accept and leaves an excess
/// connection in the kernel's backlog, this one can only be applied <em>after</em> the accept:
/// the address is not known until then. The excess connection is therefore closed at once, with
/// <see cref="NineP.Protocol.Transports.CloseReason.ResourceLimit"/>.
/// </summary>
/// <remarks>
/// It lives in the server rather than in a transport because every transport has the same problem
/// and only the server sees them all; the tls and ws listeners are TCP listeners underneath, and
/// a user-written transport gets the protection for free. The table holds only addresses with a
/// live connection, so it is bounded by the listener's own connection cap.
/// </remarks>
internal sealed class AddressCounter(int limit)
{
    private readonly Dictionary<string, int> _held = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>True when this counter refuses nothing.</summary>
    public bool Disabled => limit <= 0;

    /// <summary>Takes a slot for an address.</summary>
    /// <param name="address">The peer's address; null is never refused.</param>
    /// <param name="firstRefusal">True when this is the first refusal since the address had room.</param>
    /// <returns>True when the connection may be served.</returns>
    public bool TryHold(string? address, out bool firstRefusal)
    {
        firstRefusal = false;
        if (Disabled || address is null)
        {
            return true;
        }

        lock (_gate)
        {
            int held = _held.TryGetValue(address, out int current) ? current : 0;
            if (held >= limit)
            {
                // One record per address per episode: a flood is one event, not one per packet.
                firstRefusal = _reported.Add(address);
                return false;
            }

            _held[address] = held + 1;
            return true;
        }
    }

    /// <summary>Returns the slot a connection held.</summary>
    /// <param name="address">The address the slot was taken for; null took none.</param>
    public void Release(string? address)
    {
        if (Disabled || address is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_held.TryGetValue(address, out int held))
            {
                return;
            }

            if (held <= 1)
            {
                _held.Remove(address);
                _reported.Remove(address);
                return;
            }

            _held[address] = held - 1;
        }
    }

    /// <summary>Connections currently held for an address; for tests and diagnostics.</summary>
    /// <param name="address">The address to report.</param>
    /// <returns>The count, or zero when the address holds nothing.</returns>
    public int HeldFor(string address)
    {
        lock (_gate)
        {
            return _held.TryGetValue(address, out int held) ? held : 0;
        }
    }
}
