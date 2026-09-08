using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>The mode vocabularies of reference §4.4 and §4.7, neither of which is vendored.</summary>
public sealed class ModeBitsTests
{
    /// <summary>
    /// Rule 9: every DM* bit of reference §4.4 and every S_IF* value of reference §4.7 has the
    /// value the reference tabulates. The octal figures in the comments are the reference's own.
    /// </summary>
    [Fact]
    public void ValuesMatchReference()
    {
        Assert.Equal(0x80000000u, ModeBits.DMDIR);
        Assert.Equal(0x40000000u, ModeBits.DMAPPEND);
        Assert.Equal(0x20000000u, ModeBits.DMEXCL);
        Assert.Equal(0x10000000u, ModeBits.DMMOUNT);
        Assert.Equal(0x08000000u, ModeBits.DMAUTH);
        Assert.Equal(0x04000000u, ModeBits.DMTMP);
        Assert.Equal(0x02000000u, ModeBits.DMSYMLINK);
        Assert.Equal(0x01000000u, ModeBits.DMLINK);
        Assert.Equal(0x00800000u, ModeBits.DMDEVICE);
        Assert.Equal(0x00200000u, ModeBits.DMNAMEDPIPE);
        Assert.Equal(0x00100000u, ModeBits.DMSOCKET);
        Assert.Equal(0x00080000u, ModeBits.DMSETUID);
        Assert.Equal(0x00040000u, ModeBits.DMSETGID);
        Assert.Equal(0x00010000u, ModeBits.DMSETVTX);
        Assert.Equal(0x000001FFu, ModeBits.Permissions);

        Assert.Equal(0xF000u, ModeBits.S_IFMT);      // 0170000
        Assert.Equal(0xC000u, ModeBits.S_IFSOCK);    // 0140000
        Assert.Equal(0xA000u, ModeBits.S_IFLNK);     // 0120000
        Assert.Equal(0x8000u, ModeBits.S_IFREG);     // 0100000
        Assert.Equal(0x6000u, ModeBits.S_IFBLK);     // 060000
        Assert.Equal(0x4000u, ModeBits.S_IFDIR);     // 040000
        Assert.Equal(0x2000u, ModeBits.S_IFCHR);     // 020000
        Assert.Equal(0x1000u, ModeBits.S_IFIFO);     // 010000
        Assert.Equal(0x800u, ModeBits.S_ISUID);      // 04000
        Assert.Equal(0x400u, ModeBits.S_ISGID);      // 02000
        Assert.Equal(0x200u, ModeBits.S_ISVTX);      // 01000
        Assert.Equal(0xFFFu, ModeBits.FullPermissions); // 07777
    }

    /// <summary>The qid mirror keeps every high bit but DMMOUNT, which intro(5) skips.</summary>
    [Fact]
    public void TheQidMirrorSkipsOnlyTheMountBit() =>
        Assert.Equal(0xFF & ~0x10, ModeBits.QidMirrorMask);

    /// <summary>
    /// Reference §8 rule 15: <c>.L</c> has no <c>ORCLOSE</c>. The flag used to fall out of
    /// <c>ToLinuxFlags</c> unmentioned, so a caller that asked for remove-on-close got an ordinary
    /// open, an <c>Rlopen</c>, and a file that was still there after the clunk.
    /// </summary>
    [Fact]
    public void TheRemoveOnCloseFlagHasNoLinuxSpelling()
    {
        NinePException refusal = Assert.Throws<NinePException>(
            () => ModeBits.ToLinuxFlags(OpenMode.Write, OpenFlags.RemoveOnClose));

        Assert.Equal(Errno.EOPNOTSUPP, refusal.Error.Errno);
        Assert.Contains("ORCLOSE", refusal.Error.Ename, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reference §8 rule 15: <c>O_EXCL</c>, <c>O_DIRECTORY</c> and <c>O_NOFOLLOW</c> have no
    /// <c>Topen.mode</c> spelling — <c>OEXCL</c> is <c>0x1000</c> and does not fit the byte at all
    /// — so a 9P2000 or <c>.u</c> open that names one is refused rather than sent without it.
    /// </summary>
    /// <param name="flag">The .L-only flag the caller asked for.</param>
    [Theory]
    [InlineData(OpenFlags.Exclusive)]
    [InlineData(OpenFlags.Directory)]
    [InlineData(OpenFlags.NoFollow)]
    public void LinuxOnlyFlagsHaveNo9P2000Spelling(OpenFlags flag)
    {
        NinePException refusal = Assert.Throws<NinePException>(
            () => ModeBits.ToOpenByte(OpenMode.Read, flag));

        Assert.Equal(Errno.EOPNOTSUPP, refusal.Error.Errno);
    }

    /// <summary>The flags 9P2000 does spell still project; only the three .L-only ones are refused.</summary>
    [Fact]
    public void TheFlagsNineP2000HasStillProject() =>
        Assert.Equal(
            (byte)(0x01 | ModeBits.OTRUNC | ModeBits.ORCLOSE | ModeBits.OAPPEND),
            ModeBits.ToOpenByte(
                OpenMode.Write, OpenFlags.Truncate | OpenFlags.RemoveOnClose | OpenFlags.Append));

    /// <summary>
    /// Reference §8 rule 16: <c>OEXEC</c> goes out as <c>O_RDONLY</c>. Access mode 3 is
    /// <c>O_NOACCESS</c>, which never leaves a client, and execute permission is the Linux
    /// client's own concern; the flags travelling with it are unaffected.
    /// </summary>
    /// <param name="mode">The access mode the caller asked for.</param>
    /// <param name="expected">The access bits that go on the wire.</param>
    [Theory]
    [InlineData(OpenMode.Read, 0u)]
    [InlineData(OpenMode.Write, 1u)]
    [InlineData(OpenMode.ReadWrite, 2u)]
    [InlineData(OpenMode.Exec, 0u)]
    public void ExecIsSentAsReadOnlyInDotL(OpenMode mode, uint expected)
    {
        Assert.Equal(expected, ModeBits.ToLinuxFlags(mode, OpenFlags.None));
        Assert.Equal(expected | ModeBits.O_TRUNC, ModeBits.ToLinuxFlags(mode, OpenFlags.Truncate));
    }

    /// <summary>
    /// The server's own decoding is untouched: <c>O_NOACCESS</c> off the wire is still
    /// <see cref="OpenMode.Exec"/>, because rule 16 governs what a client sends and not what a
    /// server must accept from peers that are not this one.
    /// </summary>
    [Fact]
    public void ServerDecodingStillReadsAccessModeThree()
    {
        ModeBits.FromLinuxFlags(3, out OpenMode mode, out OpenFlags flags);

        Assert.Equal(OpenMode.Exec, mode);
        Assert.Equal(OpenFlags.None, flags);
    }
}
