using System.Text;
using NineP.Docs.Tests.Snippets;
using Xunit;

namespace NineP.Docs.Tests;

/// <summary>
/// §11 point 5 and exit criterion 6: the README's examples are <b>executed</b> and
/// <b>byte-compared</b>. Executing without comparing lets the README drift away from the code that
/// works; comparing without executing lets both rot together. Both halves are here.
/// </summary>
public sealed class ReadmeSnippetTests
{
    private static readonly string[] Regions = ["server", "client", "tree"];

    /// <summary>Every fenced block in the README is the snippet source, character for character.</summary>
    [Fact]
    public void SnippetsMatchTheirSource()
    {
        string readme = ReadmeText();

        foreach (string region in Regions)
        {
            Assert.Equal(SnippetRegion(region), ReadmeBlock(readme, "snippet:" + region));
        }
    }

    /// <summary>
    /// The two 60-second examples run against each other over loopback TCP, and the client reads
    /// what the server's tree holds. This is the same source the assertions above compare.
    /// </summary>
    [Fact]
    public async Task SnippetsRunAndMatch()
    {
        string readme = ReadmeText();

        foreach (string region in Regions)
        {
            Assert.Equal(SnippetRegion(region), ReadmeBlock(readme, "snippet:" + region));
        }

        Assert.Equal("hello, 9P\n", await ReadmeSnippets.RunAsync());
    }

    /// <summary>
    /// The scratch-install block CI step 8 extracts is present, is a complete program, and is
    /// consistent with the two examples above: it opens the same file and prints the same
    /// greeting. CI runs it against the packed packages; <c>PackagingTests</c> runs it here.
    /// </summary>
    [Fact]
    public void ScratchInstallBlockIsPresentAndConsistent()
    {
        string readme = ReadmeText();

        string program = ReadmeBlock(readme, "ci:snippet");
        string expected = ReadmeBlock(readme, "ci:expected", "text");

        Assert.Contains("<!-- ci:end -->", readme, StringComparison.Ordinal);
        Assert.Contains("NinePServer", program, StringComparison.Ordinal);
        Assert.Contains("NinePClient", program, StringComparison.Ordinal);
        Assert.Contains("hello.txt", program, StringComparison.Ordinal);
        Assert.Equal("hello.txt\nhello, 9P\n", expected);
    }

    /// <summary>The README's own text names the markers the CI step depends on.</summary>
    [Fact]
    public void ReadmeCarriesEveryMarkerCiDependsOn()
    {
        string readme = ReadmeText();

        foreach (string marker in new[]
        {
            "<!-- snippet:server -->",
            "<!-- snippet:client -->",
            "<!-- snippet:tree -->",
            "<!-- ci:snippet -->",
            "<!-- ci:expected -->",
            "<!-- ci:end -->",
        })
        {
            Assert.Contains(marker, readme, StringComparison.Ordinal);
        }
    }

    /// <summary>The fenced block that follows a marker comment, without its fences.</summary>
    /// <param name="readme">The whole document.</param>
    /// <param name="marker">The marker name, without the comment delimiters.</param>
    /// <param name="language">The fence's language tag.</param>
    /// <returns>The block's text, ending in a newline.</returns>
    internal static string ReadmeBlock(string readme, string marker, string language = "csharp")
    {
        ArgumentNullException.ThrowIfNull(readme);

        int at = readme.IndexOf($"<!-- {marker} -->", StringComparison.Ordinal);
        Assert.True(at >= 0, $"README.md has no <!-- {marker} --> marker");

        int open = readme.IndexOf("```" + language + "\n", at, StringComparison.Ordinal);
        Assert.True(open >= 0, $"no ```{language} block follows <!-- {marker} -->");

        int start = open + language.Length + 4;
        int close = readme.IndexOf("\n```", start, StringComparison.Ordinal);
        Assert.True(close >= 0, $"the ```{language} block after <!-- {marker} --> is not closed");

        return readme[start..(close + 1)];
    }

    /// <summary>The text between <c>// snippet:name</c> and <c>// endsnippet</c>, de-indented.</summary>
    private static string SnippetRegion(string name)
    {
        string[] lines = File.ReadAllLines(SnippetPath());

        int start = Array.FindIndex(lines, line => line.Trim() == "// snippet:" + name);
        Assert.True(start >= 0, $"ReadmeSnippets.cs has no // snippet:{name} region");

        int end = Array.FindIndex(lines, start + 1, line => line.Trim() == "// endsnippet");
        Assert.True(end > start, $"the // snippet:{name} region is not closed");

        string[] body = lines[(start + 1)..end];
        int indent = body.Where(line => line.Trim().Length > 0)
            .Select(line => line.Length - line.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();

        StringBuilder text = new();
        foreach (string line in body)
        {
            text.Append(line.Trim().Length == 0 ? string.Empty : line[indent..]).Append('\n');
        }

        return text.ToString();
    }

    private static string ReadmeText() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md")).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string SnippetPath() =>
        Path.Combine(RepositoryRoot(), "tests", "NineP.Docs.Tests", "Snippets", "ReadmeSnippets.cs");

    internal static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NineP.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }
}
