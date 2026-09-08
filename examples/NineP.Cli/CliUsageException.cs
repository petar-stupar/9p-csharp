using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Transports;

namespace NineP.Cli;

/// <summary>A command line <c>ninep</c> will not act on; the process prints it and exits 3.</summary>
internal sealed class CliUsageException : Exception
{
    /// <summary>Creates an empty usage failure.</summary>
    public CliUsageException()
    {
    }

    /// <summary>Creates a usage failure naming what was wrong.</summary>
    /// <param name="message">What the caller got wrong.</param>
    public CliUsageException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a usage failure wrapping the parse that failed.</summary>
    /// <param name="message">What the caller got wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public CliUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
