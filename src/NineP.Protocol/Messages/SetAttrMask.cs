namespace NineP.Protocol.Messages;

/// <summary>
/// The <c>Tsetattr.valid</c> bits (reference §4.6). A time bit without its <c>_SET</c> twin means
/// "use the server's current time"; with it, use the value the client supplied.
/// </summary>
[Flags]
public enum SetAttrMask : uint
{
    /// <summary>Nothing is being changed.</summary>
    None = 0,

    /// <summary>Change the permission bits.</summary>
    Mode = 0x1,

    /// <summary>Change the numeric owner.</summary>
    Uid = 0x2,

    /// <summary>Change the numeric group.</summary>
    Gid = 0x4,

    /// <summary>Change the length: truncate or extend.</summary>
    Size = 0x8,

    /// <summary>Change the access time.</summary>
    ATime = 0x10,

    /// <summary>Change the modification time.</summary>
    MTime = 0x20,

    /// <summary>Change the status-change time to the server's clock.</summary>
    CTime = 0x40,

    /// <summary>The access time in the message is the one to use.</summary>
    ATimeSet = 0x80,

    /// <summary>The modification time in the message is the one to use.</summary>
    MTimeSet = 0x100,
}
