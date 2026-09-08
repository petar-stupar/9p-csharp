using System.Text;
using NineP.Protocol;
using NineP.Server;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>
/// The one UTF-8 encoder todofs uses. <c>Encoding.UTF8</c> is banned repository-wide because it
/// substitutes U+FFFD for invalid input, which would store a different string than was written.
/// </summary>
internal static class TodoText
{
    /// <summary>Strict UTF-8: no byte-order mark, and invalid input throws.</summary>
    public static Encoding Utf8 { get; } =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
