using NineP.Protocol;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

/// <summary>The open modes and flags of reference §4.5.</summary>
[Trait("Category", "Conformance")]
public sealed class OpenModeTests
{
    private const byte OAPPEND = 0x80;
    private const byte AccessModeMask = 0x03;

    /// <summary>
    /// OAPPEND (0x80) is a flag OR'd with an access mode, not an access mode itself: it lies
    /// outside the low two bits the access mode is taken from, so OREAD plus OAPPEND stays a read
    /// open. It is modelled as a member of <see cref="OpenFlags"/>, never of <see cref="OpenMode"/>.
    /// </summary>
    [Fact]
    public void AppendIsAFlagNotAnAccessMode()
    {
        Assert.Equal(0, OAPPEND & AccessModeMask);
        Assert.Equal(OpenFlags.Append, OpenFlags.Append & ~(OpenFlags)AccessModeMask);

        foreach (OpenMode mode in Enum.GetValues<OpenMode>())
        {
            Assert.Equal((int)mode, (int)mode & AccessModeMask);
        }
    }

    /// <summary>The four access modes are exactly the four values the low two bits can take.</summary>
    [Fact]
    public void AccessModesAreTheLowTwoBits()
    {
        Assert.Equal(0, (int)OpenMode.Read);
        Assert.Equal(1, (int)OpenMode.Write);
        Assert.Equal(2, (int)OpenMode.ReadWrite);
        Assert.Equal(3, (int)OpenMode.Exec);
        Assert.Equal(4, Enum.GetValues<OpenMode>().Length);
    }

    /// <summary>Every open flag is a distinct single bit, so a set of them is a bit set.</summary>
    [Fact]
    public void OpenFlagsAreDistinctSingleBits()
    {
        int seen = 0;
        foreach (OpenFlags flag in Enum.GetValues<OpenFlags>())
        {
            if (flag == OpenFlags.None)
            {
                continue;
            }

            Assert.Equal(0, (int)flag & ((int)flag - 1));
            Assert.Equal(0, seen & (int)flag);
            seen |= (int)flag;
        }
    }
}
