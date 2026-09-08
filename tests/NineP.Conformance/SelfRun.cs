using System.Globalization;
using System.Text;
using System.Text.Json;
using NineP.Protocol;

namespace NineP.Conformance;

/// <summary>The whole self run: every outcome, and whether all of them passed.</summary>
/// <param name="Outcomes">One entry per combination and part.</param>
public sealed record ConformanceReport(IReadOnlyList<ConformanceOutcome> Outcomes)
{
    /// <summary>True when every step of every combination matched the fixture.</summary>
    public bool Passed => Outcomes.All(outcome => outcome.Passed);

    /// <summary>The report as the driver prints it, one line per outcome.</summary>
    /// <returns>The printable report.</returns>
    public override string ToString()
    {
        StringBuilder text = new();
        foreach (ConformanceOutcome outcome in Outcomes)
        {
            text.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0,-4} part {1}  {2}{3}",
                outcome.Passed ? "PASS" : "FAIL",
                outcome.Part,
                outcome.Combination,
                outcome.Passed ? string.Empty : "  -- " + outcome.Detail));
        }

        return text.ToString();
    }
}

/// <summary>
/// Runs the conformance scenario against this repository's own <c>jsonfs</c> and <c>ninep</c>, in
/// all three dialects over every transport this server binds (AC-a, exit criterion 3).
/// </summary>
public static class SelfRun
{
    private static readonly Dialect[] Dialects =
        [Dialect.P9_2000, Dialect.P9_2000_u, Dialect.P9_2000_L];

    /// <summary>The transports Parts A and B run over as two processes on a real socket.</summary>
    private static readonly string[] Transports = ["tcp", "tls", "ws"];

    /// <summary>Runs every combination and returns the report.</summary>
    /// <param name="progress">Called with each outcome as it is produced; null to stay quiet.</param>
    /// <returns>The report.</returns>
    public static async Task<ConformanceReport> RunAsync(Action<ConformanceOutcome>? progress = null)
    {
        string document = await File.ReadAllTextAsync(
            RepoLayout.Path("docs", "9p", "fixtures", "sample.json"));
        string expected = ConformanceText.Utf8.GetString(
            await File.ReadAllBytesAsync(RepoLayout.Path("docs", "9p", "fixtures", "sample.expected.txt")));

        using JsonDocument parsed = JsonDocument.Parse(document);
        List<ConformanceOutcome> outcomes = [];

        foreach (Dialect dialect in Dialects)
        {
            foreach (string transport in Transports)
            {
                await using (ConformanceTarget readOnly =
                    await ProcessTarget.StartAsync(dialect, transport, document))
                {
                    Report(outcomes, progress, await Scenario.PartAAsync(readOnly, parsed, expected));
                    if (transport == "tcp")
                    {
                        foreach (ConformanceOutcome outcome in await Scenario.PartEAsync(readOnly, writable: false))
                        {
                            Report(outcomes, progress, outcome);
                        }
                    }
                }

                await using (ConformanceTarget writable =
                    await ProcessTarget.StartAsync(dialect, transport, document, "--writable"))
                {
                    Report(outcomes, progress, await Scenario.PartBAsync(writable));
                    if (transport == "tcp")
                    {
                        foreach (ConformanceOutcome outcome in await Scenario.PartEAsync(writable, writable: true))
                        {
                            Report(outcomes, progress, outcome);
                        }
                    }
                }
            }

            // The memory transport is in process by construction, so the fourth combination of
            // exit criterion 3 drives the same commands over MemoryTransport instead of a socket.
            await using (ConformanceTarget readOnly = await MemoryTarget.StartAsync(dialect, document, writable: false))
            {
                Report(outcomes, progress, await Scenario.PartAAsync(readOnly, parsed, expected));
                foreach (ConformanceOutcome outcome in await Scenario.PartEAsync(readOnly, writable: false))
                {
                    Report(outcomes, progress, outcome);
                }
            }

            await using (ConformanceTarget writable = await MemoryTarget.StartAsync(dialect, document, writable: true))
            {
                Report(outcomes, progress, await Scenario.PartBAsync(writable));
                foreach (ConformanceOutcome outcome in await Scenario.PartEAsync(writable, writable: true))
                {
                    Report(outcomes, progress, outcome);
                }
            }

            // Part B step 8 is about the document on disk and Part C about what a server does
            // with Tauth: neither is a transport question, so both run once per dialect over TCP.
            Report(outcomes, progress, await Scenario.WriteBackAsync(dialect, document));
            Report(outcomes, progress, await AuthScenario.RunAsync(dialect, document));
        }

        // Part D is in-repo only and is not a dialect or a transport question: it is what one
        // hostile connection costs a live server, so it runs once, over TCP, in 9P2000.L.
        Report(outcomes, progress, await PartD.RunAsync(document));

        return new ConformanceReport(outcomes);
    }

    private static void Report(
        List<ConformanceOutcome> outcomes, Action<ConformanceOutcome>? progress, ConformanceOutcome outcome)
    {
        outcomes.Add(outcome);
        progress?.Invoke(outcome);
    }
}
