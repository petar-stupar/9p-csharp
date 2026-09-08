using System.Text;

namespace NineP.Conformance;

/// <summary>
/// The one UTF-8 encoder the driver uses. <c>Encoding.UTF8</c> substitutes U+FFFD for invalid
/// input, which would make a byte-for-byte diff pass over bytes that differ.
/// </summary>
internal static class ConformanceText
{
    /// <summary>Strict UTF-8: no byte-order mark, and invalid input throws.</summary>
    public static Encoding Utf8 { get; } =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
