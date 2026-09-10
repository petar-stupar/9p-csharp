using NineP.Protocol;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

/// <summary>The qid layout and type bits of reference §4.1.</summary>
[Trait("Category", "Conformance")]
public sealed class QidTests
{
    /// <summary>
    /// The two low type bits follow Linux (<c>QTSYMLINK 0x02</c>, <c>QTLINK 0x01</c>), not the
    /// 9P2000.u draft, which defines <c>QTLINK 0x02</c> and has no <c>QTSYMLINK</c> at all. Every
    /// .u and .L peer this workspace talks to is Linux-derived, so Linux wins.
    /// </summary>
    [Fact]
    public void TypeBitsFollowLinux()
    {
        Assert.Equal(0x01, (byte)QidType.QTLINK);
        Assert.Equal(0x02, (byte)QidType.QTSYMLINK);
        Assert.Equal(0x04, (byte)QidType.QTTMP);
        Assert.Equal(0x08, (byte)QidType.QTAUTH);
        Assert.Equal(0x10, (byte)QidType.QTMOUNT);
        Assert.Equal(0x20, (byte)QidType.QTEXCL);
        Assert.Equal(0x40, (byte)QidType.QTAPPEND);
        Assert.Equal(0x80, (byte)QidType.QTDIR);
        Assert.Equal(0x00, (byte)QidType.QTFILE);
    }

    /// <summary>A qid occupies thirteen bytes: type[1] version[4] path[8].</summary>
    [Fact]
    public void WireSizeIsThirteen() => Assert.Equal(13, Qid.WireSize);

    /// <summary>Two files are the same file exactly when their qids are equal.</summary>
    [Fact]
    public void EqualityIsByAllThreeFields()
    {
        Qid qid = new(QidType.QTDIR, 3, 7);

        Assert.Equal(qid, new Qid(QidType.QTDIR, 3, 7));
        Assert.NotEqual(qid, new Qid(QidType.QTFILE, 3, 7));
        Assert.NotEqual(qid, new Qid(QidType.QTDIR, 4, 7));
        Assert.NotEqual(qid, new Qid(QidType.QTDIR, 3, 8));
    }
}
