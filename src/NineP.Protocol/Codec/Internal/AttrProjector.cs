using System.Globalization;
using NineP.Protocol.Messages;

namespace NineP.Protocol.Codec.Internal;

/// <summary>
/// The only code that knows both sides of reference §7: it turns one dialect-neutral
/// <see cref="Attr"/> into a 9P2000 stat record, a 9P2000.u stat record or an <c>Rgetattr</c>, and
/// turns a <c>Twstat</c> or a <c>Tsetattr</c> back into one <see cref="SetAttr"/>. Handlers never
/// see a dialect-specific attribute shape, which is what keeps the three projections consistent.
/// </summary>
internal static class AttrProjector
{
    /// <summary>
    /// The qid type byte a 9P2000 / .u mode word implies: the high byte of the mode, with bit 28
    /// (<c>DMMOUNT</c>) skipped as intro(5) requires.
    /// </summary>
    /// <param name="mode">The 9P2000 or .u mode word.</param>
    /// <returns>The qid type byte.</returns>
    public static QidType QidTypeFromMode(uint mode) =>
        (QidType)(byte)((mode >> 24) & ModeBits.QidMirrorMask);

    /// <summary>
    /// The qid type byte a POSIX mode word implies (S-20): a directory is <c>QTDIR</c>, a symbolic
    /// link is <c>QTSYMLINK</c>, and everything else is a plain file.
    /// </summary>
    /// <param name="posixMode">The POSIX <c>st_mode</c> value.</param>
    /// <returns>The qid type byte a .L session carries.</returns>
    public static QidType QidTypeFromPosixMode(uint posixMode) => (posixMode & ModeBits.S_IFMT) switch
    {
        ModeBits.S_IFDIR => QidType.QTDIR,
        ModeBits.S_IFLNK => QidType.QTSYMLINK,
        _ => QidType.QTFILE,
    };

    /// <summary>
    /// The <c>07777</c> mask reference §4.7 requires on a create mode: v9fs sends the file type in
    /// <c>Tlcreate.mode</c> and <c>Tmkdir.mode</c>, and a server keeps only the permission bits.
    /// </summary>
    /// <param name="mode">The mode the client sent.</param>
    /// <returns>The permission bits alone.</returns>
    public static FilePermissions MaskCreatePerm(uint mode) =>
        (FilePermissions)(mode & ModeBits.FullPermissions);

    /// <summary>The 9P2000 / .u mode word an attribute record projects to.</summary>
    /// <param name="attr">The attributes.</param>
    /// <param name="dialect">The session dialect, which decides which high bits exist.</param>
    /// <returns>The mode word.</returns>
    public static uint ToMode(Attr attr, Dialect dialect)
    {
        ArgumentNullException.ThrowIfNull(attr);

        uint mode = (uint)attr.Perm & ModeBits.Permissions;
        mode |= HighFlagBits(attr.Flags);
        mode |= KindBits(attr.Kind, dialect);

        if (dialect == Dialect.P9_2000_u)
        {
            mode |= UnixPermissionBits(attr.Perm);
        }

        return mode;
    }

    /// <summary>The POSIX mode word an attribute record projects to (reference §4.7).</summary>
    /// <param name="attr">The attributes.</param>
    /// <returns>The <c>S_IF*</c> file type or-ed with the 07777 permission bits.</returns>
    public static uint ToPosixMode(Attr attr)
    {
        ArgumentNullException.ThrowIfNull(attr);

        return PosixTypeBits(attr.Kind) | ((uint)attr.Perm & ModeBits.FullPermissions);
    }

    /// <summary>
    /// Projects attributes into a 9P2000 or .u stat record. A symbolic link is not representable
    /// in 9P2000 and is refused there rather than reported as a plain file (reference §7).
    /// </summary>
    /// <param name="attr">The attributes.</param>
    /// <param name="name">The name the record carries; "/" for the root of a served tree.</param>
    /// <param name="dialect">The session dialect; .L never carries a stat record.</param>
    /// <returns>The stat record.</returns>
    /// <exception cref="NinePException">The file is a symlink and the dialect is 9P2000.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The dialect is 9P2000.L or not a dialect.</exception>
    public static StatRecord ToStat(Attr attr, string name, Dialect dialect)
    {
        ArgumentNullException.ThrowIfNull(attr);
        ArgumentNullException.ThrowIfNull(name);

        if (dialect is not (Dialect.P9_2000 or Dialect.P9_2000_u))
        {
            throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "a stat record is 9P2000 and .u only");
        }

        if (dialect == Dialect.P9_2000 && attr.Kind == FileKind.Symlink)
        {
            throw new NinePException(NinePError.FromEname("symlinks not supported"));
        }

        uint mode = ToMode(attr, dialect);
        StatRecord stat = new()
        {
            Type = 0,
            Dev = 0,
            Qid = attr.Qid with { Type = QidTypeFromMode(mode) },
            Mode = mode,
            ATime = (uint)attr.ATime.Seconds,
            MTime = (uint)attr.MTime.Seconds,
            Length = attr.Kind == FileKind.Directory ? 0 : attr.Size,
            Name = name,
            Uid = attr.UserName,
            Gid = attr.GroupName,
            Muid = attr.ModifierName,
        };

        return dialect == Dialect.P9_2000_u
            ? stat with
            {
                Extension = Extension(attr),
                NUid = attr.Uid,
                NGid = attr.Gid,
                NMuid = attr.ModifierUid,
            }
            : stat;
    }

    /// <summary>
    /// Projects attributes into an <c>Rgetattr</c>. The reply is always the full 160 bytes; a field
    /// the handler did not supply, or that the client did not ask for, carries zero, and the qid is
    /// valid whatever the mask says (reference §4.6). The mask has no bit of its own for
    /// <c>blksize</c>, which therefore travels with <c>BLOCKS</c>, the count it multiplies.
    /// An owner left at <see cref="Constants.NONUNAME"/> is one the handler did not state, and
    /// <c>.L</c> has no way to say that, so it carries zero here too (reference §8 rule 42).
    /// </summary>
    /// <param name="tag">The tag of the request being answered.</param>
    /// <param name="attr">The attributes.</param>
    /// <param name="requestMask">What the client asked for.</param>
    /// <param name="supplied">What the handler actually filled in.</param>
    /// <returns>The reply record.</returns>
    public static Rgetattr ToGetattr(ushort tag, Attr attr, GetAttrMask requestMask, GetAttrMask supplied)
    {
        ArgumentNullException.ThrowIfNull(attr);

        GetAttrMask valid = requestMask & supplied;
        uint mode = ToPosixMode(attr);

        return new Rgetattr(
            tag,
            valid,
            attr.Qid with { Type = QidTypeFromPosixMode(mode) },
            Marked(valid, GetAttrMask.Mode) ? mode : 0,
            Marked(valid, GetAttrMask.Uid) ? Mappable(attr.Uid) : 0,
            Marked(valid, GetAttrMask.Gid) ? Mappable(attr.Gid) : 0,
            Marked(valid, GetAttrMask.NLink) ? attr.NLink : 0,
            Marked(valid, GetAttrMask.Rdev) ? PackRdev(attr.Rdev) : 0,
            Marked(valid, GetAttrMask.Size) ? attr.Size : 0,
            Marked(valid, GetAttrMask.Blocks) ? attr.BlockSize : 0,
            Marked(valid, GetAttrMask.Blocks) ? attr.Blocks : 0,
            Marked(valid, GetAttrMask.ATime) ? attr.ATime : default,
            Marked(valid, GetAttrMask.MTime) ? attr.MTime : default,
            Marked(valid, GetAttrMask.CTime) ? attr.CTime : default,
            Marked(valid, GetAttrMask.BTime) ? attr.BTime : default,
            Marked(valid, GetAttrMask.Gen) ? attr.Gen : 0,
            Marked(valid, GetAttrMask.DataVersion) ? attr.DataVersion : 0);
    }

    /// <summary>
    /// Turns a <c>Twstat</c> stat record into one update. Reference §5.8 names the fields a
    /// <c>Twstat</c> may carry — <c>name</c>, <c>mode</c>, <c>mtime</c>, <c>gid</c>,
    /// <c>length</c> and, in <c>.u</c>, <c>n_gid</c>. The owner may never change, and
    /// <c>atime</c>, <c>muid</c>, <c>qid</c>, <c>type</c> and <c>dev</c> cannot be set at all: a
    /// record that asks for one of them is refused rather than answered <c>Rwstat</c> with
    /// nothing changed, which is a success reply for work that was never done.
    /// </summary>
    /// <param name="stat">The record the client sent.</param>
    /// <param name="dialect">The session dialect, which decides whether n_gid is present.</param>
    /// <returns>The update; an all-don't-touch record yields an fsync request.</returns>
    /// <exception cref="NinePException">The record sets a field <c>Twstat</c> cannot change.</exception>
    public static SetAttr FromWstat(in StatRecord stat, Dialect dialect) =>
        FromWstat(stat, dialect, null);

    /// <summary>
    /// Turns a <c>Twstat</c> stat record into one update, judged against the record a
    /// <c>Tstat</c> would return for the same file right now. "Don't touch" is not the only way a
    /// client says "leave this alone": a client that fills a <c>Twstat</c> from the record it just
    /// read sets every field to the value it already has, and such a field asks for no change at
    /// all. It is therefore a no-op rather than a refusal — which is what stops a Linux v9fs
    /// <c>.u</c> mount drawing <c>EPERM</c> for an ordinary <c>chmod</c> whose record carries the
    /// <c>uid</c>, <c>muid</c>, <c>type</c> and <c>dev</c> it read a moment ago. A field set to
    /// anything else is refused exactly as before, and with no current record every set field is
    /// refused, since nothing can be shown to be unchanged. The mode word's flag bits are not
    /// judged here: whether they ask for a change depends on the file's own flags, which the
    /// server core holds as an <see cref="Attr"/> and compares in every dialect (reference §8
    /// rule 19), so <see cref="SetAttr.Flags"/> is left null and the core fills it.
    /// </summary>
    /// <param name="stat">The record the client sent.</param>
    /// <param name="dialect">The session dialect, which decides whether n_gid is present.</param>
    /// <param name="current">What a <c>Tstat</c> would answer for this file now, when known.</param>
    /// <returns>The update; an all-don't-touch record yields an fsync request.</returns>
    /// <exception cref="NinePException">The record changes a field <c>Twstat</c> cannot change.</exception>
    public static SetAttr FromWstat(in StatRecord stat, Dialect dialect, in StatRecord? current)
    {
        if (stat.IsAllDontTouch)
        {
            return new SetAttr();
        }

        // Without a current record every "don't touch" field of this stand-in differs from every
        // value a client could set, so the tests below reduce to the plain refusals they were.
        StatRecord now = current ?? StatRecord.DontTouch;

        Refuse(
            stat.Uid.Length != 0 && !string.Equals(stat.Uid, now.Uid, StringComparison.Ordinal),
            "wstat cannot change the owner");
        Refuse(
            stat.NUid != Constants.NONUNAME && stat.NUid != now.NUid,
            "wstat cannot change the owner");
        Refuse(
            stat.Muid.Length != 0 && !string.Equals(stat.Muid, now.Muid, StringComparison.Ordinal),
            "wstat cannot set muid");
        Refuse(
            stat.NMuid != Constants.NONUNAME && stat.NMuid != now.NMuid,
            "wstat cannot set muid");
        Refuse(stat.ATime != uint.MaxValue && stat.ATime != now.ATime, "wstat cannot set atime");
        Refuse(stat.Type != ushort.MaxValue && stat.Type != now.Type, "wstat cannot set type");
        Refuse(stat.Dev != uint.MaxValue && stat.Dev != now.Dev, "wstat cannot set dev");
        Refuse(
            (stat.Qid.Type != (QidType)0xFF
                || stat.Qid.Version != uint.MaxValue
                || stat.Qid.Path != ulong.MaxValue)
            && stat.Qid != now.Qid,
            "wstat cannot set qid");

        uint? gid = dialect == Dialect.P9_2000_u && stat.NGid != Constants.NONUNAME
            ? stat.NGid
            : null;

        return new SetAttr
        {
            Name = stat.Name.Length == 0 ? null : stat.Name,

            // Reference §8 rule 19: the .u DMSETUID / DMSETGID / DMSETVTX bits map onto the 07777
            // permission bits and are honoured. PermOf is the inverse of the UnixPermissionBits
            // that ToMode already sends, so a v9fs `chmod u+s` -- which arrives as DMSETUID in the
            // high bits, not as 04000 in the low ones -- reaches the handler instead of being
            // masked away by a 0xFFF that could never have seen it.
            Perm = stat.Mode == uint.MaxValue ? null : PermOf(stat.Mode, dialect),
            GroupName = stat.Gid.Length == 0 ? null : stat.Gid,
            Gid = gid,
            Size = stat.Length == ulong.MaxValue ? null : stat.Length,
            MTime = stat.MTime == uint.MaxValue ? null : new TimeSpec(stat.MTime, 0),
        };
    }

    /// <summary>
    /// Turns a <c>Tsetattr</c> into one update. A time bit without its <c>_SET</c> twin does not
    /// carry a value: it asks for the server's clock, which <see cref="ResolveServerTimes"/> then
    /// supplies from the injected <see cref="TimeProvider"/> (reference §4.6).
    /// </summary>
    /// <param name="message">The message the client sent.</param>
    /// <returns>The update.</returns>
    public static SetAttr FromSetattr(in Tsetattr message)
    {
        SetAttrMask valid = message.Valid;
        bool atime = valid.HasFlag(SetAttrMask.ATime);
        bool mtime = valid.HasFlag(SetAttrMask.MTime);

        return new SetAttr
        {
            Perm = valid.HasFlag(SetAttrMask.Mode)
                ? (FilePermissions)(message.Mode & ModeBits.FullPermissions)
                : null,
            Uid = valid.HasFlag(SetAttrMask.Uid) ? message.Uid : null,
            Gid = valid.HasFlag(SetAttrMask.Gid) ? message.Gid : null,
            Size = valid.HasFlag(SetAttrMask.Size) ? message.Size : null,
            ATime = atime && valid.HasFlag(SetAttrMask.ATimeSet) ? message.ATime : null,
            MTime = mtime && valid.HasFlag(SetAttrMask.MTimeSet) ? message.MTime : null,
            ATimeToNow = atime && !valid.HasFlag(SetAttrMask.ATimeSet),
            MTimeToNow = mtime && !valid.HasFlag(SetAttrMask.MTimeSet),
            CTimeToNow = valid.HasFlag(SetAttrMask.CTime),
        };
    }

    /// <summary>
    /// Fills the times a client asked the server to choose from the injected clock (S-32), so that
    /// a handler receives values rather than a flag it would have to interpret itself.
    /// </summary>
    /// <param name="update">The update as it came off the wire.</param>
    /// <param name="timeProvider">The clock the session was configured with.</param>
    /// <returns>The update with the server-clock times filled in.</returns>
    public static SetAttr ResolveServerTimes(SetAttr update, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (!update.ATimeToNow && !update.MTimeToNow)
        {
            return update;
        }

        TimeSpec now = ToTimeSpec(timeProvider.GetUtcNow());
        return update with
        {
            ATime = update.ATimeToNow ? now : update.ATime,
            MTime = update.MTimeToNow ? now : update.MTime,
        };
    }

    /// <summary>The POSIX timestamp an instant projects to.</summary>
    /// <param name="instant">The instant the clock reported.</param>
    /// <returns>Whole seconds since the epoch plus nanoseconds within the second.</returns>
    public static TimeSpec ToTimeSpec(DateTimeOffset instant)
    {
        long seconds = instant.ToUnixTimeSeconds();
        long ticks = instant.UtcTicks - DateTimeOffset.FromUnixTimeSeconds(seconds).UtcTicks;
        return new TimeSpec(seconds, (uint)(ticks * (1_000_000_000L / TimeSpan.TicksPerSecond)));
    }

    private static bool Marked(GetAttrMask valid, GetAttrMask bit) => (valid & bit) != 0;

    // Reference §8 rule 42. NONUNAME is this library's "the handler did not state an owner", and
    // 9P2000 and .u can say that because ownership travels there as a name with the number beside
    // it optional. .L cannot: uid and gid are plain required numbers with no sentinel, and
    // 0xFFFFFFFF is (uid_t)-1, which Linux refuses to map -- it shows the file as the overflow
    // user and answers EOVERFLOW to anything needing the real owner, deciding that locally so no
    // request reaches the server to be refused honestly. Nothing is lost by sending zero instead:
    // no client can act on (uid_t)-1 either.
    private static uint Mappable(uint id) => id == Constants.NONUNAME ? 0 : id;

    private static ulong PackRdev(DeviceId? rdev) =>
        rdev is DeviceId id ? ((ulong)id.Major << 8) | id.Minor : 0;

    /// <summary>
    /// The file flags a client may set, at create and through <c>Twstat</c> (open(2), stat(5);
    /// reference §8 rule 19). <see cref="FileFlags.Auth"/> and <see cref="FileFlags.Mount"/> are
    /// the server's own.
    /// </summary>
    public const FileFlags SettableFlags = FileFlags.Append | FileFlags.Exclusive | FileFlags.Temporary;

    /// <summary>The <c>DM*</c> high bits of a mode word that a set of file flags projects to.</summary>
    /// <param name="flags">The flags.</param>
    /// <returns>The high bits of reference §4.4, with the permission bits zero.</returns>
    public static uint HighFlagBits(FileFlags flags)
    {
        uint mode = 0;
        if (flags.HasFlag(FileFlags.Append))
        {
            mode |= ModeBits.DMAPPEND;
        }

        if (flags.HasFlag(FileFlags.Exclusive))
        {
            mode |= ModeBits.DMEXCL;
        }

        if (flags.HasFlag(FileFlags.Temporary))
        {
            mode |= ModeBits.DMTMP;
        }

        if (flags.HasFlag(FileFlags.Auth))
        {
            mode |= ModeBits.DMAUTH;
        }

        if (flags.HasFlag(FileFlags.Mount))
        {
            mode |= ModeBits.DMMOUNT;
        }

        return mode;
    }

    private static uint KindBits(FileKind kind, Dialect dialect)
    {
        if (kind == FileKind.Directory)
        {
            return ModeBits.DMDIR;
        }

        // 9P2000 has no bit for any of these; the file is reported as a plain file, except for a
        // symlink, which ToStat refuses outright rather than misreport.
        return dialect != Dialect.P9_2000_u ? 0 : kind switch
        {
            FileKind.Symlink => ModeBits.DMSYMLINK,
            FileKind.Fifo => ModeBits.DMNAMEDPIPE,
            FileKind.Socket => ModeBits.DMSOCKET,
            FileKind.CharDevice or FileKind.BlockDevice => ModeBits.DMDEVICE,
            _ => 0,
        };
    }

    private static uint UnixPermissionBits(FilePermissions permissions)
    {
        uint mode = 0;
        if (permissions.HasFlag(FilePermissions.SetUid))
        {
            mode |= ModeBits.DMSETUID;
        }

        if (permissions.HasFlag(FilePermissions.SetGid))
        {
            mode |= ModeBits.DMSETGID;
        }

        if (permissions.HasFlag(FilePermissions.Sticky))
        {
            mode |= ModeBits.DMSETVTX;
        }

        return mode;
    }

    private static uint PosixTypeBits(FileKind kind) => kind switch
    {
        FileKind.Directory => ModeBits.S_IFDIR,
        FileKind.Symlink => ModeBits.S_IFLNK,
        FileKind.Fifo => ModeBits.S_IFIFO,
        FileKind.Socket => ModeBits.S_IFSOCK,
        FileKind.CharDevice => ModeBits.S_IFCHR,
        FileKind.BlockDevice => ModeBits.S_IFBLK,
        _ => ModeBits.S_IFREG,
    };

    private static string Extension(Attr attr) => attr.Kind switch
    {
        FileKind.Symlink => attr.SymlinkTarget ?? string.Empty,
        FileKind.BlockDevice => DeviceExtension('b', attr.Rdev),
        FileKind.CharDevice => DeviceExtension('c', attr.Rdev),
        _ => string.Empty,
    };

    private static string DeviceExtension(char prefix, DeviceId? rdev) =>
        rdev is DeviceId id
            ? string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}", prefix, id.Major, id.Minor)
            : string.Empty;

    /// <summary>
    /// The attributes a 9P2000 / .u stat record carries: the inverse of <see cref="ToStat"/>, so a
    /// client hands its caller the same dialect-neutral record a server handler produced.
    /// </summary>
    /// <param name="stat">The record as it came off the wire.</param>
    /// <param name="dialect">The session dialect, which decides which fields are present.</param>
    /// <returns>The attributes.</returns>
    public static Attr FromStat(in StatRecord stat, Dialect dialect)
    {
        FileKind kind = KindOf(stat.Qid, stat.Mode, stat.Extension, dialect);

        return new Attr
        {
            Qid = stat.Qid,
            Kind = kind,
            Perm = PermOf(stat.Mode, dialect),
            Flags = FlagsOf(stat.Mode),
            UserName = stat.Uid,
            GroupName = stat.Gid,
            ModifierName = stat.Muid,
            Uid = stat.NUid,
            Gid = stat.NGid,
            ModifierUid = stat.NMuid,
            Rdev = kind is FileKind.CharDevice or FileKind.BlockDevice ? ParseDevice(stat.Extension) : null,
            Size = stat.Length,
            ATime = new TimeSpec(stat.ATime, 0),
            MTime = new TimeSpec(stat.MTime, 0),
            SymlinkTarget = kind == FileKind.Symlink && dialect == Dialect.P9_2000_u
                ? stat.Extension
                : null,
        };
    }

    /// <summary>
    /// The attributes an <c>Rgetattr</c> carries: the inverse of <see cref="ToGetattr"/>. Reference
    /// §8 rule 17: only the fields <c>valid</c> marks are read, and an unmarked one keeps the
    /// <see cref="Attr"/> default rather than the zero the reply is required to pad with — a
    /// server that marks nothing but the qid was otherwise read back as a file of mode 0, size 0,
    /// nlink 0 and epoch times, which is a fabricated answer rather than a missing one. With
    /// <c>MODE</c> unmarked there is no POSIX mode word to take the kind from, so the kind comes
    /// from the qid type byte, which §4.6 says is always valid.
    /// </summary>
    /// <param name="reply">The reply as it came off the wire.</param>
    /// <returns>The attributes; a field the server did not mark valid stays at its default.</returns>
    public static Attr FromGetattr(in Rgetattr reply)
    {
        GetAttrMask valid = reply.Valid;
        bool mode = Marked(valid, GetAttrMask.Mode);
        bool device = mode && (reply.Mode & ModeBits.S_IFMT) is ModeBits.S_IFCHR or ModeBits.S_IFBLK;

        Attr attr = new()
        {
            Qid = reply.Qid,
            Kind = mode ? PosixKindOf(reply.Mode) : KindOfQidType(reply.Qid.Type),
            Perm = mode ? (FilePermissions)(reply.Mode & ModeBits.FullPermissions) : FilePermissions.None,
            NLink = Marked(valid, GetAttrMask.NLink) ? reply.NLink : 1,
            Uid = Marked(valid, GetAttrMask.Uid) ? reply.Uid : Constants.NONUNAME,
            Gid = Marked(valid, GetAttrMask.Gid) ? reply.Gid : Constants.NONUNAME,
            Rdev = device && Marked(valid, GetAttrMask.Rdev) ? UnpackRdev(reply.Rdev) : null,
        };

        // Each remaining field is applied only when it is marked, so an unmarked one keeps the
        // record's own default: `with` is what "leave it alone" spells here.
        if (Marked(valid, GetAttrMask.Size))
        {
            attr = attr with { Size = reply.Size };
        }

        // §4.6 gives blksize no bit of its own; it travels with BLOCKS, the count it multiplies,
        // exactly as ToGetattr sends it.
        if (Marked(valid, GetAttrMask.Blocks))
        {
            attr = attr with { BlockSize = reply.BlkSize, Blocks = reply.Blocks };
        }

        if (Marked(valid, GetAttrMask.ATime))
        {
            attr = attr with { ATime = reply.ATime };
        }

        if (Marked(valid, GetAttrMask.MTime))
        {
            attr = attr with { MTime = reply.MTime };
        }

        if (Marked(valid, GetAttrMask.CTime))
        {
            attr = attr with { CTime = reply.CTime };
        }

        if (Marked(valid, GetAttrMask.BTime))
        {
            attr = attr with { BTime = reply.BTime };
        }

        if (Marked(valid, GetAttrMask.Gen))
        {
            attr = attr with { Gen = reply.Gen };
        }

        if (Marked(valid, GetAttrMask.DataVersion))
        {
            attr = attr with { DataVersion = reply.DataVersion };
        }

        return attr;
    }

    /// <summary>The file kind a qid type byte names, for a reply whose mode word is not valid.</summary>
    /// <param name="type">The qid type byte, which reference §4.6 says is always valid.</param>
    /// <returns>The kind the qid can express: a directory, a symlink, or a plain file.</returns>
    private static FileKind KindOfQidType(QidType type)
    {
        if ((type & QidType.QTDIR) != 0)
        {
            return FileKind.Directory;
        }

        return (type & QidType.QTSYMLINK) != 0 ? FileKind.Symlink : FileKind.File;
    }

    /// <summary>
    /// The <c>Twstat</c> record one update projects to: every field the caller did not set keeps
    /// its "don't touch" value, so an update of one field changes exactly one field (reference §5.8).
    /// Reference §8 rule 15: a field <c>Twstat</c> has no slot for is refused here, before the
    /// message is built, because the record that carries none of them is not an empty update — an
    /// all-don't-touch <c>Twstat</c> is stat(5)'s fsync (§4.2), so silently dropping the only
    /// field an update named would send the server a request to flush the file and report that as
    /// the change having been made.
    /// </summary>
    /// <param name="update">The partial update.</param>
    /// <param name="dialect">The session dialect, which decides whether n_gid is on the wire.</param>
    /// <returns>The record to send.</returns>
    /// <exception cref="NinePException">The update names a field <c>Twstat</c> cannot carry.</exception>
    public static StatRecord ToWstat(SetAttr update, Dialect dialect)
    {
        ValidateWstat(update, dialect);

        // Reference §8 rule 19: a Twstat mode word carries the permission bits and the file
        // flags together, so an update that states one half and not the other has no honest
        // spelling -- a zero in the flag bits would clear DMAPPEND on an append-only file the
        // caller only meant to chmod, and a zero in the permission bits is a chmod 000. The
        // client's SetAttrAsync fills the missing half from a Tstat (CompleteMode); the
        // projector itself refuses the half-stated word rather than guess.
        if ((update.Perm is null) != (update.Flags is null))
        {
            throw new NinePException(new NinePError(
                "a wstat mode word carries the permission bits and the file flags together; state both",
                (int)Errno.EINVAL));
        }

        StatRecord record = StatRecord.DontTouch with
        {
            Name = update.Name ?? string.Empty,
            Mode = update.Perm is FilePermissions bits
                ? ModeOf(bits, dialect) | HighFlagBits(update.Flags ?? FileFlags.None)
                : uint.MaxValue,
            MTime = update.MTime is TimeSpec mtime ? (uint)mtime.Seconds : uint.MaxValue,
            Length = update.Size ?? ulong.MaxValue,
            Gid = update.GroupName ?? string.Empty,
        };

        return dialect == Dialect.P9_2000_u
            ? record with { Extension = string.Empty, NGid = update.Gid ?? Constants.NONUNAME }
            : record;
    }

    /// <summary>
    /// Refuses an update a <c>Twstat</c> in this dialect cannot carry: every check
    /// <see cref="ToWstat"/> makes except the completeness of the mode word, so that a client can
    /// refuse before it reads the record it may need to complete that word (reference §8 rules 15
    /// and 19). Nothing here inspects the file; it is the update alone that is judged.
    /// </summary>
    /// <param name="update">The partial update.</param>
    /// <param name="dialect">The session dialect.</param>
    /// <exception cref="NinePException">The update names a field <c>Twstat</c> cannot carry.</exception>
    public static void ValidateWstat(SetAttr update, Dialect dialect)
    {
        ArgumentNullException.ThrowIfNull(update);

        // stat(5): the owner may never change through a wstat, in either dialect. Dropping the
        // field silently would let a caller believe a chown happened.
        if (update.Uid is not null)
        {
            throw new NinePException(NinePError.FromEname("wstat cannot change the owner"));
        }

        // stat(5) lists atime among the fields a wstat may not set at all; it is EPERM rather than
        // EINVAL because the record does have an atime slot and the rule is a prohibition, not a
        // missing field.
        Refuse(update.ATime is not null, "wstat cannot set atime");

        // A Twstat carries a value, never "use the server's clock": there is no _SET twin to leave
        // off, so the .L "now" flags have no wstat spelling at all.
        if (update.ATimeToNow || update.MTimeToNow || update.CTimeToNow)
        {
            throw new NinePException(new NinePError(
                "wstat carries a time value, not a request for the server's clock", (int)Errno.EINVAL));
        }

        // n_gid is a 9P2000.u field: plain 9P2000's stat record has textual ids and nothing else,
        // so a numeric group has nowhere to go on that wire.
        if (update.Gid is not null && dialect != Dialect.P9_2000_u)
        {
            throw new NinePException(new NinePError(
                "a numeric group needs 9P2000.u; plain 9P2000 has no n_gid", (int)Errno.EINVAL));
        }

        // Reference §8 rule 15 and rule 19: setuid, setgid and sticky are spelled in .u as the
        // DM* high bits and are not spelled in plain 9P2000 at all, whose mode word carries the
        // rwx bits and nothing else. Masking them off there would answer success for a chmod that
        // did not happen.
        if (update.Perm is FilePermissions perm && (perm & ~(FilePermissions)ModeBits.Permissions) != FilePermissions.None
            && dialect != Dialect.P9_2000_u)
        {
            throw new NinePException(new NinePError(
                "setuid, setgid and sticky need 9P2000.u; plain 9P2000 has only the rwx bits",
                (int)Errno.EINVAL));
        }

        // DMAUTH and DMMOUNT are the server's; stat(5) lets a client set the other three.
        if (update.Flags is FileFlags asked && (asked & ~SettableFlags) != FileFlags.None)
        {
            throw new NinePException(new NinePError("wstat cannot set DMAUTH or DMMOUNT", (int)Errno.EPERM));
        }
    }

    /// <summary>
    /// Fills whichever half of the mode word an update leaves unstated from the record a
    /// <c>Tstat</c> just answered, so that <see cref="ToWstat"/> sends the file's own permission
    /// bits beside new flags, or its own flags beside new permission bits (reference §8 rule 19).
    /// This is what Plan 9's <c>chmod</c> and Linux v9fs do: read the record, change the bits,
    /// write the whole word back.
    /// </summary>
    /// <param name="update">The partial update.</param>
    /// <param name="current">What the server answered a <c>Tstat</c> with.</param>
    /// <param name="dialect">The session dialect, which decides how the permission bits are read.</param>
    /// <returns>The update with both halves of the mode word stated, or the update as it was.</returns>
    public static SetAttr CompleteMode(SetAttr update, in StatRecord current, Dialect dialect)
    {
        ArgumentNullException.ThrowIfNull(update);

        if ((update.Perm is null) == (update.Flags is null))
        {
            return update;
        }

        return update with
        {
            Perm = update.Perm ?? PermOf(current.Mode, dialect),
            Flags = update.Flags ?? (FlagsOf(current.Mode) & SettableFlags),
        };
    }

    /// <summary>Refuses a <c>Twstat</c> that asks for a field reference §5.8 does not let it set.</summary>
    /// <param name="asked">True when the record carries a value for that field.</param>
    /// <param name="what">What was asked for, for the <c>ename</c>.</param>
    /// <exception cref="NinePException">Always, when <paramref name="asked"/> is true.</exception>
    private static void Refuse(bool asked, string what)
    {
        if (asked)
        {
            throw new NinePException(new NinePError(what, (int)Errno.EPERM));
        }
    }

    /// <summary>The <c>Tsetattr</c> one update projects to: the inverse of <see cref="FromSetattr"/>.</summary>
    /// <param name="tag">The tag the multiplexer will replace.</param>
    /// <param name="fid">The fid to change.</param>
    /// <param name="update">The partial update.</param>
    /// <returns>The message to send.</returns>
    /// <exception cref="NinePException">The update names a field <c>Tsetattr</c> cannot carry.</exception>
    public static Tsetattr ToSetattr(ushort tag, uint fid, SetAttr update)
    {
        ArgumentNullException.ThrowIfNull(update);

        // Tsetattr has no name field; a rename is Trename or Trenameat, not an attribute change.
        if (update.Name is not null)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
        }

        // Nor does it have a textual group: .L identifies users and groups by number only
        // (reference §6), so a group *name* has no slot in the message (reference §8 rule 15).
        if (update.GroupName is not null)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
        }

        // Nor the file flags: Tsetattr.mode is a POSIX mode word, in which DMAPPEND, DMEXCL and
        // DMTMP have no bit at all (reference §4.7 and §8 rule 19). Sending the message with the
        // flags left out would answer success for a change that never reached the server.
        if (update.Flags is not null)
        {
            throw new NinePException(new NinePError(
                "9P2000.L has no spelling for DMAPPEND, DMEXCL or DMTMP", (int)Errno.EINVAL));
        }

        SetAttrMask valid = SetAttrMask.None;
        valid |= update.Perm is null ? SetAttrMask.None : SetAttrMask.Mode;
        valid |= update.Uid is null ? SetAttrMask.None : SetAttrMask.Uid;
        valid |= update.Gid is null ? SetAttrMask.None : SetAttrMask.Gid;
        valid |= update.Size is null ? SetAttrMask.None : SetAttrMask.Size;
        valid |= update.ATime is null ? SetAttrMask.None : SetAttrMask.ATime | SetAttrMask.ATimeSet;
        valid |= update.MTime is null ? SetAttrMask.None : SetAttrMask.MTime | SetAttrMask.MTimeSet;
        valid |= update.ATimeToNow ? SetAttrMask.ATime : SetAttrMask.None;
        valid |= update.MTimeToNow ? SetAttrMask.MTime : SetAttrMask.None;
        valid |= update.CTimeToNow ? SetAttrMask.CTime : SetAttrMask.None;

        return new Tsetattr(
            tag,
            fid,
            valid,
            (uint)(update.Perm ?? FilePermissions.None),
            update.Uid ?? 0,
            update.Gid ?? 0,
            update.Size ?? 0,
            update.ATime ?? default,
            update.MTime ?? default);
    }

    /// <summary>The file kind a POSIX mode word names (reference §4.7).</summary>
    /// <param name="posixMode">The <c>st_mode</c> value.</param>
    /// <returns>The dialect-neutral file kind.</returns>
    public static FileKind PosixKindOf(uint posixMode) => (posixMode & ModeBits.S_IFMT) switch
    {
        ModeBits.S_IFDIR => FileKind.Directory,
        ModeBits.S_IFLNK => FileKind.Symlink,
        ModeBits.S_IFIFO => FileKind.Fifo,
        ModeBits.S_IFSOCK => FileKind.Socket,
        ModeBits.S_IFCHR => FileKind.CharDevice,
        ModeBits.S_IFBLK => FileKind.BlockDevice,
        _ => FileKind.File,
    };

    private static FileKind KindOf(in Qid qid, uint mode, string? extension, Dialect dialect)
    {
        if ((mode & ModeBits.DMDIR) != 0)
        {
            return FileKind.Directory;
        }

        // Reference §8 rule 17: Attr.Kind and the qid type byte always agree, which is Attr's own
        // documented invariant. A plain-9P2000 server has no DMSYMLINK to set but can still mark
        // the qid QTSYMLINK, and reporting that file as a plain file left Kind and Attr.Qid.Type
        // contradicting each other in the record handed to the caller.
        if ((qid.Type & QidType.QTSYMLINK) != 0)
        {
            return FileKind.Symlink;
        }

        // 9P2000 has no bit for any other kind, so everything else is a plain file there.
        if (dialect != Dialect.P9_2000_u)
        {
            return FileKind.File;
        }

        if ((mode & ModeBits.DMSYMLINK) != 0)
        {
            return FileKind.Symlink;
        }

        if ((mode & ModeBits.DMNAMEDPIPE) != 0)
        {
            return FileKind.Fifo;
        }

        if ((mode & ModeBits.DMSOCKET) != 0)
        {
            return FileKind.Socket;
        }

        if ((mode & ModeBits.DMDEVICE) == 0)
        {
            return FileKind.File;
        }

        return extension is not null && extension.StartsWith("b ", StringComparison.Ordinal)
            ? FileKind.BlockDevice
            : FileKind.CharDevice;
    }

    /// <summary>
    /// The mode word a <c>Twstat</c> carries for one permission value: the inverse of
    /// <see cref="PermOf"/>, so what a client sends and what a server reads back agree bit for bit
    /// (reference §8 rule 19).
    /// </summary>
    /// <param name="perm">The permission value.</param>
    /// <param name="dialect">The session dialect; only .u has the three high bits.</param>
    /// <returns>The mode word.</returns>
    private static uint ModeOf(FilePermissions perm, Dialect dialect) => dialect == Dialect.P9_2000_u
        ? ((uint)perm & ModeBits.Permissions) | UnixPermissionBits(perm)
        : (uint)perm & ModeBits.Permissions;

    private static FilePermissions PermOf(uint mode, Dialect dialect)
    {
        uint perm = mode & ModeBits.Permissions;
        if (dialect != Dialect.P9_2000_u)
        {
            return (FilePermissions)perm;
        }

        perm |= (mode & ModeBits.DMSETUID) != 0 ? ModeBits.S_ISUID : 0;
        perm |= (mode & ModeBits.DMSETGID) != 0 ? ModeBits.S_ISGID : 0;
        perm |= (mode & ModeBits.DMSETVTX) != 0 ? ModeBits.S_ISVTX : 0;
        return (FilePermissions)perm;
    }

    /// <summary>The file flags a 9P2000 / .u mode word carries: the inverse of <see cref="HighFlagBits"/>.</summary>
    /// <param name="mode">The mode word of a stat record or a <c>Tcreate.perm</c>.</param>
    /// <returns>The flags its high bits name.</returns>
    public static FileFlags FlagsOf(uint mode)
    {
        FileFlags flags = FileFlags.None;
        flags |= (mode & ModeBits.DMAPPEND) != 0 ? FileFlags.Append : FileFlags.None;
        flags |= (mode & ModeBits.DMEXCL) != 0 ? FileFlags.Exclusive : FileFlags.None;
        flags |= (mode & ModeBits.DMTMP) != 0 ? FileFlags.Temporary : FileFlags.None;
        flags |= (mode & ModeBits.DMAUTH) != 0 ? FileFlags.Auth : FileFlags.None;
        flags |= (mode & ModeBits.DMMOUNT) != 0 ? FileFlags.Mount : FileFlags.None;
        return flags;
    }

    private static DeviceId UnpackRdev(ulong rdev) =>
        new((uint)(rdev >> 8), (uint)(rdev & 0xFF));

    private static DeviceId? ParseDevice(string? extension)
    {
        string[] parts = (extension ?? string.Empty).Split(' ');
        return parts.Length == 3
            && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint major)
            && uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint minor)
            ? new DeviceId(major, minor)
            : null;
    }
}
