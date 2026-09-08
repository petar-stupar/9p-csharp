using System.Text;
using NineP.Protocol.Internal;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>The sanitiser of reference §8 rule 11 (IR-1: it outlived the logger it used to sit on).</summary>
public sealed class UntrustedTextTests
{
    /// <summary>
    /// Rule 54: an untrusted string is escaped and capped at 256 bytes before it is logged, so a
    /// peer cannot forge a log line with a newline or hide inside a megabyte of text.
    /// </summary>
    [Fact]
    public void UntrustedStringsEscapedAndCapped()
    {
        string forged = "ok\nError root logged in\r\n\u0000\u001B[31m";

        string sanitized = UntrustedText.Sanitize(forged);

        Assert.DoesNotContain('\n', sanitized);
        Assert.DoesNotContain('\r', sanitized);
        Assert.DoesNotContain('\u0000', sanitized);
        Assert.DoesNotContain('\u001B', sanitized);
        Assert.Equal("ok\\nError root logged in\\r\\n\\u0000\\u001B[31m", sanitized);

        string flood = UntrustedText.Sanitize(new string('x', 4096));

        Assert.Equal(UntrustedText.SanitizedMaxBytes, Encoding.UTF8.GetByteCount(flood));
    }

    /// <summary>The cap counts UTF-8 bytes and never splits a character.</summary>
    [Fact]
    public void TheCapFallsOnARuneBoundary()
    {
        string sanitized = UntrustedText.Sanitize(new string('中', 200));

        Assert.Equal(85, sanitized.Length);
        Assert.Equal(255, Encoding.UTF8.GetByteCount(sanitized));
    }

    /// <summary>A backslash is escaped too, or the escapes themselves could be forged.</summary>
    [Fact]
    public void BackslashesAreEscaped() =>
        Assert.Equal("a\\\\nb", UntrustedText.Sanitize("a\\nb"));



    /// <summary>A null argument is a caller's bug, not a silently dropped record.</summary>
    [Fact]
    public void NullArgumentsThrow() =>
        Assert.Throws<ArgumentNullException>(() => UntrustedText.Sanitize(null!));
}
