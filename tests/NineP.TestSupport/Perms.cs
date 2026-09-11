using NineP.Protocol;

namespace NineP.TestSupport;

/// <summary>
/// The permission values the suites use, spelled as the reference spells them: in octal. C# has no
/// octal literal, so <c>0755</c> would otherwise have to be written <c>0x1ED</c> at every call
/// site. These are for the *intent* positions — building an <see cref="Attr"/>, asking for a
/// create. A test asserting what goes on the wire still writes the wire value.
/// </summary>
public static class Perms
{
    /// <summary>No permission at all.</summary>
    public const FilePermissions P0000 = FilePermissions.None;

    /// <summary>Owner read only.</summary>
    public const FilePermissions P0400 = FilePermissions.OwnerRead;

    /// <summary>Owner read and write.</summary>
    public const FilePermissions P0600 = FilePermissions.OwnerReadWrite;

    /// <summary>Owner everything.</summary>
    public const FilePermissions P0700 = FilePermissions.OwnerAll;

    /// <summary>Everyone reads; a read-only file.</summary>
    public const FilePermissions P0444 = FilePermissions.AllRead;

    /// <summary>Owner reads and writes, everyone else reads; the usual file.</summary>
    public const FilePermissions P0644 =
        FilePermissions.OwnerReadWrite | FilePermissions.GroupRead | FilePermissions.OtherRead;

    /// <summary>Everyone reads and writes.</summary>
    public const FilePermissions P0666 = FilePermissions.AllRead | FilePermissions.AllWrite;

    /// <summary>Owner everything, everyone else reads and walks; the usual directory.</summary>
    public const FilePermissions P0755 =
        FilePermissions.OwnerAll | FilePermissions.GroupReadExecute | FilePermissions.OtherReadExecute;

    /// <summary>Everyone everything.</summary>
    public const FilePermissions P0777 =
        FilePermissions.OwnerAll | FilePermissions.GroupAll | FilePermissions.OtherAll;

    /// <summary>0755 with the sticky bit.</summary>
    public const FilePermissions P1755 = P0755 | FilePermissions.Sticky;

    /// <summary>0755 with setgid.</summary>
    public const FilePermissions P2755 = P0755 | FilePermissions.SetGid;

    /// <summary>0755 with setuid.</summary>
    public const FilePermissions P4755 = P0755 | FilePermissions.SetUid;

    /// <summary>0755 with setuid and setgid.</summary>
    public const FilePermissions P6755 = P0755 | FilePermissions.SetUid | FilePermissions.SetGid;

    /// <summary>Everyone reads and walks; nobody writes.</summary>
    public const FilePermissions P0555 =
        FilePermissions.OwnerReadExecute | FilePermissions.GroupReadExecute | FilePermissions.OtherReadExecute;

    /// <summary>0666 with setuid.</summary>
    public const FilePermissions P4666 = P0666 | FilePermissions.SetUid;

    /// <summary>0755 with setuid, setgid and sticky: every bit of the 07777 mask that is set in 0755.</summary>
    public const FilePermissions P7755 = P0755 | FilePermissions.SetUid | FilePermissions.SetGid | FilePermissions.Sticky;
}
