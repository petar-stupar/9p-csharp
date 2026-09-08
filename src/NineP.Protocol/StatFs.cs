namespace NineP.Protocol;

/// <summary>Filesystem statistics answered by <c>Tstatfs</c> (reference §4.9).</summary>
/// <param name="Type">The filesystem magic; synthetic servers report <see cref="V9fsMagic"/>.</param>
/// <param name="BlockSize">The block size the other counts are expressed in.</param>
/// <param name="Blocks">Total blocks.</param>
/// <param name="BlocksFree">Free blocks.</param>
/// <param name="BlocksAvailable">Blocks available to an unprivileged user.</param>
/// <param name="Files">Total inodes.</param>
/// <param name="FilesFree">Free inodes.</param>
/// <param name="FsId">An opaque filesystem identifier.</param>
/// <param name="NameLength">The longest component name the filesystem accepts.</param>
public readonly record struct StatFs(
    uint Type,
    uint BlockSize,
    ulong Blocks,
    ulong BlocksFree,
    ulong BlocksAvailable,
    ulong Files,
    ulong FilesFree,
    ulong FsId,
    uint NameLength)
{
    /// <summary>V9FS_MAGIC (0x01021997), the type a synthetic server reports.</summary>
    public const uint V9fsMagic = 0x01021997;
}
