using System.Globalization;
using NineP.Conformance;
using NineP.TestSupport.FakeIssuer;

// The conformance driver. "self" runs the scenario of docs/9p/fixtures/conformance.md against
// this repository's own jsonfs and ninep; the exit code is 0 only when every step matched.
// "fake-issuer" runs the §8.4 issuer the suite uses, so that todofs and the cli's OIDC grants can
// be exercised by hand without standing up a Keycloak container.
if (args.Length == 1 && args[0] == "fake-issuer")
{
    return await FakeIssuerHost.RunAsync();
}

if (args.Length != 1 || args[0] != "self")
{
    await Console.Error.WriteLineAsync(
        "usage: dotnet run --project tests/NineP.Conformance -- self | fake-issuer");
    return 3;
}

ConformanceReport report = await SelfRun.RunAsync(outcome => Console.Out.Write(
    string.Format(
        CultureInfo.InvariantCulture,
        "{0,-4} part {1}  {2}{3}{4}",
        outcome.Passed ? "PASS" : "FAIL",
        outcome.Part,
        outcome.Combination,
        outcome.Passed ? string.Empty : "  -- " + outcome.Detail,
        Environment.NewLine)));

await Console.Out.WriteLineAsync();
await Console.Out.WriteLineAsync(string.Format(
    CultureInfo.InvariantCulture,
    "{0}: {1} of {2} checks passed",
    report.Passed ? "conformance" : "CONFORMANCE FAILED",
    report.Outcomes.Count(outcome => outcome.Passed),
    report.Outcomes.Count));

return report.Passed ? 0 : 1;

/// <summary>
/// The in-process OIDC issuer of §8.4, run as a program. It exists because there was no documented
/// way to exercise <c>todofs</c> or the cli's grants by hand short of standing up a Keycloak
/// container: the issuer is a test fixture, and a fixture nobody can start is a fixture that only
/// the suite benefits from.
/// </summary>
internal static class FakeIssuerHost
{
    /// <summary>Starts the issuer, prints what a developer needs, and serves until interrupted.</summary>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync()
    {
        using FakeOidcIssuer issuer = FakeOidcIssuer.Start();
        using CancellationTokenSource stopping = new();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        foreach (string line in Description(issuer))
        {
            await Console.Out.WriteLineAsync(line).ConfigureAwait(false);
        }

        await Console.Out.FlushAsync().ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.Infinite, stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C: the issuer is disposed by the using above.
        }

        return 0;
    }

    private static IEnumerable<string> Description(FakeOidcIssuer issuer)
    {
        yield return Line("issuer", issuer.Issuer);
        yield return Line("audience", issuer.Audience);
        yield return Line("client-id", issuer.ClientId);
        yield return Line("password grant", issuer.PasswordUser + " / " + issuer.Password);
        yield return string.Empty;

        // Pre-minted tokens, because the password grant knows one user and the isolation and
        // admin paths need more than one. Each is a bearer credential: ninep --auth bearer:<token>.
        yield return Line("token glenda", issuer.IssueToken());
        yield return Line(
            "token glenda+admin",
            issuer.IssueToken(new FakeTokenOptions { Roles = ["todofs-admin"] }));
        yield return Line(
            "token bob",
            issuer.IssueToken(new FakeTokenOptions { PreferredUsername = "bob", Subject = "0000-bob" }));
        yield return Line(
            "token expired",
            issuer.IssueToken(new FakeTokenOptions
            {
                NotBefore = TimeSpan.FromHours(-2),
                Lifetime = TimeSpan.FromMinutes(1),
            }));

        yield return string.Empty;
        yield return "the issuer serves plain HTTP on loopback, so todofs needs --allow-insecure-issuer";
        yield return "press Ctrl-C to stop";
    }

    private static string Line(string name, string value) =>
        string.Format(CultureInfo.InvariantCulture, "{0,-18} {1}", name, value);
}
