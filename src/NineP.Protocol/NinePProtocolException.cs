namespace NineP.Protocol;

/// <summary>
/// A malformed or illegal 9P message (reference §8). The kind is machine-readable so that a caller
/// can tell a size violation, which cannot be resynced, from a field that merely did not parse.
/// </summary>
public sealed class NinePProtocolException : NinePException
{
    /// <summary>Creates a protocol exception of no particular kind.</summary>
    public NinePProtocolException()
        : this(ProtocolErrorKind.Bounds, "bad message")
    {
    }

    /// <summary>Creates a protocol exception with a message.</summary>
    /// <param name="message">The message for the developer, never for the wire.</param>
    public NinePProtocolException(string message)
        : base(message) => Kind = ProtocolErrorKind.Bounds;

    /// <summary>Creates a protocol exception with a message and the failure that caused it.</summary>
    /// <param name="message">The message for the developer, never for the wire.</param>
    /// <param name="innerException">The failure being wrapped.</param>
    public NinePProtocolException(string message, Exception innerException)
        : base(message, innerException) => Kind = ProtocolErrorKind.Bounds;

    /// <summary>Creates a protocol exception of a given kind with a human-readable detail.</summary>
    /// <param name="kind">What was wrong with the message.</param>
    /// <param name="message">The detail for the developer, never for the wire.</param>
    public NinePProtocolException(ProtocolErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    /// <summary>What was wrong with the message.</summary>
    public ProtocolErrorKind Kind { get; }
}
