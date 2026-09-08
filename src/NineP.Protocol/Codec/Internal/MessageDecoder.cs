using System.Runtime.CompilerServices;
using NineP.Protocol.Internal;
using NineP.Protocol.Messages;

namespace NineP.Protocol.Codec.Internal;

/// <summary>
/// Turns one complete frame into its typed record. Decoding is synchronous and allocates nothing
/// in proportion to a length the frame merely claims: every counted field is checked against the
/// bytes that are actually there before it is used.
/// </summary>
internal static class MessageDecoder
{
    /// <summary>Decodes one frame, reporting the failure kind instead of throwing.</summary>
    /// <typeparam name="TMessage">The record the caller expects.</typeparam>
    /// <param name="frame">One complete frame, header included.</param>
    /// <param name="dialect">The dialect the session negotiated.</param>
    /// <param name="message">The decoded record, or the default on failure.</param>
    /// <param name="failure">What was wrong when the result is false.</param>
    /// <returns>True when the frame decoded cleanly.</returns>
    public static bool TryDecode<TMessage>(
        ReadOnlyMemory<byte> frame, Dialect dialect, out TMessage message, out ProtocolErrorKind failure)
        where TMessage : struct, IMessage
    {
        message = default;

        if (frame.Length < Constants.HDRSZ)
        {
            failure = ProtocolErrorKind.Size;
            return false;
        }

        WireReader reader = new(frame);
        if (reader.ReadUInt32() != (uint)frame.Length)
        {
            failure = ProtocolErrorKind.Size;
            return false;
        }

        MessageType type = (MessageType)reader.ReadUInt8();
        if (type != TMessage.Type || !DialectLegality.IsLegal(type, dialect))
        {
            failure = ProtocolErrorKind.Type;
            return false;
        }

        ushort tag = reader.ReadUInt16();
        if (!TryDecodeBody(ref reader, dialect, tag, out message))
        {
            failure = reader.Failed ? reader.Failure : ProtocolErrorKind.Type;
            message = default;
            return false;
        }

        reader.EnsureAtEnd();
        if (reader.Failed)
        {
            failure = reader.Failure;
            message = default;
            return false;
        }

        failure = default;
        return true;
    }

    private static bool TryDecodeBody<TMessage>(
        ref WireReader reader, Dialect dialect, ushort tag, out TMessage message)
        where TMessage : struct, IMessage
    {
        message = default;

        return TrySession(ref reader, dialect, tag, ref message)
            || TryNavigation(ref reader, tag, ref message)
            || TryLegacy(ref reader, dialect, tag, ref message)
            || TryDotLOpen(ref reader, tag, ref message)
            || TryDotLPath(ref reader, tag, ref message)
            || TryDotLAttr(ref reader, tag, ref message)
            || TryDotLIo(ref reader, tag, ref message);
    }

    private static bool TrySession<TMessage>(
        ref WireReader reader, Dialect dialect, ushort tag, ref TMessage message)
        where TMessage : struct, IMessage
    {
        if (typeof(TMessage) == typeof(Tversion))
        {
            return Emit(new Tversion(tag, reader.ReadUInt32(), reader.ReadString()), ref message);
        }

        if (typeof(TMessage) == typeof(Rversion))
        {
            return Emit(new Rversion(tag, reader.ReadUInt32(), reader.ReadString()), ref message);
        }

        if (typeof(TMessage) == typeof(Tauth))
        {
            return Emit(DecodeTauth(ref reader, dialect, tag), ref message);
        }

        if (typeof(TMessage) == typeof(Rauth))
        {
            return Emit(new Rauth(tag, reader.ReadQid()), ref message);
        }

        if (typeof(TMessage) == typeof(Tattach))
        {
            return Emit(DecodeTattach(ref reader, dialect, tag), ref message);
        }

        if (typeof(TMessage) == typeof(Rattach))
        {
            return Emit(new Rattach(tag, reader.ReadQid()), ref message);
        }

        if (typeof(TMessage) == typeof(Rerror))
        {
            return Emit(DecodeRerror(ref reader, dialect, tag), ref message);
        }

        if (typeof(TMessage) == typeof(Rlerror))
        {
            return Emit(new Rlerror(tag, ReadErrno(ref reader)), ref message);
        }

        if (typeof(TMessage) == typeof(Tflush))
        {
            return Emit(new Tflush(tag, reader.ReadUInt16()), ref message);
        }

        return typeof(TMessage) == typeof(Rflush) && Emit(new Rflush(tag), ref message);
    }

    private static bool TryNavigation<TMessage>(ref WireReader reader, ushort tag, ref TMessage message)
        where TMessage : struct, IMessage
    {
        if (typeof(TMessage) == typeof(Twalk))
        {
            return Emit(DecodeTwalk(ref reader, tag), ref message);
        }

        if (typeof(TMessage) == typeof(Rwalk))
        {
            return Emit(DecodeRwalk(ref reader, tag), ref message);
        }

        if (typeof(TMessage) == typeof(Tread))
        {
            return Emit(DecodeTread(ref reader, tag), ref message);
        }

        if (typeof(TMessage) == typeof(Rread))
        {
            return Emit(DecodeRread(ref reader, tag), ref message);
        }

        if (typeof(TMessage) == typeof(Twrite))
        {
            return Emit(DecodeTwrite(ref reader, tag), ref message);
        }

        if (typeof(TMessage) == typeof(Rwrite))
        {
            return Emit(new Rwrite(tag, reader.ReadUInt32()), ref message);
        }

        if (typeof(TMessage) == typeof(Tclunk))
        {
            return Emit(new Tclunk(tag, reader.ReadUInt32()), ref message);
        }

        if (typeof(TMessage) == typeof(Rclunk))
        {
            return Emit(new Rclunk(tag), ref message);
        }

        if (typeof(TMessage) == typeof(Tremove))
        {
            return Emit(new Tremove(tag, reader.ReadUInt32()), ref message);
        }

        return typeof(TMessage) == typeof(Rremove) && Emit(new Rremove(tag), ref message);
    }

    private static bool TryLegacy<TMessage>(
        ref WireReader reader, Dialect dialect, ushort tag, ref TMessage message)
        where TMessage : struct, IMessage
    {
        if (typeof(TMessage) == typeof(Topen))
        {
            return Emit(new Topen(tag, reader.ReadUInt32(), reader.ReadUInt8()), ref message);
        }

        if (typeof(TMessage) == typeof(Ropen))
        {
            return Emit(new Ropen(tag, reader.ReadQid(), reader.ReadUInt32()), ref message);
        }

        if (typeof(TMessage) == typeof(Tcreate))
        {
            return Emit(DecodeTcreate(ref reader, dialect, tag), ref message);
        }

        if (typeof(TMessage) == typeof(Rcreate))
        {
            return Emit(new Rcreate(tag, reader.ReadQid(), reader.ReadUInt32()), ref message);
        }

        if (typeof(TMessage) == typeof(Tstat))
        {
            return Emit(new Tstat(tag, reader.ReadUInt32()), ref message);
        }

        if (typeof(TMessage) == typeof(Rstat))
        {
            return Emit(new Rstat(tag, StatCodec.Read(ref reader, dialect)), ref message);
        }

        if (typeof(TMessage) == typeof(Twstat))
        {
            uint fid = reader.ReadUInt32();
            StatRecord stat = StatCodec.Read(ref reader, dialect);
            // Rstat may label the root "/"; Twstat instead carries a rename component,
            // with an empty string reserved for "leave the name unchanged".
            if (!reader.Failed && (NinePText.GetByteCount(stat.Name) > Constants.MaxNameLength
                || !NinePText.IsLegalName(stat.Name, allowParent: false, allowEmpty: true)))
            {
                reader.Fail(ProtocolErrorKind.Name);
            }
            return Emit(new Twstat(tag, fid, stat), ref message);
        }

        return typeof(TMessage) == typeof(Rwstat) && Emit(new Rwstat(tag), ref message);
    }

    private static Tauth DecodeTauth(ref WireReader reader, Dialect dialect, ushort tag)
    {
        uint afid = reader.ReadUInt32();
        string uname = reader.ReadString();
        string aname = reader.ReadString();
        return new Tauth(tag, afid, uname, aname, ReadNUname(ref reader, dialect));
    }

    private static Tattach DecodeTattach(ref WireReader reader, Dialect dialect, ushort tag)
    {
        uint fid = reader.ReadUInt32();
        uint afid = reader.ReadUInt32();
        string uname = reader.ReadString();
        string aname = reader.ReadString();
        return new Tattach(tag, fid, afid, uname, aname, ReadNUname(ref reader, dialect));
    }

    private static Rerror DecodeRerror(ref WireReader reader, Dialect dialect, ushort tag)
    {
        string ename = reader.ReadString();

        // errno[4] belongs to .u alone: 9P2000 has no such field and .L carries Rlerror instead,
        // so the presence of the field is decided by the session, never by what is left over.
        return new Rerror(tag, ename, dialect == Dialect.P9_2000_u ? ReadErrno(ref reader) : 0);
    }

    // errno[4] and ecode[4] are unsigned on the wire and signed in the API (workspace architecture
    // §12 rule 2). A value that does not fit the signed type is not an errno any peer defines, and
    // reading it as a negative number would produce an error value this side could never encode.
    private static int ReadErrno(ref WireReader reader)
    {
        uint raw = reader.ReadUInt32();
        if (raw > int.MaxValue)
        {
            reader.Fail(ProtocolErrorKind.Overflow);
            return 0;
        }

        return (int)raw;
    }

    private static Tcreate DecodeTcreate(ref WireReader reader, Dialect dialect, ushort tag)
    {
        uint fid = reader.ReadUInt32();
        string name = reader.ReadName(allowParent: false);
        uint perm = reader.ReadUInt32();
        byte mode = reader.ReadUInt8();
        string? extension = dialect == Dialect.P9_2000_u ? reader.ReadString() : null;
        return new Tcreate(tag, fid, name, perm, mode, extension);
    }

    // n_uname[4] is on the wire in .u and in .L (reference §3.1: the .u draft's §7.3 defines it and
    // §2.2's synopsis omits it; §7.3 wins, and diod carries the field in .L too).
    private static uint ReadNUname(ref WireReader reader, Dialect dialect) =>
        dialect == Dialect.P9_2000 ? Constants.NONUNAME : reader.ReadUInt32();

    private static Twalk DecodeTwalk(ref WireReader reader, ushort tag)
    {
        uint fid = reader.ReadUInt32();
        uint newFid = reader.ReadUInt32();
        ushort count = reader.ReadUInt16();
        if (reader.Failed)
        {
            return default;
        }

        // The count is checked against MAXWELEM before a single element is allocated, so a peer
        // that claims 65535 names costs nothing (reference §8 rule 2).
        if (count > Constants.MAXWELEM)
        {
            reader.Fail(ProtocolErrorKind.NWName);
            return default;
        }

        string[] names = new string[count];
        for (int i = 0; i < count; i++)
        {
            names[i] = reader.ReadName(allowParent: true);
        }

        return reader.Failed ? default : new Twalk(tag, fid, newFid, names);
    }

    private static Rwalk DecodeRwalk(ref WireReader reader, ushort tag)
    {
        ushort count = reader.ReadUInt16();
        if (reader.Failed)
        {
            return default;
        }

        if (count > Constants.MAXWELEM)
        {
            reader.Fail(ProtocolErrorKind.NWName);
            return default;
        }

        Qid[] qids = new Qid[count];
        for (int i = 0; i < count; i++)
        {
            qids[i] = reader.ReadQid();
        }

        return reader.Failed ? default : new Rwalk(tag, qids);
    }

    private static Tread DecodeTread(ref WireReader reader, ushort tag)
    {
        uint fid = reader.ReadUInt32();
        ulong offset = reader.ReadUInt64();
        uint count = reader.ReadUInt32();
        if (reader.Failed)
        {
            return default;
        }

        // No overflow guard here on purpose. Reference §8 rule 5 names Twrite, Tsetattr and Tlock,
        // and only those: a Tread past the end of a file is a read that returns nothing, not a
        // range a server writes into. Refusing it was a protocol error that closed the
        // connection, so a client probing near EOF with offset = 2^64-1 lost its session where
        // Plan 9 answers count = 0.
        return new Tread(tag, fid, offset, count);
    }

    private static Rread DecodeRread(ref WireReader reader, ushort tag)
    {
        uint count = reader.ReadUInt32();
        if (reader.Failed)
        {
            return default;
        }

        if (count > (uint)reader.Remaining)
        {
            reader.Fail(ProtocolErrorKind.Bounds);
            return default;
        }

        return new Rread(tag, reader.ReadBytes((int)count));
    }

    private static Twrite DecodeTwrite(ref WireReader reader, ushort tag)
    {
        uint fid = reader.ReadUInt32();
        ulong offset = reader.ReadUInt64();
        uint count = reader.ReadUInt32();
        if (reader.Failed)
        {
            return default;
        }

        // Reference §8 rule 4: count must equal size - 23, the real Twrite header. It is not
        // msize - IOHDRSZ: that is a service bound, and a maximal legal Twrite exceeds it. A
        // count that overruns the frame is Bounds; one that falls short leaves trailing bytes.
        if (count != (uint)reader.Remaining)
        {
            reader.Fail(count > (uint)reader.Remaining
                ? ProtocolErrorKind.Bounds
                : ProtocolErrorKind.Trailing);
            return default;
        }

        if (!FitsInU64(offset, count))
        {
            reader.Fail(ProtocolErrorKind.Overflow);
            return default;
        }

        return new Twrite(tag, fid, offset, reader.ReadBytes((int)count));
    }

    private static bool TryDotLOpen<TMessage>(ref WireReader reader, ushort tag, ref TMessage message)
        where TMessage : struct, IMessage
    {
        if (typeof(TMessage) == typeof(Tstatfs))
        {
            return Emit(new Tstatfs(tag, reader.ReadUInt32()), ref message);
        }

        if (typeof(TMessage) == typeof(Rstatfs))
        {
            return Emit(new Rstatfs(tag, DecodeStatFs(ref reader)), ref message);
        }

        if (typeof(TMessage) == typeof(Tlopen))
        {
            return Emit(new Tlopen(tag, reader.ReadUInt32(), reader.ReadUInt32()), ref message);
        }

        if (typeof(TMessage) == typeof(Rlopen))
        {
            return Emit(new Rlopen(tag, reader.ReadQid(), reader.ReadUInt32()), ref message);
        }

        if (typeof(TMessage) == typeof(Tlcreate))
        {
            return Emit(
                new Tlcreate(
                    tag,
                    reader.ReadUInt32(),
                    reader.ReadName(allowParent: false),
                    reader.ReadUInt32(),
                    reader.ReadUInt32(),
                    reader.ReadUInt32()),
                ref message);
        }

        if (typeof(TMessage) == typeof(Rlcreate))
        {
            return Emit(new Rlcreate(tag, reader.ReadQid(), reader.ReadUInt32()), ref message);
        }

        if (typeof(TMessage) == typeof(Tsymlink))
        {
            // symtgt is a path the server never interprets, so it is a plain string; only name is
            // a path element and subject to the name rules of reference §8 rule 3.
            return Emit(
                new Tsymlink(
                    tag,
                    reader.ReadUInt32(),
                    reader.ReadName(allowParent: false),
                    reader.ReadString(),
                    reader.ReadUInt32()),
                ref message);
        }

        if (typeof(TMessage) == typeof(Rsymlink))
        {
            return Emit(new Rsymlink(tag, reader.ReadQid()), ref message);
        }

        if (typeof(TMessage) == typeof(Tmknod))
        {
            return Emit(
                new Tmknod(
                    tag,
                    reader.ReadUInt32(),
                    reader.ReadName(allowParent: false),
                    reader.ReadUInt32(),
                    reader.ReadUInt32(),
                    reader.ReadUInt32(),
                    reader.ReadUInt32()),
                ref message);
        }

        return typeof(TMessage) == typeof(Rmknod) && Emit(new Rmknod(tag, reader.ReadQid()), ref message);
    }

    private static bool TryDotLPath<TMessage>(ref WireReader reader, ushort tag, ref TMessage message)
        where TMessage : struct, IMessage
    {
        if (typeof(TMessage) == typeof(Trename))
        {
            return Emit(
                new Trename(tag, reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadName(allowParent: false)),
                ref message);
        }

        if (typeof(TMessage) == typeof(Rrename))
        {
            return Emit(new Rrename(tag), ref message);
        }

        if (typeof(TMessage) == typeof(Treadlink))
        {
            return Emit(new Treadlink(tag, reader.ReadUInt32()), ref message);
        }

        if (typeof(TMessage) == typeof(Rreadlink))
        {
            return Emit(new Rreadlink(tag, reader.ReadString()), ref message);
        }

        if (typeof(TMessage) == typeof(Tlink))
        {
            return Emit(
                new Tlink(tag, reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadName(allowParent: false)),
                ref message);
        }

        if (typeof(TMessage) == typeof(Rlink))
        {
            return Emit(new Rlink(tag), ref message);
        }

        if (typeof(TMessage) == typeof(Tmkdir))
        {
            return Emit(
                new Tmkdir(
                    tag,
                    reader.ReadUInt32(),
                    reader.ReadName(allowParent: false),
                    reader.ReadUInt32(),
                    reader.ReadUInt32()),
                ref message);
        }

        if (typeof(TMessage) == typeof(Rmkdir))
        {
            return Emit(new Rmkdir(tag, reader.ReadQid()), ref message);
        }

        if (typeof(TMessage) == typeof(Trenameat))
        {
            return Emit(
                new Trenameat(
                    tag,
                    reader.ReadUInt32(),
                    reader.ReadName(allowParent: false),
                    reader.ReadUInt32(),
                    reader.ReadName(allowParent: false)),
                ref message);
        }

        if (typeof(TMessage) == typeof(Rrenameat))
        {
            return Emit(new Rrenameat(tag), ref message);
        }

        if (typeof(TMessage) == typeof(Tunlinkat))
        {
            return Emit(
                new Tunlinkat(tag, reader.ReadUInt32(), reader.ReadName(allowParent: false), reader.ReadUInt32()),
                ref message);
        }

        return typeof(TMessage) == typeof(Runlinkat) && Emit(new Runlinkat(tag), ref message);
    }

    private static bool TryDotLAttr<TMessage>(ref WireReader reader, ushort tag, ref TMessage message)
        where TMessage : struct, IMessage
    {
        if (typeof(TMessage) == typeof(Tgetattr))
        {
            return Emit(new Tgetattr(tag, reader.ReadUInt32(), (GetAttrMask)reader.ReadUInt64()), ref message);
        }

        if (typeof(TMessage) == typeof(Rgetattr))
        {
            return Emit(DecodeRgetattr(ref reader, tag), ref message);
        }

        if (typeof(TMessage) == typeof(Tsetattr))
        {
            return Emit(DecodeTsetattr(ref reader, tag), ref message);
        }

        if (typeof(TMessage) == typeof(Rsetattr))
        {
            return Emit(new Rsetattr(tag), ref message);
        }

        if (typeof(TMessage) == typeof(Txattrwalk))
        {
            // An empty name asks for the list of attribute names, which is why the name rules
            // accept it (reference §5.9).
            return Emit(
                // §3.4: an empty name asks for the list of attribute names, so it is the one
                // message where a nameless name means something.
                new Txattrwalk(
                    tag, reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadName(allowParent: false, allowEmpty: true)),
                ref message);
        }

        if (typeof(TMessage) == typeof(Rxattrwalk))
        {
            return Emit(new Rxattrwalk(tag, reader.ReadUInt64()), ref message);
        }

        if (typeof(TMessage) == typeof(Txattrcreate))
        {
            return Emit(
                new Txattrcreate(
                    tag,
                    reader.ReadUInt32(),
                    reader.ReadName(allowParent: false),
                    reader.ReadUInt64(),
                    (XattrFlags)reader.ReadUInt32()),
                ref message);
        }

        return typeof(TMessage) == typeof(Rxattrcreate) && Emit(new Rxattrcreate(tag), ref message);
    }

    private static bool TryDotLIo<TMessage>(ref WireReader reader, ushort tag, ref TMessage message)
        where TMessage : struct, IMessage
    {
        if (typeof(TMessage) == typeof(Treaddir))
        {
            return Emit(
                new Treaddir(tag, reader.ReadUInt32(), reader.ReadUInt64(), reader.ReadUInt32()),
                ref message);
        }

        if (typeof(TMessage) == typeof(Rreaddir))
        {
            return Emit(DecodeRreaddir(ref reader, tag), ref message);
        }

        if (typeof(TMessage) == typeof(Tfsync))
        {
            return Emit(DecodeTfsync(ref reader, tag), ref message);
        }

        if (typeof(TMessage) == typeof(Rfsync))
        {
            return Emit(new Rfsync(tag), ref message);
        }

        if (typeof(TMessage) == typeof(Tlock))
        {
            return Emit(DecodeTlock(ref reader, tag), ref message);
        }

        if (typeof(TMessage) == typeof(Rlock))
        {
            return Emit(new Rlock(tag, (LockStatus)reader.ReadUInt8()), ref message);
        }

        if (typeof(TMessage) == typeof(Tgetlock))
        {
            return Emit(DecodeTgetlock(ref reader, tag), ref message);
        }

        return typeof(TMessage) == typeof(Rgetlock) && Emit(DecodeRgetlock(ref reader, tag), ref message);
    }

    // Reference §8 rule 2 names this as its one exception. hugelgupf/p9 sends an 11-byte Tfsync
    // that carries fid[4] and nothing else, while diod and v9fs send the 15-byte frame with
    // datasync[4]; both must decode, and the short form means datasync = 0 (S-19). The branch is
    // reachable only from here, so every other short frame stays a Bounds or Trailing error.
    private static Tfsync DecodeTfsync(ref WireReader reader, ushort tag)
    {
        uint fid = reader.ReadUInt32();
        uint datasync = reader.Failed || reader.Remaining != 0 ? reader.ReadUInt32() : 0u;
        return new Tfsync(tag, fid, datasync);
    }

    private static StatFs DecodeStatFs(ref WireReader reader) => new(
        reader.ReadUInt32(),
        reader.ReadUInt32(),
        reader.ReadUInt64(),
        reader.ReadUInt64(),
        reader.ReadUInt64(),
        reader.ReadUInt64(),
        reader.ReadUInt64(),
        reader.ReadUInt64(),
        reader.ReadUInt32());

    private static Rgetattr DecodeRgetattr(ref WireReader reader, ushort tag) => new(
        tag,
        (GetAttrMask)reader.ReadUInt64(),
        reader.ReadQid(),
        reader.ReadUInt32(),
        reader.ReadUInt32(),
        reader.ReadUInt32(),
        reader.ReadUInt64(),
        reader.ReadUInt64(),
        reader.ReadUInt64(),
        reader.ReadUInt64(),
        reader.ReadUInt64(),
        DecodeTimeSpec(ref reader),
        DecodeTimeSpec(ref reader),
        DecodeTimeSpec(ref reader),
        DecodeTimeSpec(ref reader),
        reader.ReadUInt64(),
        reader.ReadUInt64());

    private static Tsetattr DecodeTsetattr(ref WireReader reader, ushort tag) => new(
        tag,
        reader.ReadUInt32(),
        (SetAttrMask)reader.ReadUInt32(),
        reader.ReadUInt32(),
        reader.ReadUInt32(),
        reader.ReadUInt32(),
        reader.ReadUInt64(),
        DecodeTimeSpec(ref reader),
        DecodeTimeSpec(ref reader));

    private static TimeSpec DecodeTimeSpec(ref WireReader reader) =>
        new((long)reader.ReadUInt64(), (uint)reader.ReadUInt64());

    private static Rreaddir DecodeRreaddir(ref WireReader reader, ushort tag)
    {
        uint count = reader.ReadUInt32();
        if (reader.Failed)
        {
            return default;
        }

        if (count > (uint)reader.Remaining)
        {
            reader.Fail(ProtocolErrorKind.Bounds);
            return default;
        }

        return new Rreaddir(tag, reader.ReadBytes((int)count));
    }

    private static Tlock DecodeTlock(ref WireReader reader, ushort tag)
    {
        uint fid = reader.ReadUInt32();
        LockType type = (LockType)reader.ReadUInt8();
        LockFlags flags = (LockFlags)reader.ReadUInt32();
        ulong start = reader.ReadUInt64();
        ulong length = reader.ReadUInt64();
        uint procId = reader.ReadUInt32();
        string clientId = reader.ReadString();

        return RangeFits(ref reader, start, length)
            ? new Tlock(tag, fid, new LockRequest(type, flags, start, length, procId, clientId))
            : default;
    }

    private static Tgetlock DecodeTgetlock(ref WireReader reader, ushort tag)
    {
        uint fid = reader.ReadUInt32();
        LockType type = (LockType)reader.ReadUInt8();
        ulong start = reader.ReadUInt64();
        ulong length = reader.ReadUInt64();
        uint procId = reader.ReadUInt32();
        string clientId = reader.ReadString();

        return RangeFits(ref reader, start, length)
            ? new Tgetlock(tag, fid, type, start, length, procId, clientId)
            : default;
    }

    private static Rgetlock DecodeRgetlock(ref WireReader reader, ushort tag)
    {
        LockType type = (LockType)reader.ReadUInt8();
        ulong start = reader.ReadUInt64();
        ulong length = reader.ReadUInt64();
        uint procId = reader.ReadUInt32();
        string clientId = reader.ReadString();

        return RangeFits(ref reader, start, length)
            ? new Rgetlock(tag, new LockQueryResult(type, start, length, procId, clientId))
            : default;
    }

    // Reference §8 rule 5: a lock's start plus its length must not wrap, or the range the server
    // computes is nowhere near the one the client asked for.
    private static bool RangeFits(ref WireReader reader, ulong start, ulong length)
    {
        if (reader.Failed)
        {
            return false;
        }

        if (start > ulong.MaxValue - length)
        {
            reader.Fail(ProtocolErrorKind.Overflow);
            return false;
        }

        return true;
    }

    private static bool FitsInU64(ulong offset, uint count) => offset <= ulong.MaxValue - count;

    private static bool Emit<TActual, TMessage>(in TActual value, ref TMessage message)
        where TActual : struct
        where TMessage : struct
    {
        if (typeof(TActual) != typeof(TMessage))
        {
            return false;
        }

        message = Unsafe.As<TActual, TMessage>(ref Unsafe.AsRef(in value));
        return true;
    }
}
