using System.Globalization;
using System.Text.Json;

namespace NineP.JsonFs;

/// <summary>
/// The number-to-text mapping of the workspace architecture §7 (S-13, R-20, RK-71). It is written
/// out rather than delegated to a bare <c>ToString()</c>, because the default rendering of a
/// double is neither culture-independent nor free of exponents, and the conformance fixture is
/// compared byte for byte.
/// </summary>
internal static class JsonNumber
{
    /// <summary>The smallest magnitude rendered without an exponent.</summary>
    private const double PlainLowerBound = 1e-6;

    /// <summary>The first magnitude above which the source token is used instead.</summary>
    private const double PlainUpperBound = 1e21;

    /// <summary>Renders one JSON number as the bytes of its file.</summary>
    /// <param name="element">A <see cref="JsonValueKind.Number"/> element.</param>
    /// <returns>The text the file holds.</returns>
    /// <exception cref="ArgumentException">The element is not a number.</exception>
    public static string Format(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException("only a JSON number has number text", nameof(element));
        }

        // An integral value inside the 64-bit range is exact, so it is rendered as itself; going
        // through a double first would silently round anything past 2^53.
        if (element.TryGetInt64(out long signed))
        {
            return signed.ToString(CultureInfo.InvariantCulture);
        }

        if (element.TryGetUInt64(out ulong unsigned))
        {
            return unsigned.ToString(CultureInfo.InvariantCulture);
        }

        if (!element.TryGetDouble(out double value))
        {
            return element.GetRawText();
        }

        double magnitude = Math.Abs(value);
        if (magnitude < PlainLowerBound || magnitude >= PlainUpperBound)
        {
            // Outside the plain-decimal window the source token is the only faithful rendering:
            // expanding 1e300 would produce three hundred digits nobody wrote.
            return element.GetRawText();
        }

        return Expand(value.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Rewrites a round-trip rendering that carries an exponent as the same value written out in
    /// full. Only magnitudes inside the plain-decimal window reach this, so the expansion is
    /// always short.
    /// </summary>
    /// <param name="rendered">The "R" rendering, which may carry an <c>E</c>.</param>
    /// <returns>The same number with no exponent.</returns>
    private static string Expand(string rendered)
    {
        int marker = rendered.IndexOf('E', StringComparison.Ordinal);
        if (marker < 0)
        {
            return rendered;
        }

        int exponent = int.Parse(
            rendered.AsSpan(marker + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        string mantissa = rendered[..marker];
        string sign = mantissa.StartsWith('-') ? "-" : string.Empty;
        if (sign.Length == 1)
        {
            mantissa = mantissa[1..];
        }

        int point = mantissa.IndexOf('.', StringComparison.Ordinal);
        string digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        int shifted = (point < 0 ? mantissa.Length : point) + exponent;

        if (shifted <= 0)
        {
            return sign + "0." + new string('0', -shifted) + digits;
        }

        return shifted >= digits.Length
            ? sign + digits + new string('0', shifted - digits.Length)
            : sign + digits[..shifted] + "." + digits[shifted..];
    }
}
