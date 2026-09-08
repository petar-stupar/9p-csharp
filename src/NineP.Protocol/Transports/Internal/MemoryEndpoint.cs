using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Threading.Channels;

namespace NineP.Protocol.Transports.Internal;

/// <summary>One end's view of why the connection closed, published for the other end to read.</summary>
internal sealed class MemoryEndpoint
{
    private int _reason = -1;

    /// <summary>The reason this end gave when it closed, or null while it is still open.</summary>
    public CloseReason? Reason
    {
        get
        {
            int value = Volatile.Read(ref _reason);
            return value < 0 ? null : (CloseReason)value;
        }
    }

    /// <summary>Records the reason once; a second close does not overwrite the first.</summary>
    /// <param name="reason">Why this end is closing.</param>
    /// <returns>True when this call was the one that recorded it.</returns>
    public bool TryClose(CloseReason reason) =>
        Interlocked.CompareExchange(ref _reason, (int)reason, -1) == -1;
}
