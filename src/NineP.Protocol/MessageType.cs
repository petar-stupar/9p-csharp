namespace NineP.Protocol;

/// <summary>
/// Every 9P message type number of reference §2: 34 T/R pairs, 68 members, R == T + 1.
/// <c>Terror</c> (106) and <c>Tlerror</c> (6) are illegal on the wire and carry no record.
/// </summary>
#pragma warning disable CA1008 // the type numbers are the protocol's; zero is not one of them
public enum MessageType : byte
{
    /// <summary>An error reply carrying only a Linux errno; the T form is illegal on the wire (.L).</summary>
    Tlerror = 6,

    /// <summary>The reply to <see cref="Tlerror"/> (.L).</summary>
    Rlerror = 7,

    /// <summary>Ask for statistics about the filesystem behind a fid (.L).</summary>
    Tstatfs = 8,

    /// <summary>The reply to <see cref="Tstatfs"/> (.L).</summary>
    Rstatfs = 9,

    /// <summary>Open an existing file with Linux open(2) flags (.L).</summary>
    Tlopen = 12,

    /// <summary>The reply to <see cref="Tlopen"/> (.L).</summary>
    Rlopen = 13,

    /// <summary>Create a file and open the fid onto it (.L).</summary>
    Tlcreate = 14,

    /// <summary>The reply to <see cref="Tlcreate"/> (.L).</summary>
    Rlcreate = 15,

    /// <summary>Create a symbolic link (.L).</summary>
    Tsymlink = 16,

    /// <summary>The reply to <see cref="Tsymlink"/> (.L).</summary>
    Rsymlink = 17,

    /// <summary>Create a device, socket or named pipe (.L).</summary>
    Tmknod = 18,

    /// <summary>The reply to <see cref="Tmknod"/> (.L).</summary>
    Rmknod = 19,

    /// <summary>Rename a file into another directory (.L).</summary>
    Trename = 20,

    /// <summary>The reply to <see cref="Trename"/> (.L).</summary>
    Rrename = 21,

    /// <summary>Read the target of a symbolic link (.L).</summary>
    Treadlink = 22,

    /// <summary>The reply to <see cref="Treadlink"/> (.L).</summary>
    Rreadlink = 23,

    /// <summary>Read the attributes of a file (.L).</summary>
    Tgetattr = 24,

    /// <summary>The reply to <see cref="Tgetattr"/> (.L).</summary>
    Rgetattr = 25,

    /// <summary>Change the attributes of a file (.L).</summary>
    Tsetattr = 26,

    /// <summary>The reply to <see cref="Tsetattr"/> (.L).</summary>
    Rsetattr = 27,

    /// <summary>Walk a fid onto an extended attribute (.L).</summary>
    Txattrwalk = 30,

    /// <summary>The reply to <see cref="Txattrwalk"/> (.L).</summary>
    Rxattrwalk = 31,

    /// <summary>Prepare a fid to write an extended attribute (.L).</summary>
    Txattrcreate = 32,

    /// <summary>The reply to <see cref="Txattrcreate"/> (.L).</summary>
    Rxattrcreate = 33,

    /// <summary>Read packed directory entries (.L).</summary>
    Treaddir = 40,

    /// <summary>The reply to <see cref="Treaddir"/> (.L).</summary>
    Rreaddir = 41,

    /// <summary>Commit a file to stable storage (.L).</summary>
    Tfsync = 50,

    /// <summary>The reply to <see cref="Tfsync"/> (.L).</summary>
    Rfsync = 51,

    /// <summary>Acquire or release a byte-range lock (.L).</summary>
    Tlock = 52,

    /// <summary>The reply to <see cref="Tlock"/> (.L).</summary>
    Rlock = 53,

    /// <summary>Query a byte-range lock (.L).</summary>
    Tgetlock = 54,

    /// <summary>The reply to <see cref="Tgetlock"/> (.L).</summary>
    Rgetlock = 55,

    /// <summary>Create a hard link (.L).</summary>
    Tlink = 70,

    /// <summary>The reply to <see cref="Tlink"/> (.L).</summary>
    Rlink = 71,

    /// <summary>Create a directory (.L).</summary>
    Tmkdir = 72,

    /// <summary>The reply to <see cref="Tmkdir"/> (.L).</summary>
    Rmkdir = 73,

    /// <summary>Rename by directory fid and name (.L).</summary>
    Trenameat = 74,

    /// <summary>The reply to <see cref="Trenameat"/> (.L).</summary>
    Rrenameat = 75,

    /// <summary>Remove by directory fid and name (.L).</summary>
    Tunlinkat = 76,

    /// <summary>The reply to <see cref="Tunlinkat"/> (.L).</summary>
    Runlinkat = 77,

    /// <summary>Negotiate the dialect and the message size (all).</summary>
    Tversion = 100,

    /// <summary>The reply to <see cref="Tversion"/> (all).</summary>
    Rversion = 101,

    /// <summary>Open an afid for the authentication exchange (all).</summary>
    Tauth = 102,

    /// <summary>The reply to <see cref="Tauth"/> (all).</summary>
    Rauth = 103,

    /// <summary>Introduce a fid to the root of a tree (all).</summary>
    Tattach = 104,

    /// <summary>The reply to <see cref="Tattach"/> (all).</summary>
    Rattach = 105,

    /// <summary>An error reply carrying an ename; the T form is illegal on the wire (9P2000 and .u).</summary>
    Terror = 106,

    /// <summary>The reply to <see cref="Terror"/> (9P2000 and .u).</summary>
    Rerror = 107,

    /// <summary>Abandon an outstanding request (all).</summary>
    Tflush = 108,

    /// <summary>The reply to <see cref="Tflush"/> (all).</summary>
    Rflush = 109,

    /// <summary>Move a fid through the tree, element by element (all).</summary>
    Twalk = 110,

    /// <summary>The reply to <see cref="Twalk"/> (all).</summary>
    Rwalk = 111,

    /// <summary>Open an existing file (9P2000 and .u).</summary>
    Topen = 112,

    /// <summary>The reply to <see cref="Topen"/> (9P2000 and .u).</summary>
    Ropen = 113,

    /// <summary>Create a file and open the fid onto it (9P2000 and .u).</summary>
    Tcreate = 114,

    /// <summary>The reply to <see cref="Tcreate"/> (9P2000 and .u).</summary>
    Rcreate = 115,

    /// <summary>Read bytes from an open fid (all).</summary>
    Tread = 116,

    /// <summary>The reply to <see cref="Tread"/> (all).</summary>
    Rread = 117,

    /// <summary>Write bytes to an open fid (all).</summary>
    Twrite = 118,

    /// <summary>The reply to <see cref="Twrite"/> (all).</summary>
    Rwrite = 119,

    /// <summary>Forget a fid (all).</summary>
    Tclunk = 120,

    /// <summary>The reply to <see cref="Tclunk"/> (all).</summary>
    Rclunk = 121,

    /// <summary>Remove the file a fid names and forget the fid (all).</summary>
    Tremove = 122,

    /// <summary>The reply to <see cref="Tremove"/> (all).</summary>
    Rremove = 123,

    /// <summary>Read a stat record (9P2000 and .u).</summary>
    Tstat = 124,

    /// <summary>The reply to <see cref="Tstat"/> (9P2000 and .u).</summary>
    Rstat = 125,

    /// <summary>Change a file through a stat record (9P2000 and .u).</summary>
    Twstat = 126,

    /// <summary>The reply to <see cref="Twstat"/> (9P2000 and .u).</summary>
    Rwstat = 127,
}
#pragma warning restore CA1008
