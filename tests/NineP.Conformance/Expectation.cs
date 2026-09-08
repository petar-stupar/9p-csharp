using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NineP.JsonFs;

namespace NineP.Conformance;

/// <summary>
/// Composes the Part A listing exactly as <c>docs/9p/fixtures/gen-conformance-expected.mjs</c>
/// does, from what the server actually answers. The <b>order</b> comes from the document — the
/// generator recurses in document order while <c>ls</c> prints bytewise-sorted names, so the two
/// cannot be recovered from each other — and every byte of <b>content</b> comes from the server.
/// </summary>
internal static class Expectation
{
    /// <summary>Walks the document depth-first, driving the target for every line.</summary>
    /// <param name="target">The running server and its cli.</param>
    /// <param name="document">The document being served, for its traversal order.</param>
    /// <returns>The composed listing, ready to be compared with the fixture.</returns>
    public static async Task<string> ComposeAsync(ConformanceTarget target, JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(document);

        List<string> lines = [];
        await WalkAsync(target, "/", document.RootElement, lines);

        return string.Join('\n', lines) + "\n";
    }

    private static async Task WalkAsync(
        ConformanceTarget target, string path, JsonElement value, List<string> lines)
    {
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            await FileAsync(target, path, lines);
            return;
        }

        CliResult listing = await target.RunAsync(["ls", path]);
        Require(listing, path, "ls");

        lines.Add("ls " + path);
        foreach (string entry in listing.Text.Split('\n'))
        {
            if (entry.Length > 0)
            {
                lines.Add(entry);
            }
        }

        lines.Add(string.Empty);

        foreach ((string name, JsonElement child) in Entries(value))
        {
            await WalkAsync(target, Join(path, name), child, lines);
        }
    }

    private static async Task FileAsync(ConformanceTarget target, string path, List<string> lines)
    {
        CliResult contents = await target.RunAsync(["cat", path]);
        Require(contents, path, "cat");

        lines.Add("cat " + path);
        lines.Add(string.Format(CultureInfo.InvariantCulture, "size {0}", contents.Stdout.Length));
        lines.Add("sha256 " + Hex(SHA256.HashData(contents.Stdout)));
        lines.Add(string.Empty);
    }

    /// <summary>The members of a container, in document order and under their file names.</summary>
    /// <param name="value">The object or array.</param>
    /// <returns>The members, in the order the document declares them.</returns>
    private static IEnumerable<(string Name, JsonElement Value)> Entries(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                yield return (index.ToString(CultureInfo.InvariantCulture), item);
                index++;
            }

            yield break;
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            yield return (JsonKey.Encode(property.Name), property.Value);
        }
    }

    /// <summary>
    /// Lower-case hex, which is what the fixture's <c>sha256</c> lines carry. It is written out
    /// rather than lower-cased from <see cref="Convert.ToHexString(byte[])"/> because CA1308
    /// rightly refuses a lower-casing that is not obviously an encoding.
    /// </summary>
    /// <param name="hash">The digest.</param>
    /// <returns>Its lower-case hexadecimal text.</returns>
    private static string Hex(ReadOnlySpan<byte> hash)
    {
        const string Digits = "0123456789abcdef";
        StringBuilder text = new(hash.Length * 2);

        foreach (byte value in hash)
        {
            text.Append(Digits[value >> 4]).Append(Digits[value & 0xF]);
        }

        return text.ToString();
    }

    private static string Join(string path, string name) =>
        path == "/" ? "/" + name : path + "/" + name;

    private static void Require(CliResult result, string path, string command)
    {
        if (result.ExitCode != 0)
        {
            throw new ConformanceFailure(string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} exited {2}: {3}",
                command,
                path,
                result.ExitCode,
                result.Stderr.Trim()));
        }
    }
}

/// <summary>A step of the scenario that did not do what the fixture says it must.</summary>
internal sealed class ConformanceFailure : Exception
{
    /// <summary>Creates an empty failure.</summary>
    public ConformanceFailure()
    {
    }

    /// <summary>Creates a failure naming what went wrong.</summary>
    /// <param name="message">What the step did instead.</param>
    public ConformanceFailure(string message)
        : base(message)
    {
    }

    /// <summary>Creates a failure wrapping the exception behind it.</summary>
    /// <param name="message">What the step did instead.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ConformanceFailure(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
