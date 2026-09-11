namespace NineP.Protocol;

/// <summary>
/// The permission bits of a file: the <c>0777</c> rwx triples plus setuid, setgid and sticky, the
/// <c>07777</c> mask reference §4.7 fixes. These are the low bits of a 9P2000 stat record's
/// <c>mode</c>, of <c>Tcreate.perm</c> and of a POSIX mode word alike; the non-permission mode
/// bits live in <see cref="FileFlags"/> and the file type in <see cref="FileKind"/>, so no one
/// type mixes the three vocabularies.
/// </summary>
/// <remarks>
/// The reference writes these in octal, which C# cannot spell as a literal — <c>0755</c> has to be
/// written <c>0x1ED</c> — so every value is named here instead. The numeric values are the POSIX
/// ones, so a cast to <see cref="uint"/> is the wire value and nothing else.
/// </remarks>
[Flags]
public enum FilePermissions : uint
{
    /// <summary>No permission at all (<c>0000</c>).</summary>
    None = 0,

    /// <summary>Anyone may execute the file, or walk into the directory (<c>0001</c>).</summary>
    OtherExecute = 0x001,

    /// <summary>Anyone may write the file (<c>0002</c>).</summary>
    OtherWrite = 0x002,

    /// <summary>Anyone may read the file, or read the directory (<c>0004</c>).</summary>
    OtherRead = 0x004,

    /// <summary>The group may execute the file, or walk into the directory (<c>0010</c>).</summary>
    GroupExecute = 0x008,

    /// <summary>The group may write the file (<c>0020</c>).</summary>
    GroupWrite = 0x010,

    /// <summary>The group may read the file, or read the directory (<c>0040</c>).</summary>
    GroupRead = 0x020,

    /// <summary>The owner may execute the file, or walk into the directory (<c>0100</c>).</summary>
    OwnerExecute = 0x040,

    /// <summary>The owner may write the file (<c>0200</c>).</summary>
    OwnerWrite = 0x080,

    /// <summary>The owner may read the file, or read the directory (<c>0400</c>).</summary>
    OwnerRead = 0x100,

    /// <summary>
    /// The sticky bit (<c>01000</c>): on a directory, only a file's owner may remove it. 9P2000
    /// has no spelling for it; the projection carries it in .u and .L only (reference §4.4).
    /// </summary>
    Sticky = 0x200,

    /// <summary>Set-group-id on execution (<c>02000</c>); .u and .L only.</summary>
    SetGid = 0x400,

    /// <summary>Set-user-id on execution (<c>04000</c>); .u and .L only.</summary>
    SetUid = 0x800,

    /// <summary>The owner may read and write (<c>0600</c>).</summary>
    OwnerReadWrite = OwnerRead | OwnerWrite,

    /// <summary>The owner may read and execute or walk (<c>0500</c>).</summary>
    OwnerReadExecute = OwnerRead | OwnerExecute,

    /// <summary>The owner may read, write and execute or walk (<c>0700</c>).</summary>
    OwnerAll = OwnerRead | OwnerWrite | OwnerExecute,

    /// <summary>The group may read and write (<c>0060</c>).</summary>
    GroupReadWrite = GroupRead | GroupWrite,

    /// <summary>The group may read and execute or walk (<c>0050</c>).</summary>
    GroupReadExecute = GroupRead | GroupExecute,

    /// <summary>The group may read, write and execute or walk (<c>0070</c>).</summary>
    GroupAll = GroupRead | GroupWrite | GroupExecute,

    /// <summary>Anyone may read and write (<c>0006</c>).</summary>
    OtherReadWrite = OtherRead | OtherWrite,

    /// <summary>Anyone may read and execute or walk (<c>0005</c>).</summary>
    OtherReadExecute = OtherRead | OtherExecute,

    /// <summary>Anyone may read, write and execute or walk (<c>0007</c>).</summary>
    OtherAll = OtherRead | OtherWrite | OtherExecute,

    /// <summary>Everyone may read (<c>0444</c>).</summary>
    AllRead = OwnerRead | GroupRead | OtherRead,

    /// <summary>Everyone may write (<c>0222</c>).</summary>
    AllWrite = OwnerWrite | GroupWrite | OtherWrite,

    /// <summary>Everyone may execute the file, or walk into the directory (<c>0111</c>).</summary>
    AllExecute = OwnerExecute | GroupExecute | OtherExecute,

    /// <summary>
    /// Every permission bit this type names: the <c>07777</c> mask. A value outside it is not a
    /// permission, and the projection masks it away rather than putting it on the wire.
    /// </summary>
    Mask = 0xFFF,
}
