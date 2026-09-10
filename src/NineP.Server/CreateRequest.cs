using NineP.Protocol;
using NineP.Protocol.Auth;

namespace NineP.Server;

/// <summary>
/// Everything <c>Tcreate</c>, <c>Tlcreate</c>, <c>Tmkdir</c>, <c>Tsymlink</c> and <c>Tmknod</c>
/// need, unified into one shape (reference §7). A handler writes one create, not five.
/// </summary>
public sealed record CreateRequest
{
    /// <summary>The name to create; validated against reference §8 rule 3 by the core.</summary>
    public required string Name { get; init; }

    /// <summary>What to create.</summary>
    public required FileKind Kind { get; init; }

    /// <summary>Permission bits after the core applied the parent mask and the 07777 mask.</summary>
    public required uint Perm { get; init; }

    /// <summary>The access mode the new file is opened with; Read for a directory.</summary>
    public OpenMode Mode { get; init; }

    /// <summary>Open flags accompanying the create.</summary>
    public OpenFlags Flags { get; init; }

    /// <summary>
    /// The file flags the new file is to carry: <see cref="FileFlags.Append"/>,
    /// <see cref="FileFlags.Exclusive"/> and <see cref="FileFlags.Temporary"/>, from the
    /// <c>DMAPPEND</c>, <c>DMEXCL</c> and <c>DMTMP</c> bits of <c>Tcreate.perm</c> (open(2);
    /// reference §8 rule 19). <see cref="FileFlags.None"/> for every other create message, which
    /// has no spelling for them. A handler that cannot give a file these flags refuses the create;
    /// the core reads the new file's attributes back and removes a file that lacks them, so a
    /// success reply is never sent for a plain file.
    /// </summary>
    public FileFlags FileFlags { get; init; }

    /// <summary>The symlink target when <see cref="Kind"/> is <see cref="FileKind.Symlink"/>.</summary>
    public string? Target { get; init; }

    /// <summary>The device numbers when the kind is a character or block device.</summary>
    public DeviceId? Rdev { get; init; }

    /// <summary>The group id a .L create supplied; NONUNAME when the dialect carries none.</summary>
    public uint Gid { get; init; } = Constants.NONUNAME;

    /// <summary>The identity the create runs as: the implicit user of the fid (reference §5.2).</summary>
    public required Identity Identity { get; init; }
}
