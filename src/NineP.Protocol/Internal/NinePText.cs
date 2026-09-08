using System.Text;

namespace NineP.Protocol.Internal;

/// <summary>
/// The one place 9P strings become .NET strings. The encoder throws on invalid bytes rather than
/// substituting U+FFFD, which is why <c>Encoding.UTF8</c> is banned in this repository: a peer
/// that sends malformed UTF-8 must be told its message is malformed, not have it quietly repaired.
/// </summary>
internal static class NinePText
{
    /// <summary>Strict UTF-8: no byte-order mark, and invalid bytes throw (reference §8 rule 3).</summary>
    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes one 9P string, rejecting a NUL byte before the encoder ever sees the bytes.
    /// </summary>
    /// <param name="bytes">The string's bytes, without its length prefix.</param>
    /// <param name="value">The decoded string, or the empty string on failure.</param>
    /// <param name="failure">The failure kind when the result is false.</param>
    /// <returns>True when the bytes are a legal 9P string.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out string value, out ProtocolErrorKind failure)
    {
        if (bytes.IndexOf((byte)0) >= 0)
        {
            value = string.Empty;
            failure = ProtocolErrorKind.Nul;
            return false;
        }

        try
        {
            value = Utf8.GetString(bytes);
            failure = default;
            return true;
        }
        catch (DecoderFallbackException)
        {
            value = string.Empty;
            failure = ProtocolErrorKind.Utf8;
            return false;
        }
    }

    /// <summary>
    /// The name rules of reference §8 rule 3: no '/', never ".", and ".." only where a walk allows
    /// it. The length rule is applied to the wire bytes before decoding. An empty name is refused
    /// too: it names no file, and accepting it let <c>Tmkdir name=""</c> create a directory whose
    /// on-wire listing entry had no name at all. The one message where empty means something is
    /// <c>Txattrwalk</c>, where it asks for the list of attribute names.
    /// </summary>
    /// <param name="name">The decoded name.</param>
    /// <param name="allowParent">True inside a <c>Twalk</c>, where ".." is a legal element.</param>
    /// <param name="allowEmpty">True inside a <c>Txattrwalk</c>, where "" is the attribute list.</param>
    /// <returns>True when the name may appear in the message being decoded.</returns>
    public static bool IsLegalName(string name, bool allowParent, bool allowEmpty = false)
    {
        if (name.Length == 0)
        {
            return allowEmpty;
        }

        if (name.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        return name switch
        {
            "." => false,
            ".." => allowParent,
            _ => true,
        };
    }

    /// <summary>The number of bytes this string occupies on the wire, excluding its length prefix.</summary>
    /// <param name="value">The string to measure.</param>
    /// <returns>The UTF-8 byte count.</returns>
    public static int GetByteCount(string value) => Utf8.GetByteCount(value);

    /// <summary>Encodes a string into a destination sized by <see cref="GetByteCount"/>.</summary>
    /// <param name="value">The string to encode.</param>
    /// <param name="destination">The span to write into.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Encode(string value, Span<byte> destination) => Utf8.GetBytes(value, destination);
}
