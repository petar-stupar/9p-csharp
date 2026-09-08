using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Transports;

namespace NineP.JsonFs;

/// <summary>A command line jsonfs will not act on; the process prints it and exits 3.</summary>
internal sealed class JsonFsUsageException : Exception
{
    /// <summary>Creates an empty usage failure.</summary>
    public JsonFsUsageException()
    {
    }

    /// <summary>Creates a usage failure naming what was wrong.</summary>
    /// <param name="message">What the caller got wrong.</param>
    public JsonFsUsageException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a usage failure wrapping the parse that failed.</summary>
    /// <param name="message">What the caller got wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public JsonFsUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
