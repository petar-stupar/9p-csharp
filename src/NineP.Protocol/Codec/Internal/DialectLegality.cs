namespace NineP.Protocol.Codec.Internal;

/// <summary>
/// Which type numbers a session of each dialect may carry (reference §2). It is a bitmap so that
/// the legality check on the hot path is one shift and one mask, taken before the frame is decoded
/// and before anything is allocated.
/// </summary>
/// <remarks>
/// <c>Terror</c> (106) and <c>Tlerror</c> (6) are in no dialect's bitmap: the reference calls both
/// illegal on the wire, and neither has a record.
/// </remarks>
internal static class DialectLegality
{
    private static readonly MessageType[] SharedTypes =
    [
        MessageType.Tversion, MessageType.Rversion,
        MessageType.Tauth, MessageType.Rauth,
        MessageType.Tattach, MessageType.Rattach,
        MessageType.Tflush, MessageType.Rflush,
        MessageType.Twalk, MessageType.Rwalk,
        MessageType.Tread, MessageType.Rread,
        MessageType.Twrite, MessageType.Rwrite,
        MessageType.Tclunk, MessageType.Rclunk,
        MessageType.Tremove, MessageType.Rremove,
    ];

    private static readonly MessageType[] LegacyTypes =
    [
        MessageType.Rerror,
        MessageType.Topen, MessageType.Ropen,
        MessageType.Tcreate, MessageType.Rcreate,
        MessageType.Tstat, MessageType.Rstat,
        MessageType.Twstat, MessageType.Rwstat,
    ];

    private static readonly MessageType[] DotLTypes =
    [
        MessageType.Rlerror,
        MessageType.Tstatfs, MessageType.Rstatfs,
        MessageType.Tlopen, MessageType.Rlopen,
        MessageType.Tlcreate, MessageType.Rlcreate,
        MessageType.Tsymlink, MessageType.Rsymlink,
        MessageType.Tmknod, MessageType.Rmknod,
        MessageType.Trename, MessageType.Rrename,
        MessageType.Treadlink, MessageType.Rreadlink,
        MessageType.Tgetattr, MessageType.Rgetattr,
        MessageType.Tsetattr, MessageType.Rsetattr,
        MessageType.Txattrwalk, MessageType.Rxattrwalk,
        MessageType.Txattrcreate, MessageType.Rxattrcreate,
        MessageType.Treaddir, MessageType.Rreaddir,
        MessageType.Tfsync, MessageType.Rfsync,
        MessageType.Tlock, MessageType.Rlock,
        MessageType.Tgetlock, MessageType.Rgetlock,
        MessageType.Tlink, MessageType.Rlink,
        MessageType.Tmkdir, MessageType.Rmkdir,
        MessageType.Trenameat, MessageType.Rrenameat,
        MessageType.Tunlinkat, MessageType.Runlinkat,
    ];

    private static readonly ulong[] Base9P2000 = Bitmap(SharedTypes, LegacyTypes);
    private static readonly ulong[] Unix9P2000 = Bitmap(SharedTypes, LegacyTypes);
    private static readonly ulong[] Linux9P2000 = Bitmap(SharedTypes, DotLTypes);

    /// <summary>Every type number that is legal on the wire in at least one dialect.</summary>
    public static IReadOnlyList<MessageType> WireLegalTypes { get; } =
        [.. SharedTypes, .. LegacyTypes, .. DotLTypes];

    /// <summary>True when a session of that dialect may carry that type number.</summary>
    /// <param name="type">The type number peeked out of the frame.</param>
    /// <param name="dialect">The dialect the session negotiated.</param>
    /// <returns>False for an unknown number and for one the dialect does not carry.</returns>
    public static bool IsLegal(MessageType type, Dialect dialect)
    {
        ulong[] bitmap = dialect switch
        {
            Dialect.P9_2000 => Base9P2000,
            Dialect.P9_2000_u => Unix9P2000,
            Dialect.P9_2000_L => Linux9P2000,
            _ => [],
        };

        byte number = (byte)type;
        return bitmap.Length != 0 && (bitmap[number >> 6] & (1ul << (number & 63))) != 0;
    }

    private static ulong[] Bitmap(params MessageType[][] sets)
    {
        // A type number is a byte, so the bitmap spans the whole 0..255 range: a peer may send
        // any of them, and every number outside the table has to answer "not legal" rather than
        // run off the end of the array.
        ulong[] bitmap = new ulong[4];
        foreach (MessageType[] set in sets)
        {
            foreach (MessageType type in set)
            {
                byte number = (byte)type;
                bitmap[number >> 6] |= 1ul << (number & 63);
            }
        }

        return bitmap;
    }
}
