using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NineP.JsonFs;

/// <summary>A JSON string, number, boolean or null: a regular file.</summary>
internal sealed class JsonScalarNode : JsonTreeNode
{
    /// <summary>Creates a scalar.</summary>
    /// <param name="path">The allocation counter's value for this node.</param>
    /// <param name="kind">Which JSON scalar this is.</param>
    /// <param name="text">The file's contents as text.</param>
    public JsonScalarNode(ulong path, JsonScalarKind kind, string text)
        : base(path)
    {
        Kind = kind;
        Text = text;
    }

    /// <summary>Which JSON scalar this is; a write may demote it to a string.</summary>
    public JsonScalarKind Kind { get; internal set; }

    /// <summary>The file's contents as text.</summary>
    public string Text { get; internal set; }

    /// <summary>The file's contents as the bytes a read answers with.</summary>
    /// <returns>A fresh copy of the file's bytes.</returns>
    public byte[] ToBytes() => JsonText.ToBytes(Text);

    /// <summary>
    /// Empties the file without deciding its type. Truncation is half of a replace — a client
    /// opens with <c>OTRUNC</c> and then writes — so the type is decided by
    /// <see cref="Assign"/> when the bytes arrive, not by the emptying that precedes them.
    /// </summary>
    public void Truncate()
    {
        Text = string.Empty;
        Touch();
    }

    /// <summary>
    /// Replaces the value from what a client wrote. Text that parses as a JSON number, boolean or
    /// null keeps that type <b>only</b> when the original was already of that type; anything else
    /// becomes a string. The demotion is what conformance Part B step 7 pins.
    /// </summary>
    /// <param name="written">The bytes the client wrote, decoded as UTF-8.</param>
    public void Assign(string written)
    {
        ArgumentNullException.ThrowIfNull(written);

        Kind = KindOf(written, Kind);
        Text = Kind == JsonScalarKind.Null ? string.Empty : written;
        Touch();
    }

    private static JsonScalarKind KindOf(string written, JsonScalarKind original) => original switch
    {
        JsonScalarKind.Number when IsNumber(written) => JsonScalarKind.Number,
        JsonScalarKind.Boolean when written is "true" or "false" => JsonScalarKind.Boolean,
        JsonScalarKind.Null when written.Length == 0 || written == "null" => JsonScalarKind.Null,
        _ => JsonScalarKind.Text,
    };

    private static bool IsNumber(string written)
    {
        if (written.Length == 0)
        {
            return false;
        }

        // The written text has to be a JSON number, not merely something double.TryParse would
        // accept: "NaN", "Infinity" and " 1 " are not JSON.
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(written);
            return parsed.RootElement.ValueKind == JsonValueKind.Number;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
