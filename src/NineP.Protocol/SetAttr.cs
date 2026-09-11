namespace NineP.Protocol;

/// <summary>
/// A partial attribute update; a null member means "do not touch" (reference §7). One type carries
/// both a <c>Twstat</c> and a <c>Tsetattr</c>, so a handler never learns which dialect asked.
/// </summary>
public sealed record SetAttr
{
    /// <summary>A new name (<c>Twstat</c> only; a rename within the same directory).</summary>
    public string? Name { get; init; }

    /// <summary>New permission bits (the 07777 mask).</summary>
    public FilePermissions? Perm { get; init; }

    /// <summary>
    /// The file flags the file is to have after the update, all three stated together:
    /// <see cref="FileFlags.Append"/>, <see cref="FileFlags.Exclusive"/> and
    /// <see cref="FileFlags.Temporary"/> are the settable ones (stat(5): "the other defined
    /// permission and mode bits can" change; reference §5.8 and §8 rule 19). <c>Twstat</c> only:
    /// <c>Tsetattr</c> has no spelling for them. The core hands a handler a value only when the
    /// request would change the flags the file has; an echoed-back set is "do not touch".
    /// </summary>
    public FileFlags? Flags { get; init; }

    /// <summary>A new numeric owner (<c>Tsetattr</c> only; a <c>Twstat</c> may never change it).</summary>
    public uint? Uid { get; init; }

    /// <summary>A new numeric group.</summary>
    public uint? Gid { get; init; }

    /// <summary>A new textual group (<c>Twstat</c>).</summary>
    public string? GroupName { get; init; }

    /// <summary>A new length: truncation or extension.</summary>
    public ulong? Size { get; init; }

    /// <summary>
    /// A new access time; null with <see cref="ATimeToNow"/> true means "use the server's clock".
    /// </summary>
    public TimeSpec? ATime { get; init; }

    /// <summary>
    /// A new modification time; null with <see cref="MTimeToNow"/> true means "use the server's
    /// clock".
    /// </summary>
    public TimeSpec? MTime { get; init; }

    /// <summary>True when the client asked for atime without _SET: use the server's clock (reference §4.6).</summary>
    public bool ATimeToNow { get; init; }

    /// <summary>True when the client asked for mtime without _SET: use the server's clock.</summary>
    public bool MTimeToNow { get; init; }

    /// <summary>True when the client asked for ctime: use the server's clock.</summary>
    public bool CTimeToNow { get; init; }

    /// <summary>
    /// True when every field is "don't touch": a <c>Twstat</c> that means commit this file to
    /// stable storage rather than change it (reference §4.2), never a no-op.
    /// </summary>
    public bool IsFsyncRequest =>
        Name is null && Perm is null && Flags is null && Uid is null && Gid is null
        && GroupName is null && Size is null && ATime is null && MTime is null
        && !ATimeToNow && !MTimeToNow && !CTimeToNow;
}
