using System.Text.RegularExpressions;
using Xunit;

namespace NineP.Repo.Tests;

/// <summary>
/// The dependency policy of `CLAUDE.md` and arch §8 item 6: every runtime dependency has a Decision
/// Log row in this repository's <c>ARCHITECTURE.md</c>, with the version pinned exactly and the same
/// version in both places. A dependency that arrives without a row, or a row that drifts from the
/// pin, fails the build rather than the review.
/// </summary>
public sealed partial class DecisionLogTests
{
    /// <summary>
    /// Packages that are build-only or test-only. §3.4 records them without a runtime
    /// justification, because none of them is in a published package or in an example that runs.
    /// </summary>
    private static readonly HashSet<string> BuildAndTestOnly = new(StringComparer.Ordinal)
    {
        "xunit.v3",
        "xunit.runner.visualstudio",
        "Microsoft.NET.Test.Sdk",
        "FsCheck",
        "FsCheck.Xunit",
        "SharpFuzz",
        "BenchmarkDotNet",
        "Microsoft.CodeAnalysis.BannedApiAnalyzers",
        "Microsoft.CodeAnalysis.PublicApiAnalyzers",
    };

    /// <summary>Every runtime package has a Decision Log row naming its exact pinned version.</summary>
    [Fact]
    public void EveryPackageVersionIsRecorded()
    {
        IReadOnlyDictionary<string, string> pins = Pins();
        string log = File.ReadAllText(RepoLayout.Path("ARCHITECTURE.md"));

        Assert.NotEmpty(pins);

        List<string> missing = [];
        foreach ((string package, string version) in pins)
        {
            if (BuildAndTestOnly.Contains(package))
            {
                continue;
            }

            if (!log.Contains($"`{package}` {version}", StringComparison.Ordinal))
            {
                missing.Add($"{package} {version} has no Decision Log row naming that version");
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    /// <summary>Every version in <c>Directory.Packages.props</c> is an exact pin, never a range.</summary>
    [Fact]
    public void EveryPinIsExact()
    {
        foreach ((string package, string version) in Pins())
        {
            Assert.True(
                ExactVersion().IsMatch(version),
                $"{package} is pinned as \"{version}\", which is not an exact version");
        }
    }

    /// <summary>The six decisions §14.3 assigns to this task are recorded.</summary>
    [Fact]
    public void TheSpecDecisionsAreRecorded()
    {
        string log = File.ReadAllText(RepoLayout.Path("ARCHITECTURE.md"));

        foreach (string decision in new[] { "S-1 / S-2", "S-3", "S-12", "S-15", "S-16", "S-18" })
        {
            Assert.Contains("**" + decision + "**", log, StringComparison.Ordinal);
        }
    }

    private static Dictionary<string, string> Pins()
    {
        Dictionary<string, string> pins = new(StringComparer.Ordinal);

        foreach (Match match in PackageVersion()
            .Matches(File.ReadAllText(RepoLayout.Path("Directory.Packages.props"))))
        {
            pins[match.Groups["id"].Value] = match.Groups["version"].Value;
        }

        return pins;
    }

    [GeneratedRegex("""<PackageVersion\s+Include="(?<id>[^"]+)"\s+Version="(?<version>[^"]+)"\s*/>""")]
    private static partial Regex PackageVersion();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex ExactVersion();
}
