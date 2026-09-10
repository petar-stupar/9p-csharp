using NineP.Client.Tests;
using NineP.Client.Tests.Conformance;
#if NET10_0_OR_GREATER
using System.Text;
using NineP.Cli;
using Xunit;

namespace NineP.Client.Tests.Conformance;

/// <summary>
/// The listing order is <b>bytewise over the UTF-8 bytes</b> (C locale), which is neither
/// <see cref="string.CompareTo(string)"/> nor an ordinal UTF-16 comparison. The three agree on
/// ASCII and disagree above U+FFFF, which is why the test uses a name there (RK-70).
/// </summary>
[Collection(CliCollection.Name)]
[Trait("Category", "Conformance")]
public sealed class CliListingTests
{
    /// <summary>
    /// U+1F600 is above the BMP and is stored as a surrogate pair, whose first unit is U+D83D.
    /// U+FFFD is below the surrogate range in UTF-8's byte order but above it in UTF-16's unit
    /// order, so ordinal UTF-16 and bytewise UTF-8 put the two names in opposite orders.
    /// </summary>
    [Fact]
    public void SortsBytewiseNotByCultureOrUtf16()
    {
        const string astral = "\U0001F600";
        const string replacement = "�";

        Assert.True(CliCommands.CompareBytewise(replacement, astral) < 0);
        Assert.True(string.CompareOrdinal(replacement, astral) > 0);

        List<string> names = [astral, replacement, "b", "A"];
        names.Sort(CliCommands.CompareBytewise);

        Assert.Equal(["A", "b", replacement, astral], names);

        // Bytewise really is the UTF-8 byte order, not a property of these two names alone.
        for (int i = 1; i < names.Count; i++)
        {
            byte[] left = Utf8.GetBytes(names[i - 1]);
            byte[] right = Utf8.GetBytes(names[i]);
            Assert.True(left.AsSpan().SequenceCompareTo(right.AsSpan()) < 0);
        }
    }

    /// <summary>A listing of names above the BMP comes back from the server in that same order.</summary>
    [Fact]
    public async Task ServerListingIsSortedBytewise()
    {
        await using CliHarness harness = await CliHarness.StartAsync(
            "{\"�\":1,\"\U0001F600\":2,\"b\":3,\"A\":4}");

        CliRun run = await harness.RunAsync(["ls", "/"]);

        run.Expect(0);
        Assert.Equal("A\nb\n�\n\U0001F600\n", run.StdoutText);
    }

    /// <summary>
    /// <c>-l</c> is ordered by name like plain <c>ls</c>, and its middle column is the entry's own
    /// size. Both used to be wrong the same way: the composed lines were sorted, so the listing
    /// came out in kind order, and the column held the qid path — a number that reads exactly like
    /// a size without being one.
    /// <b>Mutation:</b> sort the finished strings instead of the entries and <c>d</c> leads;
    /// print <c>entry.Qid.Path</c> instead of the size and the three numbers stop matching the
    /// documents they describe.
    /// </summary>
    [Fact]
    public async Task LongListingIsOrderedByNameAndShowsSizes()
    {
        // A directory first alphabetically and three files of different, known lengths: kind order
        // and name order disagree on the first line, and each size is its own value.
        await using CliHarness harness = await CliHarness.StartAsync(
            """{"a":{"inner":1},"m":"12345","z":"1234567890"}""");

        CliRun run = await harness.RunAsync(["ls", "-l", "/"]);

        run.Expect(0);
        Assert.Equal("dir 0 a/\nfile 5 m\nfile 10 z\n", run.StdoutText);

        // Plain ls is untouched: the fixture's contract is the one it fixes.
        Assert.Equal("a/\nm\nz\n", (await harness.RunAsync(["ls", "/"])).Expect(0).StdoutText);
    }

    private static Encoding Utf8 { get; } = new UTF8Encoding(false, throwOnInvalidBytes: true);
}
#endif
