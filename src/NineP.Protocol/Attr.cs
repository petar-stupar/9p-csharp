namespace NineP.Protocol;

/// <summary>
/// The dialect-neutral attributes of one file (reference §7). A handler produces exactly one of
/// these and the protocol layer projects it into a 9P2000 stat record, a 9P2000.u stat record or
/// an <c>Rgetattr</c>; no handler ever writes a dialect-specific shape itself.
/// </summary>
public sealed record Attr
{
    /// <summary>The file's qid; its type byte must agree with <see cref="Kind"/> and <see cref="Flags"/>.</summary>
    public required Qid Qid { get; init; }

    /// <summary>The file type (reference §7).</summary>
    public required FileKind Kind { get; init; }

    /// <summary>Permission bits only: 0777 plus setuid, setgid and sticky (the 07777 mask).</summary>
    public required FilePermissions Perm { get; init; }

    /// <summary>Non-permission mode bits: append, exclusive, temporary, auth.</summary>
    public FileFlags Flags { get; init; }

    /// <summary>The hard-link count; 1 for a synthetic file.</summary>
    public ulong NLink { get; init; } = 1;

    /// <summary>The textual owner, which is the 9P2000 stat record's <c>uid</c>.</summary>
    public string UserName { get; init; } = "";

    /// <summary>The textual group, which is the 9P2000 stat record's <c>gid</c>.</summary>
    public string GroupName { get; init; } = "";

    /// <summary>The textual last modifier, which is the 9P2000 stat record's <c>muid</c>.</summary>
    public string ModifierName { get; init; } = "";

    /// <summary>The numeric owner (.u <c>n_uid</c>, .L <c>uid</c>); NONUNAME when unknown.</summary>
    public uint Uid { get; init; } = Constants.NONUNAME;

    /// <summary>The numeric group (.u <c>n_gid</c>, .L <c>gid</c>); NONUNAME when unknown.</summary>
    public uint Gid { get; init; } = Constants.NONUNAME;

    /// <summary>The numeric last modifier (.u <c>n_muid</c>); NONUNAME when unknown.</summary>
    public uint ModifierUid { get; init; } = Constants.NONUNAME;

    /// <summary>The device numbers of a character or block device; null for anything else.</summary>
    public DeviceId? Rdev { get; init; }

    /// <summary>The file length in bytes; the projection reports 0 for a directory (reference §4.2).</summary>
    public ulong Size { get; init; }

    /// <summary>The preferred I/O block size an <c>Rgetattr</c> reports.</summary>
    public ulong BlockSize { get; init; } = 4096;

    /// <summary>The allocated 512-byte block count an <c>Rgetattr</c> reports.</summary>
    public ulong Blocks { get; init; }

    /// <summary>The last access time.</summary>
    public TimeSpec ATime { get; init; }

    /// <summary>The last modification time.</summary>
    public TimeSpec MTime { get; init; }

    /// <summary>The last status-change time (.L only).</summary>
    public TimeSpec CTime { get; init; }

    /// <summary>The creation time (.L only; zero when unknown).</summary>
    public TimeSpec BTime { get; init; }

    /// <summary>The generation number (.L only).</summary>
    public ulong Gen { get; init; }

    /// <summary>The data version (.L only).</summary>
    public ulong DataVersion { get; init; }

    /// <summary>The target of a <see cref="FileKind.Symlink"/>; null for anything else.</summary>
    public string? SymlinkTarget { get; init; }
}
