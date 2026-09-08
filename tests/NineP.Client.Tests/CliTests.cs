#if NET10_0_OR_GREATER
using System.Globalization;
using System.Text;
using Xunit;

namespace NineP.Client.Tests;

/// <summary>
/// The cli's output formats are frozen by <c>docs/9p/fixtures/conformance.md</c>: every language
/// in the workspace diffs its own cli against one expected file, so a byte that changes here
/// changes the cross-language contract. Each format is checked against the process's own bytes.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class CliTests
{
    private const string Document = """
        {"name":"conformance","version":1,"enabled":true,"dir":{"leaf":"x"},"list":["a","b"]}
        """;

    /// <summary><c>version</c> prints the negotiated dialect and msize, and nothing else.</summary>
    /// <param name="dialect">The dialect to ask for.</param>
    /// <returns>A task that completes when the format has been checked.</returns>
    [Theory]
    [InlineData("9P2000")]
    [InlineData("9P2000.u")]
    [InlineData("9P2000.L")]
    public async Task VersionPrintsDialectAndMsize(string dialect)
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document);
        CliRun run = await harness.RunAsync(["version"], dialect);

        run.Expect(0);
        Assert.StartsWith(
            string.Format(CultureInfo.InvariantCulture, "dialect={0} msize=", dialect),
            run.StdoutText,
            StringComparison.Ordinal);
        Assert.EndsWith("\n", run.StdoutText, StringComparison.Ordinal);

        uint msize = uint.Parse(
            run.StdoutText.Split("msize=")[1].Trim(), CultureInfo.InvariantCulture);
        Assert.True(msize >= 4096);
    }

    /// <summary><c>ls</c> prints one entry per line, directories suffixed, bytewise-sorted.</summary>
    [Fact]
    public async Task ListPrintsSortedEntriesWithDirectorySuffix()
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document);
        CliRun run = await harness.RunAsync(["ls", "/"]);

        run.Expect(0);
        Assert.Equal("dir/\nenabled\nlist/\nname\nversion\n", run.StdoutText);
    }

    /// <summary><c>cat</c> prints the file's bytes and adds nothing at all.</summary>
    [Fact]
    public async Task CatPrintsRawBytes()
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document);

        Assert.Equal("conformance"u8.ToArray(), (await harness.RunAsync(["cat", "/name"])).Stdout);
        Assert.Equal("1"u8.ToArray(), (await harness.RunAsync(["cat", "/version"])).Stdout);
        Assert.Equal("true"u8.ToArray(), (await harness.RunAsync(["cat", "/enabled"])).Stdout);
    }

    /// <summary><c>stat</c> prints the frozen five-field line.</summary>
    [Fact]
    public async Task StatPrintsTheFrozenFields()
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document);

        string directory = (await harness.RunAsync(["stat", "/dir"])).StdoutText;
        string file = (await harness.RunAsync(["stat", "/name"])).StdoutText;

        Assert.StartsWith("kind=dir size=0 perm=755 qid=128.", directory, StringComparison.Ordinal);
        Assert.StartsWith("kind=file size=11 perm=644 qid=0.", file, StringComparison.Ordinal);
        Assert.EndsWith("\n", file, StringComparison.Ordinal);
        Assert.Equal(3, file.Split("qid=")[1].TrimEnd('\n').Split('.').Length);
    }

    /// <summary><c>write</c> takes standard input, truncates, and prints the count it wrote.</summary>
    [Fact]
    public async Task WritePrintsWroteCount()
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document, "--writable");

        CliRun written = await harness.RunAsync(["write", "/name"], stdin: "changed"u8.ToArray());

        written.Expect(0);
        Assert.Equal("wrote 7\n", written.StdoutText);
        Assert.Equal("changed"u8.ToArray(), (await harness.RunAsync(["cat", "/name"])).Stdout);
    }

    /// <summary><c>mkdir</c>, <c>mv</c> and <c>rm</c> print nothing at all on success.</summary>
    [Fact]
    public async Task SilentCommandsPrintNothing()
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document, "--writable");

        Assert.Empty((await harness.RunAsync(["mkdir", "/fresh"])).Stdout);
        (await harness.RunAsync(["write", "/fresh/f"], stdin: "x"u8.ToArray())).Expect(0);
        Assert.Empty((await harness.RunAsync(["mv", "/fresh/f", "/fresh/g"])).Stdout);
        Assert.Equal("g\n", (await harness.RunAsync(["ls", "/fresh"])).StdoutText);
        Assert.Empty((await harness.RunAsync(["rm", "/fresh/g"])).Stdout);
        Assert.Empty((await harness.RunAsync(["rm", "/fresh"])).Stdout);
        Assert.DoesNotContain("fresh", (await harness.RunAsync(["ls", "/"])).StdoutText, StringComparison.Ordinal);
    }

    /// <summary>Every failure prints the one frozen error line, on standard error.</summary>
    [Fact]
    public async Task ErrorsUseTheFrozenFormat()
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document);
        CliRun run = await harness.RunAsync(["cat", "/missing"]);

        run.Expect(2);
        Assert.Empty(run.Stdout);
        Assert.Equal(
            string.Format(CultureInfo.InvariantCulture, "error: file not found (errno {0})", 2),
            run.Stderr.Trim());
    }

    /// <summary>
    /// Conformance Part A step 5: the read-only server refuses a write with <c>EROFS</c>, and the
    /// same errno and wording reach the client in every dialect — 9P2000 carries only the ename
    /// and 9P2000.L only the errno, so the pair has to be one <see cref="NineP.Protocol.ErrorTable"/>
    /// row or the two dialects would disagree.
    /// </summary>
    /// <param name="dialect">The dialect to ask for.</param>
    /// <returns>A task that completes when the refusal has been checked.</returns>
    [Theory]
    [InlineData("9P2000")]
    [InlineData("9P2000.u")]
    [InlineData("9P2000.L")]
    public async Task ReadOnlyServerRefusesWrite(string dialect)
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document);
        CliRun run = await harness.RunAsync(["write", "/name"], dialect, "no"u8.ToArray());

        run.Expect(2);
        Assert.Equal("error: read-only file system (errno 30)", run.Stderr.Trim());
    }

    /// <summary>Conformance Part A step 5: <c>..</c> at the root walks to the root.</summary>
    [Fact]
    public async Task DotDotAtRootIsRoot()
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document);

        CliRun root = await harness.RunAsync(["ls", "/"]);
        CliRun above = await harness.RunAsync(["ls", "/dir/../.."]);

        above.Expect(0);
        Assert.Equal(root.StdoutText, above.StdoutText);
    }

    /// <summary>Conformance Part A step 5: listing a file is <c>ENOTDIR</c> in every dialect.</summary>
    /// <param name="dialect">The dialect to ask for.</param>
    /// <returns>A task that completes when the refusal has been checked.</returns>
    [Theory]
    [InlineData("9P2000")]
    [InlineData("9P2000.u")]
    [InlineData("9P2000.L")]
    public async Task ListingAFileIsNotADirectory(string dialect)
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document);
        CliRun run = await harness.RunAsync(["ls", "/name"], dialect);

        run.Expect(2);
        Assert.Equal("error: not a directory (errno 20)", run.Stderr.Trim());
    }

    /// <summary>
    /// Conformance Part C.3: a server told to speak one dialect answers <c>"unknown"</c> to a
    /// client asking for another, and the client reports a version error rather than downgrading.
    /// </summary>
    [Fact]
    public async Task RefusedDialectIsAVersionError()
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document, "--dialects", "9P2000.L");
        CliRun run = await harness.RunAsync(["version"], "9P2000");

        run.Expect(1);
        Assert.Empty(run.Stdout);
        Assert.Contains("ninep:", run.Stderr, StringComparison.Ordinal);
    }

    /// <summary>Conformance Part C.1 and C.2: the three ways an attach can go with a token server.</summary>
    [Fact]
    public async Task TokenAuthenticationRoundTrips()
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document, "--auth", "token:s3cret");

        (await harness.RunAsync(["--auth", "token:s3cret", "ls", "/"])).Expect(0);
        (await harness.RunAsync(["--auth", "token:wrong", "ls", "/"])).Expect(2);
        (await harness.RunAsync(["ls", "/"])).Expect(2);
    }

    /// <summary>
    /// Conformance Part C.2: against a server with no authenticator, a client carrying a token is
    /// told <c>Tauth</c> was refused, and only <c>--auth-optional</c> falls back to a NOFID attach.
    /// The fallback is exercised on a command that needs the attach root — <c>version</c> never
    /// attaches at all, so it passed even while every other command crashed on a null root.
    /// The fallback also says so on standard error: it used to be silent, so a caller who asked
    /// for a credential and got an anonymous session had nothing to tell them apart. Standard
    /// output is byte-identical either way, which is what the conformance fixture freezes.
    /// <b>Mutation:</b> stop publishing the root from the uname overload of
    /// <c>NinePSession.AttachAsync</c> and the two listings below fail, while the two
    /// <c>version</c> lines keep passing.
    /// </summary>
    [Fact]
    public async Task AuthOptionalFallsBackToNofid()
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document, "--auth", "none");

        (await harness.RunAsync(["--auth", "token:x", "version"])).Expect(2);
        (await harness.RunAsync(["--auth", "token:x", "--auth-optional", "version"])).Expect(0);

        CliRun listed = (await harness.RunAsync(["--auth", "token:x", "--auth-optional", "ls", "/"]))
            .Expect(0);
        Assert.Equal("dir/\nenabled\nlist/\nname\nversion\n", listed.StdoutText);
        Assert.Equal(
            "ninep: server requires no authentication; attached anonymously",
            listed.Stderr.TrimEnd('\n'));

        CliRun read = (await harness.RunAsync(["--auth", "token:x", "--auth-optional", "cat", "/name"]))
            .Expect(0);
        Assert.Equal("conformance", read.StdoutText);
    }
}
#endif
