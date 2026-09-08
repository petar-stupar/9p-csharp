using System.Globalization;
using System.Text.Json;
using NineP.Protocol;

namespace NineP.Conformance;

/// <summary>One combination's verdict.</summary>
/// <param name="Combination">Which dialect and transport it was.</param>
/// <param name="Part">Which part of the scenario.</param>
/// <param name="Passed">Whether every step matched.</param>
/// <param name="Detail">What went wrong, when something did.</param>
public readonly record struct ConformanceOutcome(
    string Combination, string Part, bool Passed, string Detail);

/// <summary>
/// The scenario of <c>docs/9p/fixtures/conformance.md</c>: Part A's read-only walk and five
/// negative checks, Part B's eight mutation steps, and Part C's authentication cases.
/// </summary>
internal static partial class Scenario
{
    /// <summary>Part A: the listing, byte-compared with the fixture, plus the negative checks.</summary>
    /// <param name="target">The read-only server and its cli.</param>
    /// <param name="document">The document being served.</param>
    /// <param name="expected">The bytes of <c>sample.expected.txt</c>.</param>
    /// <returns>The outcome.</returns>
    public static async Task<ConformanceOutcome> PartAAsync(
        ConformanceTarget target, JsonDocument document, string expected)
    {
        try
        {
            CliResult version = await target.RunAsync(["version"]);
            Require(version, 0, "version");

            string wanted = string.Format(
                CultureInfo.InvariantCulture,
                "dialect={0} msize=",
                NineP.Protocol.Negotiation.Negotiator.VersionString(target.Dialect));

            if (!version.Text.StartsWith(wanted, StringComparison.Ordinal))
            {
                throw new ConformanceFailure("version printed " + version.Text.Trim());
            }

            uint msize = uint.Parse(
                version.Text.Split("msize=")[1].Trim(), CultureInfo.InvariantCulture);
            if (msize < 4096)
            {
                throw new ConformanceFailure("negotiated msize " + msize.ToString(CultureInfo.InvariantCulture));
            }

            string actual = await Expectation.ComposeAsync(target, document);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new ConformanceFailure(Diff(expected, actual));
            }

            await NegativeChecksAsync(target);
            return new ConformanceOutcome(target.Name, "A", true, string.Empty);
        }
        catch (ConformanceFailure failure)
        {
            return new ConformanceOutcome(target.Name, "A", false, failure.Message);
        }
    }

    /// <summary>Part B: the eight mutation steps, against a <c>--writable</c> server.</summary>
    /// <param name="target">The writable server and its cli.</param>
    /// <returns>The outcome.</returns>
    public static async Task<ConformanceOutcome> PartBAsync(ConformanceTarget target)
    {
        try
        {
            // 1: a write replaces a scalar's value and reports the count it wrote.
            Require(await target.RunAsync(["write", "/name"], "changed"u8.ToArray()), 0, "write /name");
            RequireText(await target.RunAsync(["cat", "/name"]), "changed", "cat /name");

            // 2: mkdir then a write into it, which creates the file.
            Require(await target.RunAsync(["mkdir", "/newdir"]), 0, "mkdir /newdir");
            Require(await target.RunAsync(["write", "/newdir/f"], "x"u8.ToArray()), 0, "write /newdir/f");
            RequireText(await target.RunAsync(["ls", "/newdir"]), "f\n", "ls /newdir");

            // 3: a rename inside one directory.
            Require(await target.RunAsync(["mv", "/newdir/f", "/newdir/g"]), 0, "mv");
            RequireText(await target.RunAsync(["ls", "/newdir"]), "g\n", "ls /newdir after mv");

            // 4: removals, and the directory is gone from the root listing.
            Require(await target.RunAsync(["rm", "/newdir/g"]), 0, "rm /newdir/g");
            Require(await target.RunAsync(["rm", "/newdir"]), 0, "rm /newdir");
            if ((await target.RunAsync(["ls", "/"])).Text.Contains("newdir", StringComparison.Ordinal))
            {
                throw new ConformanceFailure("newdir survived its removal");
            }

            // 5: a non-empty directory is ENOTEMPTY.
            RequireError(await target.RunAsync(["rm", "/dir"]), Errno.ENOTEMPTY, "rm /dir");

            // 6: an array accepts its next index and nothing else.
            Require(await target.RunAsync(["write", "/list/6"], "7"u8.ToArray()), 0, "write /list/6");
            RequireFailure(await target.RunAsync(["write", "/list/9"], "7"u8.ToArray()), "write /list/9");

            // 7: the documented demotion — a boolean written with text becomes a string.
            Require(await target.RunAsync(["write", "/enabled"], "yes"u8.ToArray()), 0, "write /enabled");
            RequireText(await target.RunAsync(["cat", "/enabled"]), "yes", "cat /enabled");

            return new ConformanceOutcome(target.Name, "B", true, string.Empty);
        }
        catch (ConformanceFailure failure)
        {
            return new ConformanceOutcome(target.Name, "B", false, failure.Message);
        }
    }

    /// <summary>
    /// Part B step 8: with <c>--write-back</c>, a change survives a restart of the server. It runs
    /// over TCP alone because it is about the document on disk and not about the transport.
    /// </summary>
    /// <param name="dialect">The dialect to negotiate.</param>
    /// <param name="document">The document to serve.</param>
    /// <returns>The outcome.</returns>
    public static async Task<ConformanceOutcome> WriteBackAsync(Dialect dialect, string document)
    {
        string name = string.Format(
            CultureInfo.InvariantCulture,
            "{0} over tcp",
            NineP.Protocol.Negotiation.Negotiator.VersionString(dialect));

        string workspace = Path.Combine(
            Path.GetTempPath(), "ninep-writeback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        string path = Path.Combine(workspace, "doc.json");

        try
        {
            await File.WriteAllTextAsync(path, document);

            await using (ConformanceTarget first = await ProcessTarget.StartAtAsync(
                dialect, "tcp", path, ownsDocument: false, "--writable", "--write-back"))
            {
                Require(await first.RunAsync(["write", "/name"], "persisted"u8.ToArray()), 0, "write /name");
            }

            await using (ConformanceTarget second = await ProcessTarget.StartAtAsync(
                dialect, "tcp", path, ownsDocument: false))
            {
                RequireText(await second.RunAsync(["cat", "/name"]), "persisted", "cat /name after a restart");
            }

            return new ConformanceOutcome(name, "B8", true, string.Empty);
        }
        catch (ConformanceFailure failure)
        {
            return new ConformanceOutcome(name, "B8", false, failure.Message);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    /// <summary>Part A step 5: the five checks that must fail, and how.</summary>
    /// <param name="target">The read-only server and its cli.</param>
    /// <returns>A task that completes when every check has been made.</returns>
    private static async Task NegativeChecksAsync(ConformanceTarget target)
    {
        RequireError(await target.RunAsync(["cat", "/missing"]), Errno.ENOENT, "cat /missing");

        // The fixture reads a directory through cat in .L, where it is EISDIR, and lists a file in
        // the 9P2000 dialects, where a directory read is a listing rather than an error.
        if (target.Dialect == Dialect.P9_2000_L)
        {
            RequireError(await target.RunAsync(["cat", "/dir"]), Errno.EISDIR, "cat /dir");
        }
        else
        {
            RequireError(await target.RunAsync(["ls", "/name"]), Errno.ENOTDIR, "ls /name");
        }

        RequireError(
            await target.RunAsync(["write", "/name"], "no"u8.ToArray()), Errno.EROFS, "write on a read-only server");

        CliResult root = await target.RunAsync(["ls", "/"]);
        CliResult above = await target.RunAsync(["ls", "/dir/../.."]);
        Require(above, 0, "ls /dir/../..");

        if (!string.Equals(root.Text, above.Text, StringComparison.Ordinal))
        {
            throw new ConformanceFailure("\"..\" at the root did not list the root");
        }
    }

    private static void Require(CliResult result, int exitCode, string what)
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

    private static void RequireText(CliResult result, string expected, string what)
    {
        Require(result, 0, what);

        if (!string.Equals(result.Text, expected, StringComparison.Ordinal))
        {
            throw new ConformanceFailure(string.Format(
                CultureInfo.InvariantCulture, "{0} printed \"{1}\", expected \"{2}\"", what, result.Text, expected));
        }
    }

    private static void RequireFailure(CliResult result, string what)
    {
        if (result.ExitCode == 0)
        {
            throw new ConformanceFailure(what + " succeeded, which the fixture forbids");
        }
    }

    /// <summary>The step must fail with exit 2 and the errno the fixture names.</summary>
    /// <param name="result">What the cli produced.</param>
    /// <param name="errno">The errno the error line must carry.</param>
    /// <param name="what">The step, for the report.</param>
    private static void RequireError(CliResult result, int errno, string what)
    {
        Require(result, 2, what);

        string wanted = string.Format(CultureInfo.InvariantCulture, "(errno {0})", errno);
        if (!result.Stderr.Contains(wanted, StringComparison.Ordinal))
        {
            throw new ConformanceFailure(string.Format(
                CultureInfo.InvariantCulture,
                "{0} reported \"{1}\", expected {2}",
                what,
                result.Stderr.Trim(),
                wanted));
        }
    }

    /// <summary>The first line that differs, which is what a reader needs from a failed diff.</summary>
    /// <param name="expected">The fixture.</param>
    /// <param name="actual">What this run composed.</param>
    /// <returns>A one-line description of the first difference.</returns>
    private static string Diff(string expected, string actual)
    {
        string[] left = expected.Split('\n');
        string[] right = actual.Split('\n');

        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            string a = i < left.Length ? left[i] : "<end of file>";
            string b = i < right.Length ? right[i] : "<end of file>";

            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "line {0}: expected \"{1}\", got \"{2}\"",
                    i + 1,
                    a,
                    b);
            }
        }

        return "the listings differ in length only";
    }
}
