using NineP.Protocol.Messages;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

/// <summary>The getattr and setattr masks of reference §4.6.</summary>
[Trait("Category", "Conformance")]
public sealed class GetAttrMaskTests
{
    /// <summary>Every getattr bit has the value the reference's table gives it.</summary>
    [Fact]
    public void ValuesMatchReference()
    {
        Assert.Equal(0x0001ul, (ulong)GetAttrMask.Mode);
        Assert.Equal(0x0002ul, (ulong)GetAttrMask.NLink);
        Assert.Equal(0x0004ul, (ulong)GetAttrMask.Uid);
        Assert.Equal(0x0008ul, (ulong)GetAttrMask.Gid);
        Assert.Equal(0x0010ul, (ulong)GetAttrMask.Rdev);
        Assert.Equal(0x0020ul, (ulong)GetAttrMask.ATime);
        Assert.Equal(0x0040ul, (ulong)GetAttrMask.MTime);
        Assert.Equal(0x0080ul, (ulong)GetAttrMask.CTime);
        Assert.Equal(0x0100ul, (ulong)GetAttrMask.Ino);
        Assert.Equal(0x0200ul, (ulong)GetAttrMask.Size);
        Assert.Equal(0x0400ul, (ulong)GetAttrMask.Blocks);
        Assert.Equal(0x0800ul, (ulong)GetAttrMask.BTime);
        Assert.Equal(0x1000ul, (ulong)GetAttrMask.Gen);
        Assert.Equal(0x2000ul, (ulong)GetAttrMask.DataVersion);
        Assert.Equal(0x07FFul, (ulong)GetAttrMask.Basic);
        Assert.Equal(0x3FFFul, (ulong)GetAttrMask.All);
    }

    /// <summary>BASIC is everything through BLOCKS, and ALL is every defined bit.</summary>
    [Fact]
    public void BasicAndAllAreTheUnionsTheReferenceSays()
    {
        GetAttrMask basic = GetAttrMask.Mode | GetAttrMask.NLink | GetAttrMask.Uid | GetAttrMask.Gid
            | GetAttrMask.Rdev | GetAttrMask.ATime | GetAttrMask.MTime | GetAttrMask.CTime
            | GetAttrMask.Ino | GetAttrMask.Size | GetAttrMask.Blocks;

        Assert.Equal(GetAttrMask.Basic, basic);
        Assert.Equal(GetAttrMask.All, basic | GetAttrMask.BTime | GetAttrMask.Gen | GetAttrMask.DataVersion);
    }

    /// <summary>Every setattr bit has the value the reference's table gives it.</summary>
    [Fact]
    public void SetAttrValuesMatchReference()
    {
        Assert.Equal(0x01u, (uint)SetAttrMask.Mode);
        Assert.Equal(0x02u, (uint)SetAttrMask.Uid);
        Assert.Equal(0x04u, (uint)SetAttrMask.Gid);
        Assert.Equal(0x08u, (uint)SetAttrMask.Size);
        Assert.Equal(0x10u, (uint)SetAttrMask.ATime);
        Assert.Equal(0x20u, (uint)SetAttrMask.MTime);
        Assert.Equal(0x40u, (uint)SetAttrMask.CTime);
        Assert.Equal(0x80u, (uint)SetAttrMask.ATimeSet);
        Assert.Equal(0x100u, (uint)SetAttrMask.MTimeSet);
    }
}
