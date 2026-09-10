using System.Buffers;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>The one attribute model of reference §7, projected into all three dialects.</summary>
public sealed class AttrProjectorTests
{
    private static Attr Sample(FileKind kind, uint perm = 0x1A4) => new()
    {
        Qid = new Qid(QidType.QTFILE, 3, 0x0102030405060708),
        Kind = kind,
        Perm = perm,
        UserName = "glenda",
        GroupName = "sys",
        ModifierName = "glenda",
        Uid = 1000,
        Gid = 1000,
        ModifierUid = 1000,
        Size = 1234,
        ATime = new TimeSpec(1_700_000_000, 0),
        MTime = new TimeSpec(1_700_000_001, 0),
    };

    /// <summary>Rule 2: the qid type byte is the high byte of the mode, with DMMOUNT skipped.</summary>
    [Theory]
    [InlineData(0x80000000u, (byte)0x80)]
    [InlineData(0x40000000u, (byte)0x40)]
    [InlineData(0x20000000u, (byte)0x20)]
    [InlineData(0x08000000u, (byte)0x08)]
    [InlineData(0x04000000u, (byte)0x04)]
    [InlineData(0x02000000u, (byte)0x02)]
    [InlineData(0x01000000u, (byte)0x01)]
    [InlineData(0x10000000u, (byte)0x00)]
    [InlineData(0xC00001EDu, (byte)0xC0)]
    public void QidTypeMirrorsModeHighBits(uint mode, byte expected) =>
        Assert.Equal((QidType)expected, AttrProjector.QidTypeFromMode(mode));

    /// <summary>Rule 3: in .L the qid type comes from the POSIX file type, nothing else.</summary>
    [Theory]
    [InlineData(0x41EDu, (byte)0x80)]
    [InlineData(0xA1FFu, (byte)0x02)]
    [InlineData(0x81A4u, (byte)0x00)]
    [InlineData(0x21B6u, (byte)0x00)]
    [InlineData(0x61B6u, (byte)0x00)]
    [InlineData(0xC1B6u, (byte)0x00)]
    [InlineData(0x11B6u, (byte)0x00)]
    public void DotLQidTypeFromPosixFileType(uint posixMode, byte expected) =>
        Assert.Equal((QidType)expected, AttrProjector.QidTypeFromPosixMode(posixMode));

    /// <summary>Rule 6: a directory's stat record reports length 0 however large the tree is.</summary>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    public void DirectoryLengthIsZero(Dialect dialect)
    {
        Attr attr = Sample(FileKind.Directory, 0x1ED) with { Size = 8192 };
        StatRecord stat = AttrProjector.ToStat(attr, "d", dialect);

        Assert.Equal(0ul, stat.Length);
        Assert.Equal(QidType.QTDIR, stat.Qid.Type);
        Assert.Equal(0x800001EDu, stat.Mode);
    }

    /// <summary>Rule 16: a create mode carries the file type on the wire; only 07777 survives.</summary>
    [Theory]
    [InlineData(0x81A4u, 0x1A4u)]
    [InlineData(0x41EDu, 0x1EDu)]
    [InlineData(0x89EDu, 0x9EDu)]
    [InlineData(0xFFFFFFFFu, 0xFFFu)]
    public void CreateModeMaskedTo07777(uint mode, uint expected) =>
        Assert.Equal(expected, AttrProjector.MaskCreatePerm(mode));

    /// <summary>A symlink has no 9P2000 shape, so stat refuses rather than lie (RK-34).</summary>
    [Fact]
    public void SymlinkStatFailsOn9P2000()
    {
        Attr attr = Sample(FileKind.Symlink) with { SymlinkTarget = "/tmp/target" };

        NinePException failure = Assert.Throws<NinePException>(
            () => AttrProjector.ToStat(attr, "link", Dialect.P9_2000));

        Assert.Equal("symlinks not supported", failure.Error.Ename);
        Assert.Equal(Errno.EOPNOTSUPP, failure.Error.Errno);
    }

    /// <summary>The same symlink is representable in .u, with the Linux qid bits of S-20.</summary>
    [Fact]
    public void DotUQidBitsAreLinux()
    {
        Attr attr = Sample(FileKind.Symlink) with { SymlinkTarget = "/tmp/target" };
        StatRecord stat = AttrProjector.ToStat(attr, "link", Dialect.P9_2000_u);

        Assert.Equal(QidType.QTSYMLINK, stat.Qid.Type);
        Assert.Equal(0x02000000u | 0x1A4u, stat.Mode);
        Assert.Equal("/tmp/target", stat.Extension);
        Assert.Equal(1000u, stat.NUid);
    }

    /// <summary>A device carries "b maj min" / "c maj min" in the .u extension (reference §4.4).</summary>
    [Theory]
    [InlineData(FileKind.BlockDevice, "b 8 1")]
    [InlineData(FileKind.CharDevice, "c 8 1")]
    public void DotUDeviceExtensionIsTheReferenceText(FileKind kind, string expected)
    {
        Attr attr = Sample(kind) with { Rdev = new DeviceId(8, 1) };
        StatRecord stat = AttrProjector.ToStat(attr, "dev", Dialect.P9_2000_u);

        Assert.Equal(expected, stat.Extension);
        Assert.Equal(0x00800000u | 0x1A4u, stat.Mode);
    }

    /// <summary>9P2000 drops setuid, setgid and sticky; .u carries them as DM bits.</summary>
    [Fact]
    public void UnixPermissionBitsOnlyExistInDotU()
    {
        Attr attr = Sample(FileKind.File, 0xDED);

        Assert.Equal(0x1EDu, AttrProjector.ToMode(attr, Dialect.P9_2000));
        Assert.Equal(0x00080000u | 0x00040000u | 0x1EDu, AttrProjector.ToMode(attr, Dialect.P9_2000_u));
    }

    /// <summary>An Rgetattr is always the full 160 bytes, whatever the valid mask says.</summary>
    [Theory]
    [InlineData(GetAttrMask.None)]
    [InlineData(GetAttrMask.Basic)]
    [InlineData(GetAttrMask.All)]
    public void RgetattrIs160Bytes(GetAttrMask requested)
    {
        Rgetattr reply = AttrProjector.ToGetattr(7, Sample(FileKind.File), requested, GetAttrMask.All);
        ArrayBufferWriter<byte> writer = new();

        int written = MessageCodec.Encode(writer, in reply, Dialect.P9_2000_L);

        Assert.Equal(160, written);
        Assert.Equal(160, writer.WrittenCount);
    }

    /// <summary>Valid is the intersection of what was asked for and what the handler filled in.</summary>
    [Fact]
    public void RgetattrValidIsRequestedAndSupplied()
    {
        Rgetattr reply = AttrProjector.ToGetattr(
            7, Sample(FileKind.File), GetAttrMask.Basic, GetAttrMask.Mode | GetAttrMask.Size | GetAttrMask.Gen);

        Assert.Equal(GetAttrMask.Mode | GetAttrMask.Size, reply.Valid);
        Assert.Equal(0ul, reply.Gen);
        Assert.Equal(1234ul, reply.Size);
        Assert.Equal(0u, reply.Uid);
    }

    /// <summary>The qid is valid whatever the mask carries (reference §4.6).</summary>
    [Fact]
    public void RgetattrQidIsAlwaysValid()
    {
        Rgetattr reply = AttrProjector.ToGetattr(7, Sample(FileKind.Directory, 0x1ED), GetAttrMask.None, GetAttrMask.None);

        Assert.Equal(QidType.QTDIR, reply.Qid.Type);
        Assert.Equal(0x0102030405060708ul, reply.Qid.Path);
        Assert.Equal(0u, reply.Mode);
    }

    /// <summary>The POSIX mode is the S_IF* type or-ed with the 07777 permission bits.</summary>
    [Theory]
    [InlineData(FileKind.File, 0x81A4u)]
    [InlineData(FileKind.Directory, 0x41A4u)]
    [InlineData(FileKind.Symlink, 0xA1A4u)]
    [InlineData(FileKind.Fifo, 0x11A4u)]
    [InlineData(FileKind.Socket, 0xC1A4u)]
    [InlineData(FileKind.CharDevice, 0x21A4u)]
    [InlineData(FileKind.BlockDevice, 0x61A4u)]
    public void PosixModeCarriesTheFileType(FileKind kind, uint expected) =>
        Assert.Equal(expected, AttrProjector.ToPosixMode(Sample(kind)));

    /// <summary>A .L session has no stat record at all; asking for one is a caller's bug.</summary>
    [Fact]
    public void StatRecordIsNotADotLShape() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AttrProjector.ToStat(Sample(FileKind.File), "f", Dialect.P9_2000_L));

    /// <summary>
    /// Reference §8 rule 17: a client reads only what <c>Rgetattr.valid</c> marks. The reply below
    /// carries a full set of plausible-looking numbers and marks none of them, which is exactly
    /// what §4.6 lets a server do; reading them anyway turned "the server did not say" into a file
    /// of mode 0, size 0, nlink 0 and epoch times.
    /// </summary>
    [Fact]
    public void GetattrReadsOnlyTheMarkedFields()
    {
        Rgetattr reply = Populated(GetAttrMask.Size | GetAttrMask.MTime);

        Attr attr = AttrProjector.FromGetattr(in reply);

        // Marked: read from the wire.
        Assert.Equal(4096ul, attr.Size);
        Assert.Equal(new TimeSpec(1_700_000_002, 20), attr.MTime);

        // Unmarked: the Attr default, not the reply's padding.
        Assert.Equal(0u, attr.Perm);
        Assert.Equal(1ul, attr.NLink);
        Assert.Equal(Constants.NONUNAME, attr.Uid);
        Assert.Equal(Constants.NONUNAME, attr.Gid);
        Assert.Null(attr.Rdev);
        Assert.Equal(4096ul, attr.BlockSize);
        Assert.Equal(0ul, attr.Blocks);
        Assert.Equal(default, attr.ATime);
        Assert.Equal(default, attr.CTime);
        Assert.Equal(default, attr.BTime);
        Assert.Equal(0ul, attr.Gen);
        Assert.Equal(0ul, attr.DataVersion);
    }

    /// <summary>Every field the mask does mark is read, so honouring the mask loses nothing.</summary>
    [Fact]
    public void GetattrReadsEveryMarkedField()
    {
        Rgetattr reply = Populated(GetAttrMask.All);

        Attr attr = AttrProjector.FromGetattr(in reply);

        Assert.Equal(FileKind.CharDevice, attr.Kind);
        Assert.Equal(0x1A4u, attr.Perm);
        Assert.Equal(3ul, attr.NLink);
        Assert.Equal(1000u, attr.Uid);
        Assert.Equal(1001u, attr.Gid);
        Assert.Equal(new DeviceId(8, 1), attr.Rdev);
        Assert.Equal(4096ul, attr.Size);
        Assert.Equal(512ul, attr.BlockSize);
        Assert.Equal(8ul, attr.Blocks);
        Assert.Equal(new TimeSpec(1_700_000_001, 10), attr.ATime);
        Assert.Equal(new TimeSpec(1_700_000_003, 30), attr.CTime);
        Assert.Equal(new TimeSpec(1_700_000_004, 40), attr.BTime);
        Assert.Equal(77ul, attr.Gen);
        Assert.Equal(88ul, attr.DataVersion);
    }

    /// <summary>
    /// Reference §8 rule 17: with <c>MODE</c> unmarked there is no POSIX mode word to take the
    /// kind from, so the kind comes from the qid type byte, which §4.6 says is always valid.
    /// </summary>
    /// <param name="type">The qid type byte the server sent.</param>
    /// <param name="expected">The kind it names.</param>
    [Theory]
    [InlineData(QidType.QTDIR, FileKind.Directory)]
    [InlineData(QidType.QTSYMLINK, FileKind.Symlink)]
    [InlineData(QidType.QTFILE, FileKind.File)]
    [InlineData(QidType.QTAPPEND, FileKind.File)]
    public void AnUnmarkedModeTakesTheKindFromTheQid(QidType type, FileKind expected)
    {
        Rgetattr reply = Populated(GetAttrMask.None) with { Qid = new Qid(type, 3, 9) };

        Assert.Equal(expected, AttrProjector.FromGetattr(in reply).Kind);
    }

    /// <summary>
    /// Reference §8 rule 17: <c>Attr.Kind</c> and the qid type byte always agree. Plain 9P2000 has
    /// no <c>DMSYMLINK</c>, but a server can still mark the qid <c>QTSYMLINK</c>; reporting that
    /// file as a plain file left the record contradicting its own qid. The target stays null,
    /// because plain 9P2000 has no extension field to carry one.
    /// </summary>
    [Fact]
    public void A9P2000SymlinkQidYieldsASymlinkKind()
    {
        StatRecord record = new()
        {
            Qid = new Qid(QidType.QTSYMLINK, 1, 7),
            Mode = 0x1FF,
            Name = "link",
            Uid = "glenda",
            Gid = "sys",
            Muid = "glenda",
        };

        Attr attr = AttrProjector.FromStat(in record, Dialect.P9_2000);

        Assert.Equal(FileKind.Symlink, attr.Kind);
        Assert.Equal(QidType.QTSYMLINK, attr.Qid.Type);
        Assert.Null(attr.SymlinkTarget);
    }

    /// <summary>The .u symlink is unchanged: the same kind, and the extension is still its target.</summary>
    [Fact]
    public void ADotUSymlinkStillCarriesItsTarget()
    {
        StatRecord record = new()
        {
            Qid = new Qid(QidType.QTSYMLINK, 1, 7),
            Mode = 0x02000000u | 0x1FF,
            Name = "link",
            Uid = "glenda",
            Gid = "sys",
            Muid = "glenda",
            Extension = "/tmp/target",
        };

        Attr attr = AttrProjector.FromStat(in record, Dialect.P9_2000_u);

        Assert.Equal(FileKind.Symlink, attr.Kind);
        Assert.Equal("/tmp/target", attr.SymlinkTarget);
    }

    /// <summary>
    /// Reference §8 rule 15: stat(5) lists <c>atime</c> among the fields a <c>Twstat</c> may not
    /// set. Dropping it produced a record that changed nothing, and a <c>Twstat</c> that changes
    /// nothing is stat(5)'s fsync (§4.2) — a success reply for an update that never happened.
    /// </summary>
    /// <param name="dialect">The dialect the update would have been sent in.</param>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    public void WstatCannotSetAtime(Dialect dialect)
    {
        NinePException refusal = Assert.Throws<NinePException>(
            () => AttrProjector.ToWstat(new SetAttr { ATime = new TimeSpec(1, 0) }, dialect));

        Assert.Equal((int)Errno.EPERM, refusal.Error.Errno);
        Assert.Contains("atime", refusal.Error.Ename, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reference §8 rule 15: a <c>Twstat</c> carries a time value and has no <c>_SET</c> twin to
    /// leave off, so "use the server's clock" has no wstat spelling. Each flag below used to
    /// project to <see cref="StatRecord.DontTouch"/> — the fsync record — and this test asserts
    /// the projection no longer reaches it.
    /// </summary>
    /// <param name="update">An update whose only field is a "server clock" flag.</param>
    [Theory]
    [MemberData(nameof(ServerClockUpdates))]
    public void WstatCannotAskForTheServerClock(SetAttr update)
    {
        NinePException refusal = Assert.Throws<NinePException>(
            () => AttrProjector.ToWstat(update, Dialect.P9_2000));

        Assert.Equal((int)Errno.EINVAL, refusal.Error.Errno);
    }

    /// <summary>The three "use the server's clock" flags, each on its own.</summary>
    /// <returns>One row per flag.</returns>
    public static TheoryData<SetAttr> ServerClockUpdates() =>
    [
        new SetAttr { ATimeToNow = true },
        new SetAttr { MTimeToNow = true },
        new SetAttr { CTimeToNow = true },
    ];

    /// <summary>
    /// Reference §8 rule 15: <c>n_gid</c> is a <c>.u</c> field, so a numeric group has nowhere to
    /// go in a plain 9P2000 stat record and is refused rather than dropped. In <c>.u</c> it is
    /// sent, which is what makes the refusal a dialect rule and not a missing feature.
    /// </summary>
    [Fact]
    public void ANumericGroupNeedsDotU()
    {
        NinePException refusal = Assert.Throws<NinePException>(
            () => AttrProjector.ToWstat(new SetAttr { Gid = 42 }, Dialect.P9_2000));

        Assert.Equal((int)Errno.EINVAL, refusal.Error.Errno);
        Assert.Equal(42u, AttrProjector.ToWstat(new SetAttr { Gid = 42 }, Dialect.P9_2000_u).NGid);
    }

    /// <summary>
    /// Reference §8 rule 15: <c>Tsetattr</c> has neither a name nor a group-name field — <c>.L</c>
    /// identifies groups by number and renames with <c>Trename</c> — so an update naming either is
    /// refused rather than sent with the field missing.
    /// </summary>
    /// <param name="update">An update whose only field is one .L cannot carry.</param>
    [Theory]
    [MemberData(nameof(UnsettableInDotL))]
    public void SetattrHasNoNameOrGroupName(SetAttr update)
    {
        NinePException refusal = Assert.Throws<NinePException>(
            () => AttrProjector.ToSetattr(1, 4, update));

        Assert.Equal((int)Errno.EINVAL, refusal.Error.Errno);
    }

    /// <summary>
    /// The two textual fields a <c>Tsetattr</c> has no slot for, and the file flags its POSIX mode
    /// word has no bit for (reference §8 rule 19).
    /// </summary>
    /// <returns>One row per field.</returns>
    public static TheoryData<SetAttr> UnsettableInDotL() =>
    [
        new SetAttr { Name = "renamed" },
        new SetAttr { GroupName = "wheel" },
        new SetAttr { Flags = FileFlags.Append },
    ];

    /// <summary>
    /// Reference §8 rule 19, the send direction: the file flags travel in the mode word's high
    /// bits beside the permission bits, and <see cref="AttrProjector.FlagsOf"/> reads them back,
    /// so what a client sends is what a server judges.
    /// </summary>
    /// <param name="flags">The flags the caller asked for.</param>
    /// <param name="high">The high bits that go out.</param>
    [Theory]
    [InlineData(FileFlags.Append, ModeBits.DMAPPEND)]
    [InlineData(FileFlags.Exclusive, ModeBits.DMEXCL)]
    [InlineData(FileFlags.Temporary, ModeBits.DMTMP)]
    [InlineData(FileFlags.Append | FileFlags.Temporary, ModeBits.DMAPPEND | ModeBits.DMTMP)]
    [InlineData(FileFlags.None, 0u)]
    public void ToWstatSendsTheFileFlagsInTheModeWord(FileFlags flags, uint high)
    {
        StatRecord sent = AttrProjector.ToWstat(new SetAttr { Perm = 0x1ED, Flags = flags }, Dialect.P9_2000);

        Assert.Equal(high | 0x1EDu, sent.Mode);
        Assert.Equal(flags, AttrProjector.FlagsOf(sent.Mode));
    }

    /// <summary>
    /// Rule 19: a <c>Twstat</c> mode word carries the permission bits and the file flags
    /// together, so an update stating one half and not the other is refused by the projector —
    /// a zero in the unstated half would be a change the caller never asked for. Neither half
    /// stated is "don't touch", as before.
    /// </summary>
    [Fact]
    public void AHalfStatedModeWordIsRefused()
    {
        NinePException permOnly = Assert.Throws<NinePException>(
            () => AttrProjector.ToWstat(new SetAttr { Perm = 0x1A4 }, Dialect.P9_2000));
        NinePException flagsOnly = Assert.Throws<NinePException>(
            () => AttrProjector.ToWstat(new SetAttr { Flags = FileFlags.Append }, Dialect.P9_2000_u));

        Assert.Equal((int)Errno.EINVAL, permOnly.Error.Errno);
        Assert.Equal((int)Errno.EINVAL, flagsOnly.Error.Errno);
        Assert.Equal(uint.MaxValue, AttrProjector.ToWstat(new SetAttr { Size = 0 }, Dialect.P9_2000).Mode);
    }

    /// <summary>
    /// Rule 19: <see cref="AttrProjector.CompleteMode"/> fills the unstated half of the mode word
    /// from the record a <c>Tstat</c> answered — the file's own flags beside a new permission
    /// value, the file's own permission bits beside new flags — and leaves an update that states
    /// both, or neither, as it is. In .u the permission bits are read with their setuid spelling.
    /// </summary>
    [Fact]
    public void CompleteModeFillsTheUnstatedHalfFromTheRecord()
    {
        StatRecord current = StatRecord.DontTouch with { Mode = ModeBits.DMAPPEND | 0x1ED };

        SetAttr chmod = AttrProjector.CompleteMode(new SetAttr { Perm = 0x1A4 }, current, Dialect.P9_2000);
        Assert.Equal(0x1A4u, chmod.Perm);
        Assert.Equal(FileFlags.Append, chmod.Flags);

        SetAttr clearing = AttrProjector.CompleteMode(new SetAttr { Flags = FileFlags.None }, current, Dialect.P9_2000);
        Assert.Equal(0x1EDu, clearing.Perm);
        Assert.Equal(FileFlags.None, clearing.Flags);

        SetAttr both = new() { Perm = 0x1A4, Flags = FileFlags.Exclusive };
        Assert.Same(both, AttrProjector.CompleteMode(both, current, Dialect.P9_2000));

        SetAttr neither = new() { Size = 0 };
        Assert.Same(neither, AttrProjector.CompleteMode(neither, current, Dialect.P9_2000));

        StatRecord setuid = StatRecord.DontTouch with { Mode = ModeBits.DMSETUID | 0x1ED };
        Assert.Equal(
            0x9EDu,
            AttrProjector.CompleteMode(new SetAttr { Flags = FileFlags.Append }, setuid, Dialect.P9_2000_u).Perm);
    }

    /// <summary>
    /// Rule 19: <c>DMAUTH</c> and <c>DMMOUNT</c> are the server's own bits, so an update naming
    /// either flag is refused before a record is built.
    /// </summary>
    /// <param name="flag">The server-owned flag.</param>
    [Theory]
    [InlineData(FileFlags.Auth)]
    [InlineData(FileFlags.Mount)]
    public void AServerOwnedFlagIsRefusedByTheProjector(FileFlags flag)
    {
        NinePException refusal = Assert.Throws<NinePException>(
            () => AttrProjector.ToWstat(new SetAttr { Perm = 0x1ED, Flags = flag }, Dialect.P9_2000));

        Assert.Equal((int)Errno.EPERM, refusal.Error.Errno);
    }

    /// <summary>
    /// Reference §8 rule 19: the <c>.u</c> <c>DMSETUID</c> / <c>DMSETGID</c> / <c>DMSETVTX</c>
    /// bits map onto the <c>07777</c> permission bits and are honoured. They arrive in the mode
    /// word's <b>high</b> bits — a v9fs <c>chmod u+s</c> sends <c>0x00080000</c>, not <c>04000</c>
    /// — so the old <c>0xFFF</c> mask could never have seen them and dropped every one.
    /// </summary>
    /// <param name="mode">The <c>Twstat.mode</c> word a .u client sends.</param>
    /// <param name="expected">The 07777 permission value it means.</param>
    [Theory]
    [InlineData(0x000801EDu, 0x9EDu)]
    [InlineData(0x000401EDu, 0x5EDu)]
    [InlineData(0x000101EDu, 0x3EDu)]
    [InlineData(0x000D01EDu, 0xFEDu)]
    [InlineData(0x000001EDu, 0x1EDu)]
    public void WstatCarriesTheUnixPermissionBitsInDotU(uint mode, uint expected)
    {
        StatRecord stat = StatRecord.DontTouch with { Mode = mode };

        Assert.Equal(expected, AttrProjector.FromWstat(in stat, Dialect.P9_2000_u, null).Perm);
    }

    /// <summary>
    /// Plain 9P2000's mode word carries the rwx bits and nothing else, so the same record there
    /// yields only those: the high bits are not a permission spelling that dialect has.
    /// </summary>
    [Fact]
    public void PlainNineP2000WstatHasOnlyTheRwxBits()
    {
        StatRecord stat = StatRecord.DontTouch with { Mode = 0x000801EDu };

        Assert.Equal(0x1EDu, AttrProjector.FromWstat(in stat, Dialect.P9_2000, null).Perm);
    }

    /// <summary>
    /// Reference §8 rule 19, the send direction: <c>ToWstat</c> is the exact inverse, so what a
    /// client sends is what a server reads back. Round-tripping the pair is the assertion, because
    /// a projection that agreed with itself but not with the wire would still be wrong.
    /// </summary>
    /// <param name="perm">The 07777 permission value the caller asked for.</param>
    /// <param name="expected">The mode word that goes out.</param>
    [Theory]
    [InlineData(0x9EDu, 0x000801EDu)]
    [InlineData(0x5EDu, 0x000401EDu)]
    [InlineData(0x3EDu, 0x000101EDu)]
    [InlineData(0xFEDu, 0x000D01EDu)]
    [InlineData(0x1EDu, 0x000001EDu)]
    public void ToWstatSendsTheUnixPermissionBitsInDotU(uint perm, uint expected)
    {
        StatRecord sent = AttrProjector.ToWstat(new SetAttr { Perm = perm, Flags = FileFlags.None }, Dialect.P9_2000_u);

        Assert.Equal(expected, sent.Mode);
        Assert.Equal(perm, AttrProjector.FromWstat(in sent, Dialect.P9_2000_u, null).Perm);
    }

    /// <summary>
    /// Reference §8 rule 15: setuid, setgid and sticky have no plain-9P2000 spelling, so a chmod
    /// naming one is refused there rather than sent with the bit masked off and answered
    /// <c>Rwstat</c>. An ordinary rwx chmod still goes out.
    /// </summary>
    /// <param name="perm">The permission value the caller asked for.</param>
    [Theory]
    [InlineData(0x9EDu)]
    [InlineData(0x5EDu)]
    [InlineData(0x3EDu)]
    public void PlainNineP2000RefusesTheUnixPermissionBits(uint perm)
    {
        NinePException refusal = Assert.Throws<NinePException>(
            () => AttrProjector.ToWstat(new SetAttr { Perm = perm, Flags = FileFlags.None }, Dialect.P9_2000));

        Assert.Equal((int)Errno.EINVAL, refusal.Error.Errno);
        Assert.Equal(
            0x1EDu,
            AttrProjector.ToWstat(new SetAttr { Perm = 0x1ED, Flags = FileFlags.None }, Dialect.P9_2000).Mode);
    }

    /// <summary>An Rgetattr carrying a value in every field, valid for whatever the mask says.</summary>
    private static Rgetattr Populated(GetAttrMask valid) => new(
        7,
        valid,
        new Qid(QidType.QTFILE, 3, 0x0102030405060708),
        ModeBits.S_IFCHR | 0x1A4,
        1000,
        1001,
        3,
        (8ul << 8) | 1,
        4096,
        512,
        8,
        new TimeSpec(1_700_000_001, 10),
        new TimeSpec(1_700_000_002, 20),
        new TimeSpec(1_700_000_003, 30),
        new TimeSpec(1_700_000_004, 40),
        77,
        88);
}
