using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Transports;

namespace NineP.Cli;

/// <summary>
/// The one UTF-8 encoder the cli uses. <c>Encoding.UTF8</c> is banned repository-wide because it
/// substitutes U+FFFD for invalid input.
/// </summary>
internal static class CliText
{
    /// <summary>Strict UTF-8: no byte-order mark, and invalid input throws.</summary>
    public static Encoding Utf8 { get; } =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
