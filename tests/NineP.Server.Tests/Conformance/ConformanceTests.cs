using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
#if NET10_0_OR_GREATER
using NineP.Conformance;
using Xunit;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// AC-a and exit criterion 3: this repository's own <c>jsonfs</c> and <c>ninep</c> reproduce
/// <c>docs/9p/fixtures/sample.expected.txt</c> byte for byte, in all three dialects over every
/// transport, and pass the mutation and authentication parts of the scenario too.
/// </summary>
/// <remarks>
/// The run is shared between the two tests: it starts dozens of processes and there is no reason
/// to do that twice to make two assertions about one report.
/// </remarks>
[Collection(ConformanceCollection.Name)]
[Trait("Category", "Conformance")]
public sealed class ConformanceTests
{
    private static readonly Lazy<Task<ConformanceReport>> ReportLazy =
        new(() => SelfRun.RunAsync());

    /// <summary>Part A's listing matches the fixture byte for byte in every combination.</summary>
    [Fact]
    public async Task SampleOutputMatchesByteForByte()
    {
        ConformanceReport report = await ReportLazy.Value;

        List<ConformanceOutcome> partA = [.. report.Outcomes.Where(outcome => outcome.Part == "A")];

        Assert.Equal(12, partA.Count);
        Assert.True(
            partA.TrueForAll(outcome => outcome.Passed),
            string.Join(Environment.NewLine, partA.Where(outcome => !outcome.Passed)));
    }

    /// <summary>Every part of the scenario passes in every dialect over every transport.</summary>
    [Fact]
    public async Task AllDialectsAllTransports()
    {
        ConformanceReport report = await ReportLazy.Value;

        Assert.True(report.Passed, report.ToString());

        // Three dialects times four transports for Parts A and B, plus the write-back and
        // authentication parts once per dialect, plus Part D's hostile client once.
        Assert.Equal(145, report.Outcomes.Count);
        for (int i = 1; i <= 19; i++)
        {
            string id = "E" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(6, report.Outcomes.Count(outcome => outcome.Part == id));
        }
        Assert.Equal(report.Outcomes.Count, report.Outcomes.Select(outcome => (outcome.Part, outcome.Combination)).Distinct().Count());
    }
}

/// <summary>
/// The conformance run owns the machine while it lasts: it spawns two processes per cli
/// invocation and binds a listener per combination.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConformanceCollection
{
    /// <summary>The collection name the conformance suite names.</summary>
    public const string Name = "conformance";
}
#endif
