using System.Globalization;
using NineP.Protocol;

namespace NineP.Conformance;

/// <summary>
/// Part C of <c>docs/9p/fixtures/conformance.md</c>: what a server does with <c>Tauth</c>, and
/// what a client does with the answer. Part C.4 is the OIDC grant, which the fixture itself says
/// runs in the repository's own test suite against the fake issuer rather than cross-language.
/// </summary>
internal static class AuthScenario
{
    /// <summary>Runs Part C.1 to C.3 for one dialect over TCP.</summary>
    /// <param name="dialect">The dialect to negotiate.</param>
    /// <param name="document">The document to serve.</param>
    /// <returns>The outcome.</returns>
    public static async Task<ConformanceOutcome> RunAsync(Dialect dialect, string document)
    {
        string name = string.Format(
            CultureInfo.InvariantCulture,
            "{0} over tcp",
            NineP.Protocol.Negotiation.Negotiator.VersionString(dialect));

        try
        {
            await TokenServerAsync(dialect, document);
            await NoAuthenticatorAsync(dialect, document);
            await DialectRefusalAsync(dialect, document);

            return new ConformanceOutcome(name, "C", true, string.Empty);
        }
        catch (ConformanceFailure failure)
        {
            return new ConformanceOutcome(name, "C", false, failure.Message);
        }
    }

    /// <summary>C.1: the right token attaches, a wrong one and no token do not.</summary>
    private static async Task TokenServerAsync(Dialect dialect, string document)
    {
        await using ProcessTarget target = await ProcessTarget
            .StartAsync(dialect, "tcp", document, "--auth", "token:s3cret");

        Expect(await target.RunAsync(["--auth", "token:s3cret", "ls", "/"]), 0, "attach with the right token");
        Expect(await target.RunAsync(["--auth", "token:wrong", "ls", "/"]), 2, "attach with a wrong token");
        Expect(await target.RunAsync(["ls", "/"]), 2, "attach with no credential");
    }

    /// <summary>C.2: a server with no authenticator refuses Tauth, and --auth-optional falls back.</summary>
    private static async Task NoAuthenticatorAsync(Dialect dialect, string document)
    {
        await using ProcessTarget target = await ProcessTarget.StartAsync(dialect, "tcp", document);

        CliResult refused = await target.RunAsync(["--auth", "token:x", "version"]);
        Expect(refused, 2, "Tauth against a server that does not authenticate");

        string wanted = dialect == Dialect.P9_2000_L
            ? string.Format(CultureInfo.InvariantCulture, "(errno {0})", Errno.ECONNREFUSED)
            : "authentication not required";

        if (!refused.Stderr.Contains(wanted, StringComparison.Ordinal))
        {
            throw new ConformanceFailure(string.Format(
                CultureInfo.InvariantCulture,
                "the refusal reported \"{0}\", expected {1}",
                refused.Stderr.Trim(),
                wanted));
        }

        Expect(
            await target.RunAsync(["--auth", "token:x", "--auth-optional", "version"]),
            0,
            "--auth-optional falling back to a NOFID attach");
    }

    /// <summary>
    /// C.3: a server told to speak one dialect never answers with another. Reference §5.1 step 4
    /// leaves it two ways out — the dialect it was told to speak, which is a legal downgrade, or
    /// <c>"unknown"</c>, which the client reports as a version error — and forbids everything
    /// else. Both of the fixture's cases fall out of that rule.
    /// </summary>
    private static async Task DialectRefusalAsync(Dialect dialect, string document)
    {
        await using ProcessTarget target = await ProcessTarget.StartAsync(dialect, "tcp", document);

        string served = NineP.Protocol.Negotiation.Negotiator.VersionString(dialect);
        Expect(await target.RunAsync(["version"]), 0, "a client asking for the served dialect");

        foreach (Dialect asked in new[] { Dialect.P9_2000, Dialect.P9_2000_u, Dialect.P9_2000_L })
        {
            if (asked == dialect)
            {
                continue;
            }

            CliResult mismatched = await target.RunAsync(
                ["--dialect", NineP.Protocol.Negotiation.Negotiator.VersionString(asked), "version"]);

            if (mismatched.ExitCode == 1)
            {
                continue;
            }

            Expect(mismatched, 0, "a client asking for a dialect the server was told not to speak");

            string wanted = string.Format(CultureInfo.InvariantCulture, "dialect={0} ", served);
            if (!mismatched.Text.StartsWith(wanted, StringComparison.Ordinal))
            {
                throw new ConformanceFailure(string.Format(
                    CultureInfo.InvariantCulture,
                    "a server told to speak only {0} negotiated \"{1}\"",
                    served,
                    mismatched.Text.Trim()));
            }
        }
    }

    private static void Expect(CliResult result, int exitCode, string what)
    {
        if (result.ExitCode != exitCode)
        {
            throw new ConformanceFailure(string.Format(
                CultureInfo.InvariantCulture,
                "{0} exited {1}, expected {2}: {3}",
                what,
                result.ExitCode,
                exitCode,
                result.Stderr.Trim()));
        }
    }
}
