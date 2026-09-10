using NineP.Protocol;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

/// <summary>Which type numbers each dialect carries, from the legality table of reference §2.</summary>
[Trait("Category", "Conformance")]
public sealed class DialectLegalityTests
{
    private static readonly MessageType[] Legacy =
    [
        MessageType.Tversion, MessageType.Rversion,
        MessageType.Tauth, MessageType.Rauth,
        MessageType.Tattach, MessageType.Rattach,
        MessageType.Rerror,
        MessageType.Tflush, MessageType.Rflush,
        MessageType.Twalk, MessageType.Rwalk,
        MessageType.Topen, MessageType.Ropen,
        MessageType.Tcreate, MessageType.Rcreate,
        MessageType.Tread, MessageType.Rread,
        MessageType.Twrite, MessageType.Rwrite,
        MessageType.Tclunk, MessageType.Rclunk,
        MessageType.Tremove, MessageType.Rremove,
        MessageType.Tstat, MessageType.Rstat,
        MessageType.Twstat, MessageType.Rwstat,
    ];

    private static readonly MessageType[] Linux =
    [
        MessageType.Rlerror,
        MessageType.Tstatfs, MessageType.Rstatfs,
        MessageType.Tlopen, MessageType.Rlopen,
        MessageType.Tlcreate, MessageType.Rlcreate,
        MessageType.Tsymlink, MessageType.Rsymlink,
        MessageType.Tmknod, MessageType.Rmknod,
        MessageType.Trename, MessageType.Rrename,
        MessageType.Treadlink, MessageType.Rreadlink,
        MessageType.Tgetattr, MessageType.Rgetattr,
        MessageType.Tsetattr, MessageType.Rsetattr,
        MessageType.Txattrwalk, MessageType.Rxattrwalk,
        MessageType.Txattrcreate, MessageType.Rxattrcreate,
        MessageType.Treaddir, MessageType.Rreaddir,
        MessageType.Tfsync, MessageType.Rfsync,
        MessageType.Tlock, MessageType.Rlock,
        MessageType.Tgetlock, MessageType.Rgetlock,
        MessageType.Tlink, MessageType.Rlink,
        MessageType.Tmkdir, MessageType.Rmkdir,
        MessageType.Trenameat, MessageType.Rrenameat,
        MessageType.Tunlinkat, MessageType.Runlinkat,
        MessageType.Tversion, MessageType.Rversion,
        MessageType.Tauth, MessageType.Rauth,
        MessageType.Tattach, MessageType.Rattach,
        MessageType.Tflush, MessageType.Rflush,
        MessageType.Twalk, MessageType.Rwalk,
        MessageType.Tread, MessageType.Rread,
        MessageType.Twrite, MessageType.Rwrite,
        MessageType.Tclunk, MessageType.Rclunk,
        MessageType.Tremove, MessageType.Rremove,
    ];

    /// <summary>Every one of the 66 wire-legal numbers is checked against all three dialects.</summary>
    [Fact]
    public void EveryWireLegalTypeIsCheckedInEveryDialect()
    {
        HashSet<MessageType> legacy = [.. Legacy];
        HashSet<MessageType> linux = [.. Linux];
        HashSet<MessageType> wireLegal = [.. legacy, .. linux];

        Assert.Equal(66, wireLegal.Count);
        Assert.Equal(66, MessageTypeTests.Records.Count);
        Assert.Equal(wireLegal, MessageTypeTests.Records.Keys.ToHashSet());

        foreach (MessageType type in wireLegal)
        {
            Assert.Equal(legacy.Contains(type), MessageTypes.IsLegal(type, Dialect.P9_2000));
            Assert.Equal(legacy.Contains(type), MessageTypes.IsLegal(type, Dialect.P9_2000_u));
            Assert.Equal(linux.Contains(type), MessageTypes.IsLegal(type, Dialect.P9_2000_L));
        }
    }

    /// <summary>9P2000.u carries exactly what 9P2000 carries; only the fields differ.</summary>
    [Fact]
    public void UnixCarriesTheSameTypesAsBase()
    {
        foreach (MessageType type in Enum.GetValues<MessageType>())
        {
            Assert.Equal(MessageTypes.IsLegal(type, Dialect.P9_2000), MessageTypes.IsLegal(type, Dialect.P9_2000_u));
        }
    }

    /// <summary>
    /// A 9P2000.L session carries neither open, create, stat nor wstat, and never an Rerror: its
    /// error reply is Rlerror, which the other two dialects in turn never carry.
    /// </summary>
    [Fact]
    public void DialectsDoNotShareTheirOwnMessages()
    {
        foreach (MessageType only9P2000 in new[]
        {
            MessageType.Topen, MessageType.Ropen, MessageType.Tcreate, MessageType.Rcreate,
            MessageType.Tstat, MessageType.Rstat, MessageType.Twstat, MessageType.Rwstat,
            MessageType.Rerror,
        })
        {
            Assert.True(MessageTypes.IsLegal(only9P2000, Dialect.P9_2000));
            Assert.False(MessageTypes.IsLegal(only9P2000, Dialect.P9_2000_L));
        }

        foreach (MessageType onlyLinux in new[]
        {
            MessageType.Rlerror, MessageType.Tgetattr, MessageType.Treaddir, MessageType.Tlopen,
        })
        {
            Assert.False(MessageTypes.IsLegal(onlyLinux, Dialect.P9_2000));
            Assert.True(MessageTypes.IsLegal(onlyLinux, Dialect.P9_2000_L));
        }
    }

    /// <summary>A type number the protocol does not define is legal in no dialect.</summary>
    [Fact]
    public void UndefinedNumbersAreLegalNowhere()
    {
        foreach (Dialect dialect in Enum.GetValues<Dialect>())
        {
            Assert.False(MessageTypes.IsLegal((MessageType)0, dialect));
            Assert.False(MessageTypes.IsLegal((MessageType)99, dialect));
            Assert.False(MessageTypes.IsLegal((MessageType)200, dialect));
        }
    }
}
