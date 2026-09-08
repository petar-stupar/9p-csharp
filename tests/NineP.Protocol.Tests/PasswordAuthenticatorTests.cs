using System.Globalization;
using NineP.Protocol.Auth;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>The password file of S-15: PBKDF2-HMAC-SHA-256 at 600 000 iterations.</summary>
public sealed class PasswordAuthenticatorTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// The stored line is parsed and its iteration count read back: the KDF cost is a property of
    /// what is on disk, not of what the code intended when it wrote it.
    /// </summary>
    [Fact]
    public void IterationCountIsAtLeast600000()
    {
        string line = PasswordFileStore.HashPassword("correct horse battery staple");
        string[] parts = line.Split('$');

        Assert.Equal(4, parts.Length);
        Assert.Equal(PasswordFileStore.Algorithm, parts[0]);

        int iterations = int.Parse(parts[1], CultureInfo.InvariantCulture);

        Assert.True(iterations >= 600_000, string.Format(
            CultureInfo.InvariantCulture, "{0} iterations is below the 600 000 floor", iterations));
        Assert.Equal(PasswordFileStore.MinimumIterations, iterations);

        // A 128-bit salt and a 256-bit output, as S-15 fixes them.
        Assert.Equal(16, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);
    }

    /// <summary>Below the floor is refused rather than quietly accepted.</summary>
    [Fact]
    public void AWeakerIterationCountIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordFileStore.HashPassword("x", 1000));

    /// <summary>Two hashes of one password differ: the salt is fresh every time.</summary>
    [Fact]
    public void EachHashCarriesItsOwnSalt()
    {
        string first = PasswordFileStore.HashPassword("same password");
        string second = PasswordFileStore.HashPassword("same password");

        Assert.NotEqual(first, second);
        Assert.NotEqual(first.Split('$')[2], second.Split('$')[2]);
    }

    /// <summary>A stored line verifies the password it was made from, and nothing else.</summary>
    [Fact]
    public async Task AStoredLineVerifiesItsOwnPassword()
    {
        string path = WriteFile("glenda:" + PasswordFileStore.HashPassword("correct horse battery staple"));
        try
        {
            PasswordFileStore store = PasswordFileStore.Load(path);

            Assert.NotNull(await store.VerifyAsync("glenda", "correct horse battery staple"u8.ToArray(), Ct));
            Assert.Null(await store.VerifyAsync("glenda", "hunter2"u8.ToArray(), Ct));
            Assert.Null(await store.VerifyAsync("rob", "correct horse battery staple"u8.ToArray(), Ct));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Blank lines and comments are skipped; everything else must be a credential.</summary>
    [Fact]
    public async Task CommentsAndBlankLinesAreSkipped()
    {
        string path = WriteFile(
            "# the operations team's note",
            string.Empty,
            "glenda:" + PasswordFileStore.HashPassword("pw"),
            "   ");

        try
        {
            PasswordFileStore store = PasswordFileStore.Load(path);

            Assert.NotNull(await store.VerifyAsync("glenda", "pw"u8.ToArray(), Ct));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A malformed line fails the load, and the message names the line number.</summary>
    [Theory]
    [InlineData("glenda", 1)]
    [InlineData("glenda:plaintext", 1)]
    [InlineData("glenda:md5$1$aaaa$bbbb", 1)]
    [InlineData("glenda:pbkdf2-sha256$1000$aaaa$bbbb", 1)]
    [InlineData(":pbkdf2-sha256$600000$aaaa$bbbb", 1)]
    public void AMalformedLineNamesItsNumber(string bad, int number)
    {
        string path = WriteFile(bad);
        try
        {
            FormatException failure = Assert.Throws<FormatException>(() => PasswordFileStore.Load(path));

            Assert.Contains(
                string.Format(CultureInfo.InvariantCulture, "line {0}", number),
                failure.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>An iteration count below the floor on disk is a malformed line, not a slow login.</summary>
    [Fact]
    public void AStoredLineBelowTheFloorIsRefused()
    {
        string weak = PasswordFileStore.HashPassword("pw").Replace(
            "$600000$", "$599999$", StringComparison.Ordinal);
        string path = WriteFile("glenda:" + weak);

        try
        {
            Assert.Throws<FormatException>(() => PasswordFileStore.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteFile(params string[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), "ninep-" + Guid.NewGuid().ToString("N") + ".pw");
        File.WriteAllLines(path, lines);
        return path;
    }
}
