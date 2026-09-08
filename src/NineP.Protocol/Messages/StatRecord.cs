namespace NineP.Protocol.Messages;

/// <summary>
/// A 9P2000 / 9P2000.u stat record (reference §4.2). In a 9P2000 session <see cref="Extension"/>
/// is null and the three numeric ids are <see cref="Constants.NONUNAME"/>: which fields are on
/// the wire is decided by the session dialect, never by sniffing bytes.
/// </summary>
public readonly record struct StatRecord
{
    private const ushort DontTouchU16 = 0xFFFF;
    private const uint DontTouchU32 = 0xFFFFFFFF;
    private const ulong DontTouchU64 = 0xFFFFFFFFFFFFFFFF;

    private readonly string? _name;
    private readonly string? _uid;
    private readonly string? _gid;
    private readonly string? _muid;
    private readonly uint? _nuid;
    private readonly uint? _ngid;
    private readonly uint? _nmuid;

    /// <summary>For kernel use; a server that does not care sends zero.</summary>
    public ushort Type { get; init; }

    /// <summary>For kernel use; a server that does not care sends zero.</summary>
    public uint Dev { get; init; }

    /// <summary>The qid of the file this record describes.</summary>
    public Qid Qid { get; init; }

    /// <summary>The permission and type bits of reference §4.4.</summary>
    public uint Mode { get; init; }

    /// <summary>The last access time, in whole seconds since the Unix epoch.</summary>
    public uint ATime { get; init; }

    /// <summary>The last modification time, in whole seconds since the Unix epoch.</summary>
    public uint MTime { get; init; }

    /// <summary>The file length in bytes; zero for a directory.</summary>
    public ulong Length { get; init; }

    /// <summary>The file's name; "/" for the root of a served tree.</summary>
    public string Name { get => _name ?? string.Empty; init => _name = Normalize(value); }

    /// <summary>The textual owner.</summary>
    public string Uid { get => _uid ?? string.Empty; init => _uid = Normalize(value); }

    /// <summary>The textual group.</summary>
    public string Gid { get => _gid ?? string.Empty; init => _gid = Normalize(value); }

    /// <summary>The textual name of the last user to modify the file.</summary>
    public string Muid { get => _muid ?? string.Empty; init => _muid = Normalize(value); }

    /// <summary>
    /// The .u extension: a symlink target, or "b maj min" / "c maj min" for a device. Null in a
    /// 9P2000 session, where the field is not on the wire at all.
    /// </summary>
    public string? Extension { get; init; }

    /// <summary>The numeric owner (.u); NONUNAME when absent, as in every 9P2000 session.</summary>
    public uint NUid { get => _nuid ?? Constants.NONUNAME; init => _nuid = Normalize(value); }

    /// <summary>The numeric group (.u); NONUNAME when absent, as in every 9P2000 session.</summary>
    public uint NGid { get => _ngid ?? Constants.NONUNAME; init => _ngid = Normalize(value); }

    /// <summary>The numeric last modifier (.u); NONUNAME when absent, as in every 9P2000 session.</summary>
    public uint NMuid { get => _nmuid ?? Constants.NONUNAME; init => _nmuid = Normalize(value); }

    /// <summary>
    /// A record whose every integer field is the "don't touch" value of its width and whose every
    /// string is empty: the shape a <c>Twstat</c> takes when it means fsync (reference §4.2).
    /// </summary>
    public static StatRecord DontTouch => new()
    {
        Type = DontTouchU16,
        Dev = DontTouchU32,
        Qid = new Qid((QidType)0xFF, DontTouchU32, DontTouchU64),
        Mode = DontTouchU32,
        ATime = DontTouchU32,
        MTime = DontTouchU32,
        Length = DontTouchU64,
    };

    /// <summary>
    /// True when every field carries its "don't touch" value, which makes a <c>Twstat</c> a
    /// request to commit the file to stable storage rather than to change it (reference §4.2).
    /// </summary>
    public bool IsAllDontTouch =>
        Type == DontTouchU16 &&
        Dev == DontTouchU32 &&
        Qid.Type == (QidType)0xFF &&
        Qid.Version == DontTouchU32 &&
        Qid.Path == DontTouchU64 &&
        Mode == DontTouchU32 &&
        ATime == DontTouchU32 &&
        MTime == DontTouchU32 &&
        Length == DontTouchU64 &&
        Name.Length == 0 &&
        Uid.Length == 0 &&
        Gid.Length == 0 &&
        Muid.Length == 0 &&
        (Extension is null || Extension.Length == 0) &&
        NUid == DontTouchU32 &&
        NGid == DontTouchU32 &&
        NMuid == DontTouchU32;

    /// <summary>
    /// The number of bytes this record occupies after its own leading <c>size[2]</c>, which is the
    /// value that leading field carries.
    /// </summary>
    /// <param name="dialect">The session dialect, which decides whether the .u fields are present.</param>
    /// <returns>The encoded length in bytes.</returns>
    public int GetEncodedSize(Dialect dialect)
    {
        // type[2] dev[4] qid[13] mode[4] atime[4] mtime[4] length[8], then four counted strings.
        int size = 39
            + 8
            + Internal.NinePText.GetByteCount(Name)
            + Internal.NinePText.GetByteCount(Uid)
            + Internal.NinePText.GetByteCount(Gid)
            + Internal.NinePText.GetByteCount(Muid);

        if (dialect == Dialect.P9_2000_u)
        {
            size += 2 + Internal.NinePText.GetByteCount(Extension ?? string.Empty) + 12;
        }

        return size;
    }
    // A stat string is empty or it is not there; storing "" as null keeps two records that mean
    // the same thing equal, which the golden-vector round trip depends on. The numeric ids get
    // the same treatment for NONUNAME, so a default record and one decoded from a 9P2000 frame
    // are equal, and so are DontTouch and a .u record that carried 0xFFFFFFFF on the wire.
    private static string? Normalize(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static uint? Normalize(uint value) => value == Constants.NONUNAME ? null : value;
}
