using System.Text;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

/// <summary>The error value itself: truncation, and the two directions of the table.</summary>
[Trait("Category", "Conformance")]
public sealed class ErrorTests
{
    /// <summary>
    /// Rule 53: an ename is capped at ERRMAX - 1 bytes and never split mid-character, so a peer
    /// that decodes it strictly still sees valid UTF-8.
    /// </summary>
    [Fact]
    public void EnameTruncatedAtRuneBoundary()
    {
        // Every rune is three UTF-8 bytes, so 127 bytes cannot be filled exactly: the cut has to
        // fall at 126 bytes, one rune short, rather than half-way through the 43rd character.
        NinePError error = new(new string('中', 64), Errno.EIO);

        string truncated = error.TruncatedEname;

        Assert.Equal(42, truncated.Length);
        Assert.Equal(126, Encoding.UTF8.GetByteCount(truncated));
        Assert.True(Encoding.UTF8.GetByteCount(truncated) <= Constants.ERRMAX - 1);
        Assert.Equal(new string('中', 42), truncated);
    }

    /// <summary>An ename that already fits is handed back unchanged, byte for byte.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("file not found")]
    [InlineData("éèê")]
    public void ShortEnamesAreNotTouched(string ename) =>
        Assert.Equal(ename, new NinePError(ename, Errno.EIO).TruncatedEname);

    /// <summary>An ASCII ename of exactly ERRMAX - 1 bytes is kept whole.</summary>
    [Fact]
    public void AnEnameOfExactlyTheCapSurvives()
    {
        string ename = new('x', Constants.ERRMAX - 1);

        Assert.Equal(ename, new NinePError(ename, Errno.EIO).TruncatedEname);
    }

    /// <summary>Building from an errno and from an ename are inverses on the table's rows.</summary>
    [Fact]
    public void FromErrnoAndFromEnameAgree()
    {
        NinePError fromErrno = NinePError.FromErrno(Errno.ENOTEMPTY);
        NinePError fromEname = NinePError.FromEname("Directory not empty");

        Assert.Equal(fromErrno, fromEname);
    }

    /// <summary>An ename nobody in this workspace writes maps to EIO rather than to a guess.</summary>
    [Fact]
    public void AnUnknownEnameIsEio() =>
        Assert.Equal(Errno.EIO, NinePError.FromEname("something a diod build invented").Errno);
}
