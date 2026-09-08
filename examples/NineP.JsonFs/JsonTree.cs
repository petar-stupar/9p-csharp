using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NineP.JsonFs;

/// <summary>
/// The whole served document, plus the allocation counter its qid paths come from. The tree is
/// mutable under one lock; jsonfs is an example, and a single lock is the shape whose correctness
/// a reader can check by eye.
/// </summary>
internal sealed partial class JsonTree
{
    /// <summary>The largest document this server will load, per the architecture's §7 bound.</summary>
    public const long MaxDocumentBytes = 64L * 1024 * 1024;

    /// <summary>The deepest nesting this server will load.</summary>
    public const int MaxDepth = 256;

    /// <summary>
    /// The largest a scalar file may grow to through a write. A value cannot be larger than the
    /// document that holds it, and §7 refuses a document of <see cref="MaxDocumentBytes"/> or
    /// more, so that is the bound. Without it the length of the buffer a write allocates is
    /// whatever offset the client chose, and a fifteen-byte <c>Twrite</c> at a one-gigabyte
    /// offset allocated a gigabyte before it could fail.
    /// </summary>
    public const long MaxScalarBytes = MaxDocumentBytes;

    /// <summary>
    /// The bound the parser itself runs under. It is deliberately above <see cref="MaxDepth"/> so
    /// that a document just over the limit is refused by this file's own check, with a message
    /// naming the limit, rather than by the reader with a message about JSON syntax. Anything
    /// past this is refused by the reader, which is the outer guard on a hostile file.
    /// </summary>
    private const int ParserMaxDepth = 1024;

    private ulong _nextPath = 1;

    private JsonTree(JsonDirectoryNode root) => Root = root;

    /// <summary>The document's root, which is always a container.</summary>
    public JsonDirectoryNode Root { get; }

    /// <summary>Serialises every mutation and every write-back.</summary>
    public object Gate { get; } = new();

    /// <summary>Hands out the next qid path.</summary>
    /// <returns>A path no other node of this tree has.</returns>
    public ulong NextPath() => _nextPath++;

    /// <summary>
    /// Loads a document, refusing one that is too large or too deeply nested before any of it is
    /// mapped. Both refusals name their limit, because an operator who hits one needs to know
    /// which.
    /// </summary>
    /// <param name="path">The file to load.</param>
    /// <returns>The loaded tree.</returns>
    /// <exception cref="JsonFsStartupException">The document breaks a documented limit.</exception>
    public static JsonTree Load(string path)
    {
        long length = new FileInfo(path).Length;
        if (length >= MaxDocumentBytes)
        {
            throw new JsonFsStartupException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} is {1} bytes; jsonfs refuses a document of {2} bytes or more",
                path,
                length,
                MaxDocumentBytes));
        }

        using FileStream stream = File.OpenRead(path);
        return Parse(stream, path);
    }

    /// <summary>Loads a document from an open stream, for the tests and for stdin.</summary>
    /// <param name="stream">The JSON text.</param>
    /// <param name="origin">What to name in a refusal message.</param>
    /// <returns>The loaded tree.</returns>
    /// <exception cref="JsonFsStartupException">The document breaks a documented limit.</exception>
    public static JsonTree Parse(Stream stream, string origin)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = ParserMaxDepth });
        }
        catch (JsonException failure)
        {
            throw new JsonFsStartupException(
                string.Format(CultureInfo.InvariantCulture, "{0} is not valid JSON: {1}", origin, failure.Message),
                failure);
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                throw new JsonFsStartupException(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} has a {1} at its root; jsonfs serves an object or an array",
                    origin,
                    document.RootElement.ValueKind));
            }

            JsonTree tree = new(new JsonDirectoryNode(0, document.RootElement.ValueKind == JsonValueKind.Array));
            tree.Fill(tree.Root, document.RootElement, origin, depth: 1);
            return tree;
        }
    }

    /// <summary>Writes the tree back as JSON. The caller holds <see cref="Gate"/>.</summary>
    /// <param name="writer">Where to write.</param>
    public void Write(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        WriteNode(writer, Root);
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonTreeNode node)
    {
        // Stream the document instead of retaining every scalar in the JSON writer's buffer.
        // A single scalar can still need its own encoding buffer, bounded by MaxScalarBytes.
        if (writer.BytesPending >= 16384)
        {
            writer.Flush();
        }
        switch (node)
        {
            case JsonDirectoryNode { IsArray: true } array:
                writer.WriteStartArray();
                foreach (JsonChild child in array.Children)
                {
                    WriteNode(writer, child.Node);
                }

                writer.WriteEndArray();
                return;

            case JsonDirectoryNode container:
                writer.WriteStartObject();
                foreach (JsonChild child in container.Children)
                {
                    writer.WritePropertyName(child.Key);
                    WriteNode(writer, child.Node);
                }

                writer.WriteEndObject();
                return;

            case JsonScalarNode scalar:
                WriteScalar(writer, scalar);
                return;

            default:
                writer.WriteNullValue();
                return;
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, JsonScalarNode scalar)
    {
        switch (scalar.Kind)
        {
            // A number file that has been truncated and not written yet holds no number text, so
            // it serialises as the empty string it currently is rather than as invalid JSON.
            //
            // The token is written through a JsonDocument rather than with WriteRawValue, which
            // emits no newline and no indent before the value it writes: an indented document came
            // out with its numbers appended to the previous element's line. Parsing and writing
            // the element keeps the source token exactly as it was written — 1e300 stays 1e300,
            // 1.50 keeps its trailing zero — and pays the indentation the writer owes.
            case JsonScalarKind.Number when scalar.Text.Length > 0:
                using (JsonDocument number = JsonDocument.Parse(scalar.Text))
                {
                    number.RootElement.WriteTo(writer);
                }

                return;
            case JsonScalarKind.Boolean:
                writer.WriteBooleanValue(scalar.Text == "true");
                return;
            case JsonScalarKind.Null:
                writer.WriteNullValue();
                return;
            default:
                writer.WriteStringValue(scalar.Text);
                return;
        }
    }

    private void Fill(JsonDirectoryNode container, JsonElement element, string origin, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new JsonFsStartupException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} nests deeper than {1} levels, which jsonfs refuses",
                origin,
                MaxDepth));
        }

        if (container.IsArray)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                string key = index.ToString(CultureInfo.InvariantCulture);
                container.Add(new JsonChild(key, key, Build(item, origin, depth)));
                index++;
            }

            return;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            container.Add(new JsonChild(
                property.Name, JsonKey.Encode(property.Name), Build(property.Value, origin, depth)));
        }
    }

    private JsonTreeNode Build(JsonElement element, string origin, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            case JsonValueKind.Array:
                JsonDirectoryNode container =
                    new(NextPath(), element.ValueKind == JsonValueKind.Array);
                Fill(container, element, origin, depth + 1);
                return container;

            case JsonValueKind.String:
                return new JsonScalarNode(NextPath(), JsonScalarKind.Text, element.GetString() ?? string.Empty);

            case JsonValueKind.Number:
                return new JsonScalarNode(NextPath(), JsonScalarKind.Number, JsonNumber.Format(element));

            case JsonValueKind.True:
            case JsonValueKind.False:
                return new JsonScalarNode(
                    NextPath(), JsonScalarKind.Boolean, element.ValueKind == JsonValueKind.True ? "true" : "false");

            default:
                return new JsonScalarNode(NextPath(), JsonScalarKind.Null, string.Empty);
        }
    }
}
