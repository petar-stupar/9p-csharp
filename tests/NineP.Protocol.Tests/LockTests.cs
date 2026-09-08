using NineP.Protocol;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>The lock constants of reference §4.8.</summary>
public sealed class LockTests
{
    /// <summary>Lock types, flags and statuses have the reference's values.</summary>
    [Fact]
    public void ConstantsMatchReference()
    {
        Assert.Equal(0, (byte)LockType.ReadLock);
        Assert.Equal(1, (byte)LockType.WriteLock);
        Assert.Equal(2, (byte)LockType.Unlock);

        Assert.Equal(1, (int)LockFlags.Block);
        Assert.Equal(2, (int)LockFlags.Reclaim);

        Assert.Equal(0, (byte)LockStatus.Success);
        Assert.Equal(1, (byte)LockStatus.Blocked);
        Assert.Equal(2, (byte)LockStatus.Error);
        Assert.Equal(3, (byte)LockStatus.Grace);

        Assert.Equal(1, (int)XattrFlags.Create);
        Assert.Equal(2, (int)XattrFlags.Replace);
    }

    /// <summary>
    /// A length of 0 means "to the end of the file", and an Rgetlock whose type is Unlock means no
    /// lock conflicts; both are the reference's conventions rather than sentinel constants.
    /// </summary>
    [Fact]
    public void ZeroLengthAndUnlockAreTheReferencesConventions()
    {
        LockRequest toEnd = new(LockType.WriteLock, LockFlags.None, 0, 0, 42, "client");
        Assert.Equal(0ul, toEnd.Length);

        LockQueryResult noConflict = new(LockType.Unlock, 0, 0, 0, string.Empty);
        Assert.Equal(LockType.Unlock, noConflict.Type);
    }

    /// <summary>V9FS_MAGIC is the type a synthetic server reports from Tstatfs.</summary>
    [Fact]
    public void SyntheticFilesystemMagicIsV9fs() => Assert.Equal(0x01021997u, StatFs.V9fsMagic);
}
