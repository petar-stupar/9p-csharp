using NineP.Protocol;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

/// <summary>Every constant of reference §1, checked against the reference's own table.</summary>
[Trait("Category", "Conformance")]
public sealed class ConstantsTests
{
    /// <summary>The numeric constants of reference §1 under their fcall.h / linux-9p.h names.</summary>
    [Theory]
    [InlineData("NOTAG", Constants.NOTAG, 0xFFFFL)]
    [InlineData("NOFID", Constants.NOFID, 0xFFFFFFFFL)]
    [InlineData("NONUNAME", Constants.NONUNAME, 0xFFFFFFFFL)]
    [InlineData("MAXWELEM", Constants.MAXWELEM, 16L)]
    [InlineData("IOHDRSZ", Constants.IOHDRSZ, 24L)]
    [InlineData("READDIRHDRSZ", Constants.READDIRHDRSZ, 24L)]
    [InlineData("ERRMAX", Constants.ERRMAX, 128L)]
    [InlineData("STATFIXLEN", Constants.STATFIXLEN, 49L)]
    [InlineData("HDRSZ", Constants.HDRSZ, 7L)]
    [InlineData("TwriteHeaderSize", Constants.TwriteHeaderSize, 23L)]
    [InlineData("RreadHeaderSize", Constants.RreadHeaderSize, 11L)]
    [InlineData("MaxNameLength", Constants.MaxNameLength, 255L)]
    public void NumericConstantsMatchTheReference(string name, long actual, long expected) =>
        Assert.True(actual == expected, $"{name} is {actual}; reference §1 says {expected}");

    /// <summary>The version strings are exactly the bytes the wire carries.</summary>
    [Theory]
    [InlineData(Constants.Version9P2000, "9P2000")]
    [InlineData(Constants.Version9P2000u, "9P2000.u")]
    [InlineData(Constants.Version9P2000L, "9P2000.L")]
    [InlineData(Constants.VersionUnknown, "unknown")]
    [InlineData(Constants.WebSocketSubprotocol, "9p")]
    public void VersionStringsMatchTheReference(string actual, string expected) =>
        Assert.Equal(expected, actual);

    /// <summary>
    /// The header sizes are not IOHDRSZ: reference §1 says the real Rread header is 11 bytes and
    /// the real Twrite header 23, and IOHDRSZ (24) is the conventional over-estimate. Confusing
    /// the two is what turns a maximal legal Twrite into a spurious "malformed" (reference §8
    /// rule 4).
    /// </summary>
    [Fact]
    public void ServiceAllowanceIsNotTheHeaderSize()
    {
        Assert.Equal(24, Constants.IOHDRSZ);
        Assert.Equal(23, Constants.TwriteHeaderSize);
        Assert.Equal(11, Constants.RreadHeaderSize);
        Assert.NotEqual(Constants.IOHDRSZ, Constants.TwriteHeaderSize);
    }

    /// <summary>NOFID and NONUNAME are the same bit pattern but mean different things.</summary>
    [Fact]
    public void NoFidAndNonUnameAreBothAllOnes()
    {
        Assert.Equal(uint.MaxValue, Constants.NOFID);
        Assert.Equal(uint.MaxValue, Constants.NONUNAME);
        Assert.Equal(ushort.MaxValue, Constants.NOTAG);
    }
}
