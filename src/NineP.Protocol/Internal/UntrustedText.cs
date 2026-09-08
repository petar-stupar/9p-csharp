using System.Globalization;
using System.Text;

namespace NineP.Protocol.Internal;

/// <summary>
/// Reference §8 rule 11: a string that came from a peer is never put into a log record or an error
/// message as it arrived. A file name, an <c>aname</c>, a WebSocket <c>Origin</c> or a version
/// string is chosen by the other end of the connection, so left raw it can forge a whole log line
/// with an embedded newline, drive a terminal with an escape sequence, or flood a single record to
/// the msize bound. Every such string goes through <see cref="Sanitize"/> first.
///
/// Internal, and deliberately so (IR-3): rule 11 is an obligation on what these packages log, and
/// they discharge it at every boundary where a peer string is handed outward — an
/// <c>IRequestLogSink</c> receives a summary this has already been through. A consumer logging its
/// own strings does so through its own sink. Publishing later would be additive; unpublishing
/// would be breaking, so it starts internal.
/// </summary>
internal static class UntrustedText
{
    /// <summary>The cap of reference §8 rule 11: an untrusted string never exceeds it once escaped.</summary>
    public const int SanitizedMaxBytes = 256;

    /// <summary>
    /// Escapes control characters and caps an untrusted string at 256 UTF-8 bytes on a rune
    /// boundary (reference §8 rule 11), so that a peer cannot forge log lines or hide in them.
    /// </summary>
    /// <param name="value">The untrusted string.</param>
    /// <returns>The string as it is safe to log or to name in an error.</returns>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        StringBuilder escaped = new(value.Length);
        int bytes = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            string piece = Escape(rune);
            int width = NinePText.GetByteCount(piece);
            if (bytes + width > SanitizedMaxBytes)
            {
                break;
            }

            escaped.Append(piece);
            bytes += width;
        }

        return escaped.ToString();
    }

    private static string Escape(Rune rune)
    {
        if (!Rune.IsControl(rune) && rune.Value != '\\')
        {
            return rune.ToString();
        }

        return rune.Value switch
        {
            '\\' => "\\\\",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            _ => string.Format(CultureInfo.InvariantCulture, "\\u{0:X4}", rune.Value),
        };
    }
}
