namespace NineP.Protocol;

/// <summary>
/// Qid type bits (reference §4.1). The two low bits follow Linux, not the 9P2000.u draft:
/// every .u and .L peer this workspace interoperates with is Linux-derived.
/// </summary>
[Flags]
#pragma warning disable CA1008 // QTFILE is the reference's name for the zero value; None is not
public enum QidType : byte
{
    /// <summary>A plain file.</summary>
    QTFILE = 0x00,

    /// <summary>A hard link (.u, Linux).</summary>
    QTLINK = 0x01,

    /// <summary>A symbolic link (.u, Linux).</summary>
    QTSYMLINK = 0x02,

    /// <summary>Not backed up.</summary>
    QTTMP = 0x04,

    /// <summary>An authentication file, the file an afid names.</summary>
    QTAUTH = 0x08,

    /// <summary>A mounted channel.</summary>
    QTMOUNT = 0x10,

    /// <summary>Exclusive use: one open fid at a time.</summary>
    QTEXCL = 0x20,

    /// <summary>Append only: writes ignore the offset.</summary>
    QTAPPEND = 0x40,

    /// <summary>A directory.</summary>
    QTDIR = 0x80,
}
