using System.Text;

namespace NineP.JsonFs;

/// <summary>
/// The one UTF-8 encoder jsonfs uses. <c>Encoding.UTF8</c> is banned repository-wide because it
/// substitutes U+FFFD for invalid input, which would turn a client's malformed bytes into a
/// different document rather than into an error.
/// </summary>
internal static class JsonText
{
    /// <summary>Strict UTF-8: no byte-order mark, and invalid input throws.</summary>
    public static Encoding Utf8 { get; } =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Encodes text as the bytes a read answers with.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Its UTF-8 bytes.</returns>
    public static byte[] ToBytes(string text) => Utf8.GetBytes(text);

    /// <summary>The number of bytes <paramref name="text"/> occupies.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Its length in UTF-8 bytes.</returns>
    public static int ByteCount(string text) => Utf8.GetByteCount(text);

    /// <summary>Decodes what a client wrote.</summary>
    /// <param name="bytes">The bytes the client sent.</param>
    /// <returns>The text they stand for.</returns>
    /// <exception cref="DecoderFallbackException">The bytes are not valid UTF-8.</exception>
    public static string FromBytes(ReadOnlySpan<byte> bytes) => Utf8.GetString(bytes);
}
