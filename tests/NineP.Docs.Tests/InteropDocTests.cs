using Xunit;

namespace NineP.Docs.Tests;

/// <summary>
/// Rule-index rows 68 and 72 (AC-csharp-3, exit criterion 4): <c>docs/interop.md</c> says what was
/// run against which peer and what happened. Every peer row carries <c>pass</c>, <c>fail</c> or
/// <c>not run: &lt;reason&gt;</c>; a blank cell is not an answer, and an omitted peer is worse than
/// one recorded as not run.
/// </summary>
public sealed class InteropDocTests
{
    private static readonly string[] RequiredPeers = ["p9ufs", "plan9port"];

    /// <summary>Every row of the peer table has a verdict of one of the three legal shapes.</summary>
    [Fact]
    public void EveryPeerHasAVerdict()
    {
        string document = Document();
        List<string[]> rows = PeerRows(document);

        Assert.True(rows.Count >= 4, $"docs/interop.md lists {rows.Count} peer rows");

        foreach (string[] cells in rows)
        {
            string peer = cells[0].Trim().Trim('`');
            string verdict = cells[5].Trim().Trim('*').Trim('`').Trim();
            string command = cells[6].Trim();

            Assert.False(string.IsNullOrWhiteSpace(peer), "a peer row has no peer");
            Assert.True(
                verdict is "pass" or "fail" || verdict.StartsWith("not run: ", StringComparison.Ordinal),
                $"{peer}: \"{verdict}\" is not pass, fail or \"not run: <reason>\"");

            // A row that passed or failed must say how; a row that was not run says why in the
            // verdict itself, and may legitimately have no command at all.
            if (verdict is "pass" or "fail")
            {
                Assert.False(string.IsNullOrWhiteSpace(command), $"{peer} passed with no command recorded");
                Assert.NotEqual("—", command);
            }
        }
    }

    /// <summary>Both external reference peers of ARCHITECTURE.md §10.4 appear, in both directions.</summary>
    [Fact]
    public void BothExternalPeersAppear()
    {
        string document = Document();

        foreach (string peer in RequiredPeers)
        {
            Assert.Contains(peer, document, StringComparison.Ordinal);
        }

        List<string[]> rows = PeerRows(document);
        Assert.Contains(rows, cells => cells[2].Contains("server", StringComparison.Ordinal));
        Assert.Contains(rows, cells => cells[2].Contains("client", StringComparison.Ordinal));
    }

    /// <summary>The merged-language statement is present and says which languages there are.</summary>
    [Fact]
    public void MergedLanguagesAreStated()
    {
        string document = Document();

        Assert.Contains("Merged languages", document, StringComparison.Ordinal);
        Assert.Contains("none yet", document, StringComparison.Ordinal);
    }

    /// <summary>The Tfsync caveat of the fixture is answered rather than ignored.</summary>
    [Fact]
    public void TfsyncCaveatIsRecorded()
    {
        Assert.Contains("Tfsync", Document(), StringComparison.Ordinal);
    }

    private static List<string[]> PeerRows(string document)
    {
        List<string[]> rows = [];
        bool inTable = false;

        foreach (string line in document.Split('\n'))
        {
            if (line.StartsWith("| peer |", StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }

            if (!inTable)
            {
                continue;
            }

            if (!line.StartsWith("| ", StringComparison.Ordinal))
            {
                break;
            }

            string[] cells = line.Trim().Trim('|').Split('|');
            if (cells.Length >= 7 && !cells[0].Trim().StartsWith("---", StringComparison.Ordinal))
            {
                rows.Add(cells);
            }
        }

        return rows;
    }

    private static string Document() =>
        File.ReadAllText(Path.Combine(ReadmeSnippetTests.RepositoryRoot(), "docs", "interop.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
}
