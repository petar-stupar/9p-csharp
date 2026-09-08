using System.Text.RegularExpressions;
using Xunit;

namespace NineP.Docs.Tests;

/// <summary>
/// Rule-index row 64 (AC-e): <c>docs/benchmarks.md</c> carries <b>measured</b> numbers for the four
/// benchmarks of architecture §9, and each of them carries the command that produced it, the
/// machine it ran on and the runtime it ran under. A benchmark document without those is a claim,
/// not a measurement, so this test reads the document rather than trusting it.
/// </summary>
public sealed partial class BenchmarkDocTests
{
    private static readonly string[] Sections =
    [
        "(a) sequential read and write",
        "(b) 100 000 walk + stat + clunk",
        "(c) peak RSS",
        "(d) codec",
    ];

    private static readonly string[] MachineFacts =
    [
        "Apple M4 Pro",
        "macOS",
        "SDK",
        "net10.0",
        "GC",
    ];

    /// <summary>Every section of §11 point 6 is present, with a number and the command behind it.</summary>
    [Fact]
    public void MeasuredNumbersArePresent()
    {
        string document = Document();

        foreach (string fact in MachineFacts)
        {
            Assert.Contains(fact, document, StringComparison.Ordinal);
        }

        foreach (string section in Sections)
        {
            string body = Section(document, section);

            Assert.Contains("dotnet run -c Release --project tests/NineP.Benchmarks", body, StringComparison.Ordinal);
            Assert.True(
                MeasurementPattern().IsMatch(body),
                $"section \"{section}\" carries no measured number with a unit");
        }
    }

    /// <summary>Peak RSS names both the mechanism and the cross-check it was verified against.</summary>
    [Fact]
    public void PeakRssCarriesItsCrossCheck()
    {
        string body = Section(Document(), "(c) peak RSS");

        Assert.Contains("getrusage", body, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/time", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PeakWorkingSet64 =", body, StringComparison.Ordinal);
    }

    /// <summary>The closing note promises no target and names the regression threshold.</summary>
    [Fact]
    public void NoTargetIsPromised()
    {
        string document = Document();

        Assert.Contains("No target is promised", document, StringComparison.Ordinal);
        Assert.Contains("20 %", document, StringComparison.Ordinal);
    }

    private static string Section(string document, string heading)
    {
        int start = document.IndexOf("## " + heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"docs/benchmarks.md has no section \"{heading}\"");

        int next = document.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return next < 0 ? document[start..] : document[start..next];
    }

    private static string Document() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "benchmarks.md"));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NineP.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }

    /// <summary>A decimal number followed by one of the units the four benchmarks report in.</summary>
    [GeneratedRegex(@"\d[\d ,.]*\s*(MiB/s|ops/s|µs|us/op|ns|MiB|bytes)")]
    private static partial Regex MeasurementPattern();
}
