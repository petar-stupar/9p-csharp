using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>Twstat and Tsetattr both become one SetAttr (reference §7).</summary>
public sealed class SetAttrTests
{
    /// <summary>
    /// Rule 15: a time bit without its _SET twin asks for the server's clock, and the core fills it
    /// from the injected TimeProvider rather than from the machine's wall clock (S-32).
    /// </summary>
    [Fact]
    public void TimeWithoutSetUsesServerClock()
    {
        FakeTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_700_000_500));
        Tsetattr message = new(
            1, 4, SetAttrMask.ATime | SetAttrMask.MTime | SetAttrMask.MTimeSet, 0, 0, 0, 0,
            new TimeSpec(11, 12), new TimeSpec(21, 22));

        SetAttr update = AttrProjector.ResolveServerTimes(AttrProjector.FromSetattr(in message), clock);

        Assert.True(update.ATimeToNow);
        Assert.False(update.MTimeToNow);
        Assert.Equal(new TimeSpec(1_700_000_500, 0), update.ATime);
        Assert.Equal(new TimeSpec(21, 22), update.MTime);
    }

    /// <summary>A ctime bit is always "the server's clock"; there is no CTIME_SET.</summary>
    [Fact]
    public void CTimeIsAlwaysTheServerClock()
    {
        Tsetattr message = new(1, 4, SetAttrMask.CTime, 0, 0, 0, 0, default, default);

        SetAttr update = AttrProjector.FromSetattr(in message);

        Assert.True(update.CTimeToNow);
        Assert.False(update.IsFsyncRequest);
    }

    /// <summary>
    /// A flags-only update is a change (reference §8 rule 19), never mistaken for the fsync an
    /// all-don't-touch record means (§4.2).
    /// </summary>
    [Fact]
    public void AFlagsOnlyUpdateIsNotAnFsync() =>
        Assert.False(new SetAttr { Flags = FileFlags.Append }.IsFsyncRequest);

    /// <summary>Only the bits the client set are projected; everything else is "don't touch".</summary>
    [Fact]
    public void OnlyMarkedFieldsAreProjected()
    {
        Tsetattr message = new(
            1, 4, SetAttrMask.Mode | SetAttrMask.Gid | SetAttrMask.Size, 0x89ED, 7, 9, 4096, default, default);

        SetAttr update = AttrProjector.FromSetattr(in message);

        Assert.Equal(0x9EDu, update.Perm);
        Assert.Null(update.Uid);
        Assert.Equal(9u, update.Gid);
        Assert.Equal(4096ul, update.Size);
        Assert.Null(update.Name);
    }

    /// <summary>An all-don't-touch Twstat is an fsync request, not a no-op (reference §4.2).</summary>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    public void AllDontTouchWstatIsAnFsyncRequest(Dialect dialect)
    {
        StatRecord stat = StatRecord.DontTouch;

        SetAttr update = AttrProjector.FromWstat(in stat, dialect);

        Assert.True(update.IsFsyncRequest);
    }

    /// <summary>A Twstat may rename, re-permission, re-group, truncate and set mtime — nothing else.</summary>
    [Fact]
    public void WstatProjectsOnlyTheSettableFields()
    {
        StatRecord stat = StatRecord.DontTouch with
        {
            Name = "renamed",
            Mode = 0x800001FF,
            Gid = "wheel",
            Length = 64,
            MTime = 1_700_000_002,
        };

        SetAttr update = AttrProjector.FromWstat(in stat, Dialect.P9_2000);

        Assert.Equal("renamed", update.Name);
        Assert.Equal(0x1FFu, update.Perm);
        Assert.Equal("wheel", update.GroupName);
        Assert.Equal(64ul, update.Size);
        Assert.Equal(new TimeSpec(1_700_000_002, 0), update.MTime);
        Assert.Null(update.ATime);
        Assert.Null(update.Uid);
        Assert.False(update.IsFsyncRequest);
    }

    /// <summary>
    /// The owner may never change through a <c>Twstat</c>, however the record is filled in — and
    /// the record is refused rather than projected with the field dropped, because dropping it
    /// answers <c>Rwstat</c> for a chown that never happened (reference §5.8).
    /// </summary>
    [Fact]
    public void WstatNeverChangesTheOwner()
    {
        StatRecord stat = StatRecord.DontTouch with { Uid = "root", NUid = 0 };

        NinePException refusal = Assert.Throws<NinePException>(
            () => AttrProjector.FromWstat(in stat, Dialect.P9_2000_u));

        Assert.Equal((int)Errno.EPERM, refusal.Error.Errno);
        Assert.Contains("owner", refusal.Error.Ename, StringComparison.Ordinal);
    }

    /// <summary>The .u numeric group is projected; a NONUNAME one still means "don't touch".</summary>
    [Theory]
    [InlineData(Dialect.P9_2000_u, 42u, 42u)]
    [InlineData(Dialect.P9_2000_u, Constants.NONUNAME, null)]
    [InlineData(Dialect.P9_2000, 42u, null)]
    public void WstatProjectsTheUnixGroup(Dialect dialect, uint ngid, uint? expected)
    {
        StatRecord stat = StatRecord.DontTouch with { NGid = ngid };

        SetAttr update = AttrProjector.FromWstat(in stat, dialect);

        Assert.Equal(expected, update.Gid);
    }

    /// <summary>A clock the test owns, so "the server's time" is an assertion rather than a race.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
