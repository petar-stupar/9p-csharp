using System.Text.RegularExpressions;
using NineP.Protocol;
using NineP.Server.Internal;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using Xunit;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// <c>docs/server.md</c> is the published handler table, so it is read by a test rather than
/// trusted: a T-message routed by the dispatcher and missing from the document, or a row naming a
/// message the dispatcher does not route, fails the build.
/// </summary>
[Trait("Category", "Conformance")]
public sealed partial class ServerDocTests
{
    /// <summary>The document lists exactly the T-messages the dispatcher routes.</summary>
    [Fact]
    public void HandlerTableListsEveryTMessage()
    {
        HashSet<string> documented = DocumentedTypes();
        HashSet<string> routed = [.. Dispatcher.HandledTypes.Select(MessageTypes.GetName)];

        Assert.Equal(32, routed.Count);
        Assert.Empty(routed.Except(documented, StringComparer.Ordinal));
        Assert.Empty(documented.Except(routed, StringComparer.Ordinal));
    }

    /// <summary>Every row names a handler method or says the core owns the message outright.</summary>
    [Fact]
    public void EveryRowNamesAHandlerOrTheCore()
    {
        foreach (string line in TableRows())
        {
            string[] cells = line.Split('|', StringSplitOptions.TrimEntries);
            Assert.True(cells.Length >= 4, "a handler-table row needs three cells: " + line);
            Assert.NotEmpty(cells[2]);
            Assert.NotEmpty(cells[3]);
        }
    }

    private static HashSet<string> DocumentedTypes()
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string line in TableRows())
        {
            string cell = line.Split('|', StringSplitOptions.TrimEntries)[1];
            names.Add(cell.Trim('`'));
        }

        return names;
    }

    private static IEnumerable<string> TableRows()
    {
        string path = Path.Combine(RepositoryRoot(), "docs", "server.md");

        // Only the handler table, which is the first one: the sections after it list the same
        // message names again under different headings.
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                yield break;
            }

            if (RowPattern().IsMatch(line))
            {
                yield return line;
            }
        }
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NineP.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }

    [GeneratedRegex(@"^\| `T[a-z]+` \|")]
    private static partial Regex RowPattern();
}
