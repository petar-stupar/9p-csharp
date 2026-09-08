using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using NineP.Protocol;
using NineP.Protocol.Transports;

namespace NineP.TodoFs;

/// <summary>A command line todofs will not act on; the process prints it and exits 3.</summary>
internal sealed class TodoFsUsageException : Exception
{
    /// <summary>Creates an empty usage failure.</summary>
    public TodoFsUsageException()
    {
    }

    /// <summary>Creates a usage failure naming what was wrong.</summary>
    /// <param name="message">What the caller got wrong.</param>
    public TodoFsUsageException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a usage failure wrapping the parse that failed.</summary>
    /// <param name="message">What the caller got wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public TodoFsUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
