using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace NineP.Cli.Oidc;

/// <summary>An OIDC grant that did not complete; the cli reports it and exits 1.</summary>
internal sealed class OidcException : Exception
{
    /// <summary>Creates an empty failure.</summary>
    public OidcException()
    {
    }

    /// <summary>Creates a failure naming what the issuer said.</summary>
    /// <param name="message">What went wrong.</param>
    public OidcException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a failure wrapping the transport error behind it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public OidcException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
