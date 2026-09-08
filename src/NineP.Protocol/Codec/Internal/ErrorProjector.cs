using System.Buffers;
using NineP.Protocol.Messages;

namespace NineP.Protocol.Codec.Internal;

/// <summary>
/// One <see cref="NinePError"/> value, three wire shapes, chosen by the session dialect (§6.3):
/// <c>Rerror</c> with a truncated ename in 9P2000, the same plus <c>errno[4]</c> in .u, and
/// <c>Rlerror</c> with an errno alone in .L. A .L session never carries an <c>Rerror</c>.
/// </summary>
internal static class ErrorProjector
{
    /// <summary>The error type number a session of that dialect carries.</summary>
    /// <param name="dialect">The session dialect.</param>
    /// <returns><c>Rlerror</c> for .L, <c>Rerror</c> otherwise.</returns>
    public static MessageType ErrorTypeFor(Dialect dialect) =>
        dialect == Dialect.P9_2000_L ? MessageType.Rlerror : MessageType.Rerror;

    /// <summary>
    /// The error value a failure projects to. Anything that is not a <see cref="NinePException"/>
    /// is a bug on this side, and the peer is told "i/o error" — never the exception's message,
    /// which could carry a path or an untrusted string (reference §8 rule 10).
    /// </summary>
    /// <param name="exception">The failure that escaped a handler.</param>
    /// <returns>The error value to project.</returns>
    public static NinePError FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is NinePException nineP ? nineP.Error : NinePError.FromErrno(Errno.EIO);
    }

    /// <summary>
    /// The 9P2000 / .u error reply for a value; the errno rides along only in .u, and a 9P2000
    /// reply carries 0, which means none.
    /// </summary>
    /// <param name="tag">The tag of the request being answered.</param>
    /// <param name="error">The error value.</param>
    /// <param name="dialect">The session dialect.</param>
    /// <returns>The reply record, with the ename already truncated.</returns>
    public static Rerror ToRerror(ushort tag, NinePError error, Dialect dialect) =>
        new(tag, error.TruncatedEname, dialect == Dialect.P9_2000_u ? WireErrno(error.Errno) : 0);

    /// <summary>The .L error reply for a value.</summary>
    /// <param name="tag">The tag of the request being answered.</param>
    /// <param name="error">The error value.</param>
    /// <returns>The reply record.</returns>
    public static Rlerror ToRlerror(ushort tag, NinePError error) => new(tag, WireErrno(error.Errno));

    /// <summary>Writes the error reply the session dialect calls for into a buffer.</summary>
    /// <param name="writer">The destination; exactly one frame is written and advanced over.</param>
    /// <param name="tag">The tag of the request being answered.</param>
    /// <param name="error">The error value.</param>
    /// <param name="dialect">The session dialect.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Write(IBufferWriter<byte> writer, ushort tag, NinePError error, Dialect dialect)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (dialect == Dialect.P9_2000_L)
        {
            Rlerror reply = ToRlerror(tag, error);
            return MessageCodec.Encode(writer, in reply, dialect);
        }

        Rerror legacy = ToRerror(tag, error, dialect);
        return MessageCodec.Encode(writer, in legacy, dialect);
    }

    /// <summary>
    /// The error value an <c>Rerror</c> carries; a .u reply's errno wins over the table, and a
    /// reply without one (0, which is every 9P2000 reply) maps its ename through the table.
    /// </summary>
    /// <param name="reply">The reply as it came off the wire.</param>
    /// <returns>The error value.</returns>
    public static NinePError FromRerror(in Rerror reply) =>
        new(reply.Ename, reply.Errno != 0 ? reply.Errno : ErrorTable.ErrnoFor(reply.Ename));

    /// <summary>The error value an <c>Rlerror</c> carries; the ename comes from the table.</summary>
    /// <param name="reply">The reply as it came off the wire.</param>
    /// <returns>The error value.</returns>
    public static NinePError FromRlerror(in Rlerror reply) => NinePError.FromErrno(reply.Ecode);

    // A negative errno is not an errno, and the encoder refuses to put one on the wire. It can
    // only come from a handler that built a NinePError by hand, which is a bug on this side, and
    // a bug on this side is told to the peer as "i/o error" (the rule FromException applies) --
    // in the errno field alone, so a 9P2000 peer still gets the ename the handler chose.
    private static int WireErrno(int errno) => errno < 0 ? Errno.EIO : errno;
}
