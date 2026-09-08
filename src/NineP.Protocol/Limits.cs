using System.Globalization;

namespace NineP.Protocol;

/// <summary>
/// Every configurable bound of the workspace architecture §4, with the defaults this workspace
/// ships. The record is immutable: a server or client is handed one at construction and it does
/// not change under it.
/// </summary>
public sealed record Limits
{
    /// <summary>The limits every server and client uses unless the caller overrides them.</summary>
    public static Limits Default { get; } = new();

    /// <summary>The largest msize this side will negotiate. Default 1 MiB.</summary>
    public uint MaxMsize { get; init; } = 1024 * 1024;

    /// <summary>
    /// The smallest msize this side will serve; below it a server answers <c>Rversion "unknown"</c>
    /// rather than an <c>Rerror</c>, which version(5) forbids. Default 4096.
    /// </summary>
    public uint MinMsize { get; init; } = 4096;

    /// <summary>
    /// The frame cap that applies until <c>Tversion</c> has been answered. It is a small constant,
    /// never the configured maximum: an unauthenticated peer must not be able to make a connection
    /// reserve a megabyte by lying in the size field. Default 8192.
    /// </summary>
    public uint PreNegotiationFrameCap { get; init; } = 8192;

    /// <summary>Fids one connection may hold; beyond it "too many fids" / ENFILE. Default 65536.</summary>
    public int MaxFidsPerConnection { get; init; } = 65536;

    /// <summary>Requests in flight per connection, including the flush reserve. Default 256.</summary>
    /// <remarks>Excess ordinary requests receive EAGAIN; Tflush uses its reserved capacity.</remarks>
    public int MaxInFlightPerConnection { get; init; } = 256;

    /// <summary>Ordinary requests in flight across every connection of one listener. Default 4096.</summary>
    /// <remarks>Excess ordinary requests receive EAGAIN; Tflush does not consume this budget.</remarks>
    public int MaxInFlightPerListener { get; init; } = 4096;

    /// <summary>
    /// Slots of the per-connection budget only <c>Tflush</c> may use, so a client that filled its
    /// window can still cancel what is in it. Default 8.
    /// </summary>
    public int FlushReservePerConnection { get; init; } = 8;

    /// <summary>Accepted connections per listener. Default 1024.</summary>
    public int MaxConnectionsPerListener { get; init; } = 1024;

    /// <summary>How long a connection may take to deliver a complete frame header. Default 30 s.</summary>
    public TimeSpan ReadHeaderTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Idle timeout; <see cref="TimeSpan.Zero"/> disables it. Default Zero.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.Zero;

    /// <summary>The longest legal file-name component in bytes. Default 255.</summary>
    public int MaxNameLength { get; init; } = Constants.MaxNameLength;

    /// <summary>Bytes an afid exchange may carry in each direction. Default 64 KiB.</summary>
    public int MaxAuthBytes { get; init; } = 64 * 1024;

    /// <summary>The wall-clock budget for one afid exchange. Default 30 s.</summary>
    public TimeSpan AuthTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> when the record is internally inconsistent,
    /// so a misconfiguration fails at construction rather than under load.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is out of range or contradicts another.</exception>
    public void Validate()
    {
        Positive(MaxMsize, nameof(MaxMsize));
        Positive(MinMsize, nameof(MinMsize));
        Positive(PreNegotiationFrameCap, nameof(PreNegotiationFrameCap));
        Positive(MaxFidsPerConnection, nameof(MaxFidsPerConnection));
        Positive(MaxInFlightPerConnection, nameof(MaxInFlightPerConnection));
        Positive(MaxInFlightPerListener, nameof(MaxInFlightPerListener));
        Positive(MaxConnectionsPerListener, nameof(MaxConnectionsPerListener));
        Positive(MaxNameLength, nameof(MaxNameLength));
        Positive(MaxAuthBytes, nameof(MaxAuthBytes));

        AtLeast(MaxMsize, MinMsize, nameof(MaxMsize), "the minimum msize");
        AtLeast(MinMsize, (uint)Constants.HDRSZ + 1, nameof(MinMsize), "one header plus a byte");
        AtLeast(PreNegotiationFrameCap, (uint)Constants.HDRSZ + 1, nameof(PreNegotiationFrameCap), "one header plus a byte");
        AtLeast(MaxInFlightPerListener, MaxInFlightPerConnection, nameof(MaxInFlightPerListener), "the per-connection bound");
        AtLeast(MaxInFlightPerConnection, FlushReservePerConnection + 1, nameof(MaxInFlightPerConnection), "the flush reserve plus a slot");

        if (FlushReservePerConnection < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FlushReservePerConnection),
                FlushReservePerConnection,
                "at least one slot must be reserved for Tflush");
        }

        NotNegative(ReadHeaderTimeout, nameof(ReadHeaderTimeout));
        NotNegative(IdleTimeout, nameof(IdleTimeout));
        NotNegative(AuthTimeout, nameof(AuthTimeout));

        if (MaxNameLength > Constants.MaxNameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxNameLength), MaxNameLength, "a name may never exceed 255 bytes");
        }
    }

    private static void Positive(long value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "must be positive");
        }
    }

    private static void AtLeast(long value, long floor, string name, string what)
    {
        if (value < floor)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                string.Format(CultureInfo.InvariantCulture, "must be at least {0} ({1})", floor, what));
        }
    }

    private static void NotNegative(TimeSpan value, string name)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, "must not be negative");
        }
    }
}
