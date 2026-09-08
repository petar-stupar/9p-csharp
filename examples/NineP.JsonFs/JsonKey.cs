using System.Text;

namespace NineP.JsonFs;

/// <summary>
/// The JSON-key to file-name mapping of the workspace architecture §7 (S-28, R-19). Every key of
/// a JSON object has to become a legal 9P name — no <c>/</c>, no NUL, never <c>.</c> or
/// <c>..</c>, and never empty — and the mapping has to be injective, or two distinct keys would
/// share one file.
/// </summary>
internal static class JsonKey
{
    private const string PercentEscape = "%25";
    private const string SlashEscape = "%2F";
    private const string NulEscape = "%00";
    private const string DotEscape = "%2E";
    private const string DotDotEscape = "%2E%2E";
    private const string EmptyEscape = "%";
    private const int EscapeLength = 3;

    /// <summary>
    /// Encodes one JSON key as a file name, in the order the architecture fixes and no other:
    /// <c>%</c> first, then <c>/</c> and NUL, then the two dot names, then the empty key.
    /// </summary>
    /// <param name="key">The key as it appears in the document.</param>
    /// <returns>The name the file has in the served tree.</returns>
    /// <exception cref="ArgumentNullException">The key is null.</exception>
    public static string Encode(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length == 0)
        {
            // Step 4: no other key can produce a bare "%", because step 1 escaped every literal
            // one, which is what keeps the empty key distinguishable.
            return EmptyEscape;
        }

        // Step 1 runs first so that "a/b" (-> "a%2Fb") stays distinct from the literal key
        // "a%2Fb" (-> "a%252Fb"). Escaping the slash first would collapse the two.
        StringBuilder builder = new(key.Length + EscapeLength);
        foreach (char c in key)
        {
            switch (c)
            {
                case '%':
                    builder.Append(PercentEscape);
                    break;
                case '/':
                    builder.Append(SlashEscape);
                    break;
                case '\0':
                    builder.Append(NulEscape);
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        string encoded = builder.ToString();

        // Step 3: a key that survived step 1 unchanged may still read as a dot name.
        return encoded switch
        {
            "." => DotEscape,
            ".." => DotDotEscape,
            _ => encoded,
        };
    }

    /// <summary>The exact inverse of <see cref="Encode"/>, applied in the reverse order.</summary>
    /// <param name="name">A name from the served tree.</param>
    /// <returns>The JSON key it stands for.</returns>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    public static string Decode(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        switch (name)
        {
            case EmptyEscape:
                return string.Empty;
            case DotEscape:
                return ".";
            case DotDotEscape:
                return "..";
            default:
                break;
        }

        StringBuilder builder = new(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char? unescaped = i + EscapeLength <= name.Length
                ? Unescape(name.AsSpan(i, EscapeLength))
                : null;

            if (unescaped is char c)
            {
                builder.Append(c);
                i += EscapeLength - 1;
                continue;
            }

            builder.Append(name[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The character one three-byte escape stands for, or null when the run is not one this
    /// encoder produces — a name a client invented rather than one this tree published.
    /// </summary>
    /// <param name="escape">Three characters starting at a candidate escape.</param>
    /// <returns>The character, or null when the run is not an escape.</returns>
    private static char? Unescape(ReadOnlySpan<char> escape) => escape switch
    {
        PercentEscape => '%',
        SlashEscape => '/',
        NulEscape => '\0',
        _ => null,
    };
}
