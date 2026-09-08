using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NineP.JsonFs;

/// <summary>A document jsonfs refuses to serve; the process exits 3 and prints the reason.</summary>
internal sealed class JsonFsStartupException : Exception
{
    /// <summary>Creates an empty refusal.</summary>
    public JsonFsStartupException()
    {
    }

    /// <summary>Creates a refusal with a message naming the limit that was broken.</summary>
    /// <param name="message">What is wrong with the document.</param>
    public JsonFsStartupException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a refusal wrapping the failure that caused it.</summary>
    /// <param name="message">What is wrong with the document.</param>
    /// <param name="innerException">The underlying failure.</param>
    public JsonFsStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
