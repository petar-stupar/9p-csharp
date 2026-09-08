namespace NineP.Protocol;

/// <summary>
/// The base of every exception this library throws, carrying the 9P error value the core projects
/// onto the wire. Nothing else escapes a public method except the <see cref="ArgumentException"/>
/// family, <see cref="ObjectDisposedException"/> and <see cref="OperationCanceledException"/>.
/// </summary>
public class NinePException : Exception
{
    /// <summary>Creates an exception carrying the generic I/O error.</summary>
    public NinePException()
        : this(NinePError.FromErrno(Errno.EIO))
    {
    }

    /// <summary>Creates an exception with a message and the generic I/O error.</summary>
    /// <param name="message">The message for the developer, never for the wire.</param>
    public NinePException(string message)
        : base(message) => Error = NinePError.FromErrno(Errno.EIO);

    /// <summary>Creates an exception with a message and the failure that caused it.</summary>
    /// <param name="message">The message for the developer, never for the wire.</param>
    /// <param name="innerException">The failure being wrapped.</param>
    public NinePException(string message, Exception innerException)
        : base(message, innerException) => Error = NinePError.FromErrno(Errno.EIO);

    /// <summary>Creates an exception carrying a specific 9P error value.</summary>
    /// <param name="error">The error the peer will be told about.</param>
    public NinePException(NinePError error)
        : base(error.Ename) => Error = error;

    /// <summary>The 9P error this exception projects to on the wire.</summary>
    public NinePError Error { get; }
}
