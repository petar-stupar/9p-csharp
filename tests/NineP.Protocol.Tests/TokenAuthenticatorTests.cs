using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NineP.Protocol.Auth;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>The default token authenticator, and the rule that it is the only comparison path.</summary>
public sealed class TokenAuthenticatorTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>A token that is not the secret is refused, and no identity is produced.</summary>
    [Fact]
    public async Task WrongTokenRejected()
    {
        byte[] secret = "s3cr3t-token"u8.ToArray();
        byte[] wrong = "s3cr3t-tokeN"u8.ToArray();

        await Assert.ThrowsAsync<NinePException>(async () => await AfidPump.ExchangeAsync(
            new TokenAuthenticator(secret),
            new TokenCredential(wrong),
            new AuthRequest("glenda", 1000, ""),
            cancellationToken: Ct));
    }

    /// <summary>A token of the wrong length cannot match and is refused without being buffered.</summary>
    [Theory]
    [InlineData("short")]
    [InlineData("s3cr3t-token-with-more")]
    public async Task ATokenOfTheWrongLengthIsRejected(string wrong)
    {
        byte[] secret = "s3cr3t-token"u8.ToArray();

        await Assert.ThrowsAsync<NinePException>(async () => await AfidPump.ExchangeAsync(
            new TokenAuthenticator(secret),
            new TokenCredential(Encoding.ASCII.GetBytes(wrong)),
            new AuthRequest("glenda", 1000, ""),
            cancellationToken: Ct));
    }

    /// <summary>A token written in pieces still matches: the client chooses its own chunking.</summary>
    [Fact]
    public async Task ATokenSplitAcrossWritesStillMatches()
    {
        byte[] secret = "s3cr3t-token"u8.ToArray();
        CallbackCredential chunked = new(async (channel, token) =>
        {
            for (int i = 0; i < secret.Length; i++)
            {
                await channel.WriteAsync(secret.AsMemory(i, 1), token);
            }

            ReadOnlyMemory<byte> answer = await channel.ReadAsync(3, token);
            Assert.Equal("ok\n"u8.ToArray(), answer.ToArray());
        });

        Identity? identity = await AfidPump.ExchangeAsync(
            new TokenAuthenticator(secret), chunked, new AuthRequest("glenda", 1000, ""), cancellationToken: Ct);

        Assert.NotNull(identity);
    }

    /// <summary>
    /// The comparison goes through <see cref="ConstantTime"/> and nowhere else: a grep over the
    /// authentication sources is what keeps a future edit from reintroducing an early-exit compare
    /// that leaks a secret one byte at a time.
    /// </summary>
    [Fact]
    public void ComparisonGoesThroughConstantTime()
    {
        string authDirectory = Path.Combine(RepositoryRoot(), "src", "NineP.Protocol", "Auth");
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(authDirectory, "*.cs", SearchOption.AllDirectories))
        {
            string code = CodeOf(file);
            string name = Path.GetFileName(file);

            if (code.Contains("SequenceEqual", StringComparison.Ordinal))
            {
                offenders.Add(string.Format(CultureInfo.InvariantCulture, "{0}: SequenceEqual", name));
            }

            if (code.Contains("FixedTimeEquals", StringComparison.Ordinal)
                && !string.Equals(name, "ConstantTime.cs", StringComparison.Ordinal))
            {
                offenders.Add(string.Format(CultureInfo.InvariantCulture, "{0}: FixedTimeEquals", name));
            }
        }

        Assert.Empty(offenders);

        // And the one place that is allowed to compare bytes really does it in constant time.
        string constantTime = CodeOf(Path.Combine(authDirectory, "ConstantTime.cs"));

        Assert.Contains("CryptographicOperations.FixedTimeEquals", constantTime, StringComparison.Ordinal);
    }

    /// <summary>The repository root: the directory the solution file lives in.</summary>
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NineP.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    /// <summary>A file's code with line comments removed, so prose cannot trip a code check.</summary>
    private static string CodeOf(string file) =>
        Regex.Replace(File.ReadAllText(file), @"//[^\n]*", string.Empty);
}
