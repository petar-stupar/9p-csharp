using System.Globalization;
using System.Runtime.CompilerServices;
using NineP.Protocol.Internal;
using NineP.Protocol.Messages;

namespace NineP.Protocol.Codec.Internal;

/// <summary>
/// Writes one record into a span that has already been sized for it. The size field is reserved
/// first and back-patched once the body has been written, so the length on the wire is always the
/// length that was actually produced rather than a length that was predicted.
/// </summary>
internal static class MessageWriter
{
    // Rstatfs: type[4] bsize[4] blocks[8] bfree[8] bavail[8] files[8] ffree[8] fsid[8] namelen[4].
    private const int StatFsBodySize = 60;

    // Rgetattr: valid[8] qid[13] mode[4] uid[4] gid[4] nlink[8] rdev[8] size[8] blksize[8]
    // blocks[8] four timespecs of sec[8] nsec[8] gen[8] data_version[8] — 160 bytes with the
    // header, which reference §3.4 states as an exact figure.
    private const int RgetattrBodySize = 153;

    // Tsetattr: fid[4] valid[4] mode[4] uid[4] gid[4] size[8] atime[16] mtime[16].
    private const int TsetattrBodySize = 60;

    /// <summary>The exact number of bytes this message occupies in this dialect.</summary>
    /// <typeparam name="TMessage">The record being measured.</typeparam>
    /// <param name="message">The message.</param>
    /// <param name="dialect">The dialect the session negotiated.</param>
    /// <returns>The frame length, <c>size[4]</c> included.</returns>
    /// <exception cref="ArgumentException">The record has no encoder in this dialect.</exception>
    public static int GetEncodedSize<TMessage>(in TMessage message, Dialect dialect)
        where TMessage : struct, IMessage
    {
        if (TrySizeSession(in message, dialect, out int size)
            || TrySizeNavigation(in message, out size)
            || TrySizeLegacy(in message, dialect, out size)
            || TrySizeDotL(in message, out size))
        {
            return size;
        }

        throw Unsupported<TMessage>(dialect);
    }

    /// <summary>Writes one message into a destination sized by <see cref="GetEncodedSize"/>.</summary>
    /// <typeparam name="TMessage">The record being written.</typeparam>
    /// <param name="destination">The exact bytes the frame will occupy.</param>
    /// <param name="message">The message.</param>
    /// <param name="dialect">The dialect the session negotiated.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException">The record has no encoder in this dialect.</exception>
    public static int Write<TMessage>(Span<byte> destination, in TMessage message, Dialect dialect)
        where TMessage : struct, IMessage
    {
        WireWriter writer = new(destination);
        writer.WriteUInt32(0);
        writer.WriteUInt8((byte)TMessage.Type);
        writer.WriteUInt16(message.Tag);

        if (!TryWriteSession(ref writer, in message, dialect)
            && !TryWriteNavigation(ref writer, in message)
            && !TryWriteLegacy(ref writer, in message, dialect)
            && !TryWriteDotL(ref writer, in message))
        {
            throw Unsupported<TMessage>(dialect);
        }

        writer.PatchUInt32(0, (uint)writer.Position);
        return writer.Position;
    }

    private static bool TrySizeSession<TMessage>(in TMessage message, Dialect dialect, out int size)
        where TMessage : struct, IMessage
    {
        size = Constants.HDRSZ;

        if (typeof(TMessage) == typeof(Tversion))
        {
            size += 4 + Text(As<TMessage, Tversion>(in message).Version);
        }
        else if (typeof(TMessage) == typeof(Rversion))
        {
            size += 4 + Text(As<TMessage, Rversion>(in message).Version);
        }
        else if (typeof(TMessage) == typeof(Tauth))
        {
            ref readonly Tauth auth = ref As<TMessage, Tauth>(in message);
            size += 4 + Text(auth.Uname) + Text(auth.Aname) + NUnameSize(dialect);
        }
        else if (typeof(TMessage) == typeof(Rauth) || typeof(TMessage) == typeof(Rattach))
        {
            size += Qid.WireSize;
        }
        else if (typeof(TMessage) == typeof(Tattach))
        {
            ref readonly Tattach attach = ref As<TMessage, Tattach>(in message);
            size += 8 + Text(attach.Uname) + Text(attach.Aname) + NUnameSize(dialect);
        }
        else if (typeof(TMessage) == typeof(Rerror))
        {
            size += Text(As<TMessage, Rerror>(in message).Ename)
                + (dialect == Dialect.P9_2000_u ? 4 : 0);
        }
        else if (typeof(TMessage) == typeof(Tflush))
        {
            size += 2;
        }
        else if (typeof(TMessage) != typeof(Rflush))
        {
            size = 0;
            return false;
        }

        return true;
    }

    private static bool TrySizeNavigation<TMessage>(in TMessage message, out int size)
        where TMessage : struct, IMessage
    {
        size = Constants.HDRSZ;

        if (typeof(TMessage) == typeof(Twalk))
        {
            ref readonly Twalk walk = ref As<TMessage, Twalk>(in message);
            size += 10;
            foreach (string name in Names(in walk))
            {
                size += Text(name);
            }
        }
        else if (typeof(TMessage) == typeof(Rwalk))
        {
            size += 2 + (Qid.WireSize * Qids(As<TMessage, Rwalk>(in message)).Count);
        }
        else if (typeof(TMessage) == typeof(Tread))
        {
            size += 16;
        }
        else if (typeof(TMessage) == typeof(Rread))
        {
            size += 4 + As<TMessage, Rread>(in message).Data.Length;
        }
        else if (typeof(TMessage) == typeof(Twrite))
        {
            size += 16 + As<TMessage, Twrite>(in message).Data.Length;
        }
        else if (typeof(TMessage) == typeof(Rwrite)
            || typeof(TMessage) == typeof(Tclunk)
            || typeof(TMessage) == typeof(Tremove))
        {
            size += 4;
        }
        else if (typeof(TMessage) != typeof(Rclunk) && typeof(TMessage) != typeof(Rremove))
        {
            size = 0;
            return false;
        }

        return true;
    }

    private static bool TrySizeLegacy<TMessage>(in TMessage message, Dialect dialect, out int size)
        where TMessage : struct, IMessage
    {
        size = Constants.HDRSZ;

        if (typeof(TMessage) == typeof(Topen))
        {
            size += 5;
        }
        else if (typeof(TMessage) == typeof(Ropen) || typeof(TMessage) == typeof(Rcreate))
        {
            size += Qid.WireSize + 4;
        }
        else if (typeof(TMessage) == typeof(Tcreate))
        {
            ref readonly Tcreate create = ref As<TMessage, Tcreate>(in message);
            size += 9 + Text(create.Name)
                + (dialect == Dialect.P9_2000_u ? Text(create.Extension ?? string.Empty) : 0);
        }
        else if (typeof(TMessage) == typeof(Tstat))
        {
            size += 4;
        }
        else if (typeof(TMessage) == typeof(Rstat))
        {
            size += 4 + As<TMessage, Rstat>(in message).Stat.GetEncodedSize(dialect);
        }
        else if (typeof(TMessage) == typeof(Twstat))
        {
            size += 8 + As<TMessage, Twstat>(in message).Stat.GetEncodedSize(dialect);
        }
        else if (typeof(TMessage) != typeof(Rwstat))
        {
            size = 0;
            return false;
        }

        return true;
    }

    private static bool TryWriteSession<TMessage>(ref WireWriter writer, in TMessage message, Dialect dialect)
        where TMessage : struct, IMessage
    {
        if (typeof(TMessage) == typeof(Tversion))
        {
            ref readonly Tversion version = ref As<TMessage, Tversion>(in message);
            writer.WriteUInt32(version.Msize);
            writer.WriteString(version.Version);
        }
        else if (typeof(TMessage) == typeof(Rversion))
        {
            ref readonly Rversion version = ref As<TMessage, Rversion>(in message);
            writer.WriteUInt32(version.Msize);
            writer.WriteString(version.Version);
        }
        else if (typeof(TMessage) == typeof(Tauth))
        {
            ref readonly Tauth auth = ref As<TMessage, Tauth>(in message);
            writer.WriteUInt32(auth.Afid);
            writer.WriteString(auth.Uname);
            writer.WriteString(auth.Aname);
            WriteNUname(ref writer, dialect, auth.NUname);
        }
        else if (typeof(TMessage) == typeof(Rauth))
        {
            writer.WriteQid(As<TMessage, Rauth>(in message).Aqid);
        }
        else if (typeof(TMessage) == typeof(Tattach))
        {
            ref readonly Tattach attach = ref As<TMessage, Tattach>(in message);
            writer.WriteUInt32(attach.Fid);
            writer.WriteUInt32(attach.Afid);
            writer.WriteString(attach.Uname);
            writer.WriteString(attach.Aname);
            WriteNUname(ref writer, dialect, attach.NUname);
        }
        else if (typeof(TMessage) == typeof(Rattach))
        {
            writer.WriteQid(As<TMessage, Rattach>(in message).Qid);
        }
        else if (typeof(TMessage) == typeof(Rerror))
        {
            ref readonly Rerror error = ref As<TMessage, Rerror>(in message);
            writer.WriteString(error.Ename);
            if (dialect == Dialect.P9_2000_u)
            {
                writer.WriteUInt32(WireErrno(error.Errno, "Rerror.Errno"));
            }
        }
        else if (typeof(TMessage) == typeof(Tflush))
        {
            writer.WriteUInt16(As<TMessage, Tflush>(in message).OldTag);
        }
        else if (typeof(TMessage) != typeof(Rflush))
        {
            return false;
        }

        return true;
    }

    private static bool TryWriteNavigation<TMessage>(ref WireWriter writer, in TMessage message)
        where TMessage : struct, IMessage
    {
        if (typeof(TMessage) == typeof(Twalk))
        {
            WriteTwalk(ref writer, in As<TMessage, Twalk>(in message));
        }
        else if (typeof(TMessage) == typeof(Rwalk))
        {
            WriteRwalk(ref writer, in As<TMessage, Rwalk>(in message));
        }
        else if (typeof(TMessage) == typeof(Tread))
        {
            ref readonly Tread read = ref As<TMessage, Tread>(in message);
            writer.WriteUInt32(read.Fid);
            writer.WriteUInt64(read.Offset);
            writer.WriteUInt32(read.Count);
        }
        else if (typeof(TMessage) == typeof(Rread))
        {
            ReadOnlyMemory<byte> data = As<TMessage, Rread>(in message).Data;
            writer.WriteUInt32((uint)data.Length);
            writer.WriteBytes(data.Span);
        }
        else if (typeof(TMessage) == typeof(Twrite))
        {
            ref readonly Twrite write = ref As<TMessage, Twrite>(in message);
            writer.WriteUInt32(write.Fid);
            writer.WriteUInt64(write.Offset);
            writer.WriteUInt32((uint)write.Data.Length);
            writer.WriteBytes(write.Data.Span);
        }
        else if (typeof(TMessage) == typeof(Rwrite))
        {
            writer.WriteUInt32(As<TMessage, Rwrite>(in message).Count);
        }
        else if (typeof(TMessage) == typeof(Tclunk))
        {
            writer.WriteUInt32(As<TMessage, Tclunk>(in message).Fid);
        }
        else if (typeof(TMessage) == typeof(Tremove))
        {
            writer.WriteUInt32(As<TMessage, Tremove>(in message).Fid);
        }
        else if (typeof(TMessage) != typeof(Rclunk) && typeof(TMessage) != typeof(Rremove))
        {
            return false;
        }

        return true;
    }

    private static bool TryWriteLegacy<TMessage>(ref WireWriter writer, in TMessage message, Dialect dialect)
        where TMessage : struct, IMessage
    {
        if (typeof(TMessage) == typeof(Topen))
        {
            ref readonly Topen open = ref As<TMessage, Topen>(in message);
            writer.WriteUInt32(open.Fid);
            writer.WriteUInt8(open.Mode);
        }
        else if (typeof(TMessage) == typeof(Ropen))
        {
            ref readonly Ropen open = ref As<TMessage, Ropen>(in message);
            writer.WriteQid(open.Qid);
            writer.WriteUInt32(open.Iounit);
        }
        else if (typeof(TMessage) == typeof(Tcreate))
        {
            ref readonly Tcreate create = ref As<TMessage, Tcreate>(in message);
            writer.WriteUInt32(create.Fid);
            writer.WriteString(create.Name);
            writer.WriteUInt32(create.Perm);
            writer.WriteUInt8(create.Mode);
            if (dialect == Dialect.P9_2000_u)
            {
                writer.WriteString(create.Extension ?? string.Empty);
            }
        }
        else if (typeof(TMessage) == typeof(Rcreate))
        {
            ref readonly Rcreate create = ref As<TMessage, Rcreate>(in message);
            writer.WriteQid(create.Qid);
            writer.WriteUInt32(create.Iounit);
        }
        else if (typeof(TMessage) == typeof(Tstat))
        {
            writer.WriteUInt32(As<TMessage, Tstat>(in message).Fid);
        }
        else if (typeof(TMessage) == typeof(Rstat))
        {
            StatCodec.Write(ref writer, As<TMessage, Rstat>(in message).Stat, dialect);
        }
        else if (typeof(TMessage) == typeof(Twstat))
        {
            ref readonly Twstat wstat = ref As<TMessage, Twstat>(in message);
            writer.WriteUInt32(wstat.Fid);
            StatCodec.Write(ref writer, wstat.Stat, dialect);
        }
        else if (typeof(TMessage) != typeof(Rwstat))
        {
            return false;
        }

        return true;
    }

    private static bool TrySizeDotL<TMessage>(in TMessage message, out int size)
        where TMessage : struct, IMessage
    {
        size = Constants.HDRSZ;

        if (typeof(TMessage) == typeof(Rlerror)
            || typeof(TMessage) == typeof(Tstatfs)
            || typeof(TMessage) == typeof(Treadlink))
        {
            size += 4;
        }
        else if (typeof(TMessage) == typeof(Rstatfs))
        {
            size += StatFsBodySize;
        }
        else if (typeof(TMessage) == typeof(Tlopen)
            || typeof(TMessage) == typeof(Tfsync)
            || typeof(TMessage) == typeof(Rxattrwalk))
        {
            size += 8;
        }
        else if (typeof(TMessage) == typeof(Rlopen) || typeof(TMessage) == typeof(Rlcreate))
        {
            size += Qid.WireSize + 4;
        }
        else if (typeof(TMessage) == typeof(Rsymlink)
            || typeof(TMessage) == typeof(Rmknod)
            || typeof(TMessage) == typeof(Rmkdir))
        {
            size += Qid.WireSize;
        }
        else if (typeof(TMessage) == typeof(Rlock))
        {
            size += 1;
        }
        else if (!TrySizeDotLCounted(in message, ref size))
        {
            size = 0;
            return false;
        }

        return true;
    }

    private static bool TrySizeDotLCounted<TMessage>(in TMessage message, ref int size)
        where TMessage : struct, IMessage
    {
        if (typeof(TMessage) == typeof(Tlcreate))
        {
            size += 16 + Text(As<TMessage, Tlcreate>(in message).Name);
        }
        else if (typeof(TMessage) == typeof(Tsymlink))
        {
            ref readonly Tsymlink symlink = ref As<TMessage, Tsymlink>(in message);
            size += 8 + Text(symlink.Name) + Text(symlink.Symtgt);
        }
        else if (typeof(TMessage) == typeof(Tmknod))
        {
            size += 20 + Text(As<TMessage, Tmknod>(in message).Name);
        }
        else if (typeof(TMessage) == typeof(Trename))
        {
            size += 8 + Text(As<TMessage, Trename>(in message).Name);
        }
        else if (typeof(TMessage) == typeof(Rreadlink))
        {
            size += Text(As<TMessage, Rreadlink>(in message).Target);
        }
        else if (typeof(TMessage) == typeof(Tgetattr))
        {
            size += 12;
        }
        else if (typeof(TMessage) == typeof(Rgetattr))
        {
            size += RgetattrBodySize;
        }
        else if (typeof(TMessage) == typeof(Tsetattr))
        {
            size += TsetattrBodySize;
        }
        else if (typeof(TMessage) == typeof(Txattrwalk))
        {
            size += 8 + Text(As<TMessage, Txattrwalk>(in message).Name);
        }
        else if (typeof(TMessage) == typeof(Txattrcreate))
        {
            size += 16 + Text(As<TMessage, Txattrcreate>(in message).Name);
        }
        else if (typeof(TMessage) == typeof(Treaddir))
        {
            size += 16;
        }
        else if (typeof(TMessage) == typeof(Rreaddir))
        {
            size += 4 + As<TMessage, Rreaddir>(in message).Data.Length;
        }
        else if (typeof(TMessage) == typeof(Tlock))
        {
            size += 29 + Text(As<TMessage, Tlock>(in message).Request.ClientId);
        }
        else if (typeof(TMessage) == typeof(Tgetlock))
        {
            size += 25 + Text(As<TMessage, Tgetlock>(in message).ClientId);
        }
        else if (typeof(TMessage) == typeof(Rgetlock))
        {
            size += 21 + Text(As<TMessage, Rgetlock>(in message).Result.ClientId);
        }
        else if (typeof(TMessage) == typeof(Tlink))
        {
            size += 8 + Text(As<TMessage, Tlink>(in message).Name);
        }
        else if (typeof(TMessage) == typeof(Tmkdir))
        {
            size += 12 + Text(As<TMessage, Tmkdir>(in message).Name);
        }
        else if (typeof(TMessage) == typeof(Trenameat))
        {
            ref readonly Trenameat renameat = ref As<TMessage, Trenameat>(in message);
            size += 8 + Text(renameat.OldName) + Text(renameat.NewName);
        }
        else if (typeof(TMessage) == typeof(Tunlinkat))
        {
            size += 8 + Text(As<TMessage, Tunlinkat>(in message).Name);
        }
        else if (!IsEmptyDotLReply<TMessage>())
        {
            return false;
        }

        return true;
    }

    // The .L replies whose whole body is the header: nothing follows size, type and tag.
    private static bool IsEmptyDotLReply<TMessage>()
        where TMessage : struct, IMessage =>
        typeof(TMessage) == typeof(Rrename)
        || typeof(TMessage) == typeof(Rsetattr)
        || typeof(TMessage) == typeof(Rxattrcreate)
        || typeof(TMessage) == typeof(Rfsync)
        || typeof(TMessage) == typeof(Rlink)
        || typeof(TMessage) == typeof(Rrenameat)
        || typeof(TMessage) == typeof(Runlinkat);

    private static bool TryWriteDotL<TMessage>(ref WireWriter writer, in TMessage message)
        where TMessage : struct, IMessage
    {
        if (typeof(TMessage) == typeof(Rlerror))
        {
            writer.WriteUInt32(WireErrno(As<TMessage, Rlerror>(in message).Ecode, "Rlerror.Ecode"));
        }
        else if (typeof(TMessage) == typeof(Tstatfs))
        {
            writer.WriteUInt32(As<TMessage, Tstatfs>(in message).Fid);
        }
        else if (typeof(TMessage) == typeof(Rstatfs))
        {
            WriteStatFs(ref writer, As<TMessage, Rstatfs>(in message).Stat);
        }
        else if (typeof(TMessage) == typeof(Tlopen))
        {
            ref readonly Tlopen open = ref As<TMessage, Tlopen>(in message);
            writer.WriteUInt32(open.Fid);
            writer.WriteUInt32(open.Flags);
        }
        else if (typeof(TMessage) == typeof(Rlopen))
        {
            ref readonly Rlopen open = ref As<TMessage, Rlopen>(in message);
            writer.WriteQid(open.Qid);
            writer.WriteUInt32(open.Iounit);
        }
        else if (typeof(TMessage) == typeof(Tlcreate))
        {
            ref readonly Tlcreate create = ref As<TMessage, Tlcreate>(in message);
            writer.WriteUInt32(create.Fid);
            writer.WriteString(create.Name);
            writer.WriteUInt32(create.Flags);
            writer.WriteUInt32(create.Mode);
            writer.WriteUInt32(create.Gid);
        }
        else if (typeof(TMessage) == typeof(Rlcreate))
        {
            ref readonly Rlcreate create = ref As<TMessage, Rlcreate>(in message);
            writer.WriteQid(create.Qid);
            writer.WriteUInt32(create.Iounit);
        }
        else if (typeof(TMessage) == typeof(Tsymlink))
        {
            ref readonly Tsymlink symlink = ref As<TMessage, Tsymlink>(in message);
            writer.WriteUInt32(symlink.Fid);
            writer.WriteString(symlink.Name);
            writer.WriteString(symlink.Symtgt);
            writer.WriteUInt32(symlink.Gid);
        }
        else if (typeof(TMessage) == typeof(Rsymlink))
        {
            writer.WriteQid(As<TMessage, Rsymlink>(in message).Qid);
        }
        else if (typeof(TMessage) == typeof(Tmknod))
        {
            WriteTmknod(ref writer, in As<TMessage, Tmknod>(in message));
        }
        else if (typeof(TMessage) == typeof(Rmknod))
        {
            writer.WriteQid(As<TMessage, Rmknod>(in message).Qid);
        }
        else
        {
            return TryWriteDotLPath(ref writer, in message);
        }

        return true;
    }

    private static bool TryWriteDotLPath<TMessage>(ref WireWriter writer, in TMessage message)
        where TMessage : struct, IMessage
    {
        if (typeof(TMessage) == typeof(Trename))
        {
            ref readonly Trename rename = ref As<TMessage, Trename>(in message);
            writer.WriteUInt32(rename.Fid);
            writer.WriteUInt32(rename.Dfid);
            writer.WriteString(rename.Name);
        }
        else if (typeof(TMessage) == typeof(Treadlink))
        {
            writer.WriteUInt32(As<TMessage, Treadlink>(in message).Fid);
        }
        else if (typeof(TMessage) == typeof(Rreadlink))
        {
            writer.WriteString(As<TMessage, Rreadlink>(in message).Target);
        }
        else if (typeof(TMessage) == typeof(Tlink))
        {
            ref readonly Tlink link = ref As<TMessage, Tlink>(in message);
            writer.WriteUInt32(link.Dfid);
            writer.WriteUInt32(link.Fid);
            writer.WriteString(link.Name);
        }
        else if (typeof(TMessage) == typeof(Tmkdir))
        {
            ref readonly Tmkdir mkdir = ref As<TMessage, Tmkdir>(in message);
            writer.WriteUInt32(mkdir.Dfid);
            writer.WriteString(mkdir.Name);
            writer.WriteUInt32(mkdir.Mode);
            writer.WriteUInt32(mkdir.Gid);
        }
        else if (typeof(TMessage) == typeof(Rmkdir))
        {
            writer.WriteQid(As<TMessage, Rmkdir>(in message).Qid);
        }
        else if (typeof(TMessage) == typeof(Trenameat))
        {
            ref readonly Trenameat renameat = ref As<TMessage, Trenameat>(in message);
            writer.WriteUInt32(renameat.OldDirFid);
            writer.WriteString(renameat.OldName);
            writer.WriteUInt32(renameat.NewDirFid);
            writer.WriteString(renameat.NewName);
        }
        else if (typeof(TMessage) == typeof(Tunlinkat))
        {
            ref readonly Tunlinkat unlinkat = ref As<TMessage, Tunlinkat>(in message);
            writer.WriteUInt32(unlinkat.DirFid);
            writer.WriteString(unlinkat.Name);
            writer.WriteUInt32(unlinkat.Flags);
        }
        else
        {
            return TryWriteDotLAttr(ref writer, in message);
        }

        return true;
    }

    private static bool TryWriteDotLAttr<TMessage>(ref WireWriter writer, in TMessage message)
        where TMessage : struct, IMessage
    {
        if (typeof(TMessage) == typeof(Tgetattr))
        {
            ref readonly Tgetattr getattr = ref As<TMessage, Tgetattr>(in message);
            writer.WriteUInt32(getattr.Fid);
            writer.WriteUInt64((ulong)getattr.RequestMask);
        }
        else if (typeof(TMessage) == typeof(Rgetattr))
        {
            WriteRgetattr(ref writer, in As<TMessage, Rgetattr>(in message));
        }
        else if (typeof(TMessage) == typeof(Tsetattr))
        {
            WriteTsetattr(ref writer, in As<TMessage, Tsetattr>(in message));
        }
        else if (typeof(TMessage) == typeof(Txattrwalk))
        {
            ref readonly Txattrwalk walk = ref As<TMessage, Txattrwalk>(in message);
            writer.WriteUInt32(walk.Fid);
            writer.WriteUInt32(walk.NewFid);
            writer.WriteString(walk.Name);
        }
        else if (typeof(TMessage) == typeof(Rxattrwalk))
        {
            writer.WriteUInt64(As<TMessage, Rxattrwalk>(in message).Size);
        }
        else if (typeof(TMessage) == typeof(Txattrcreate))
        {
            ref readonly Txattrcreate create = ref As<TMessage, Txattrcreate>(in message);
            writer.WriteUInt32(create.Fid);
            writer.WriteString(create.Name);
            writer.WriteUInt64(create.AttrSize);
            writer.WriteUInt32((uint)create.Flags);
        }
        else
        {
            return TryWriteDotLIo(ref writer, in message);
        }

        return true;
    }

    private static bool TryWriteDotLIo<TMessage>(ref WireWriter writer, in TMessage message)
        where TMessage : struct, IMessage
    {
        if (typeof(TMessage) == typeof(Treaddir))
        {
            ref readonly Treaddir readdir = ref As<TMessage, Treaddir>(in message);
            writer.WriteUInt32(readdir.Fid);
            writer.WriteUInt64(readdir.Offset);
            writer.WriteUInt32(readdir.Count);
        }
        else if (typeof(TMessage) == typeof(Rreaddir))
        {
            ReadOnlyMemory<byte> data = As<TMessage, Rreaddir>(in message).Data;
            writer.WriteUInt32((uint)data.Length);
            writer.WriteBytes(data.Span);
        }
        else if (typeof(TMessage) == typeof(Tfsync))
        {
            // The encoder always emits datasync[4] (S-19): diod and v9fs expect the 15-byte frame,
            // and a peer that only reads fid[4] simply leaves the four bytes unread.
            ref readonly Tfsync fsync = ref As<TMessage, Tfsync>(in message);
            writer.WriteUInt32(fsync.Fid);
            writer.WriteUInt32(fsync.Datasync);
        }
        else if (typeof(TMessage) == typeof(Tlock))
        {
            WriteTlock(ref writer, in As<TMessage, Tlock>(in message));
        }
        else if (typeof(TMessage) == typeof(Rlock))
        {
            writer.WriteUInt8((byte)As<TMessage, Rlock>(in message).Status);
        }
        else if (typeof(TMessage) == typeof(Tgetlock))
        {
            WriteTgetlock(ref writer, in As<TMessage, Tgetlock>(in message));
        }
        else if (typeof(TMessage) == typeof(Rgetlock))
        {
            WriteRgetlock(ref writer, in As<TMessage, Rgetlock>(in message));
        }
        else
        {
            return IsEmptyDotLReply<TMessage>();
        }

        return true;
    }

    private static void WriteStatFs(ref WireWriter writer, in StatFs stat)
    {
        writer.WriteUInt32(stat.Type);
        writer.WriteUInt32(stat.BlockSize);
        writer.WriteUInt64(stat.Blocks);
        writer.WriteUInt64(stat.BlocksFree);
        writer.WriteUInt64(stat.BlocksAvailable);
        writer.WriteUInt64(stat.Files);
        writer.WriteUInt64(stat.FilesFree);
        writer.WriteUInt64(stat.FsId);
        writer.WriteUInt32(stat.NameLength);
    }

    private static void WriteTmknod(ref WireWriter writer, in Tmknod mknod)
    {
        writer.WriteUInt32(mknod.Dfid);
        writer.WriteString(mknod.Name);
        writer.WriteUInt32(mknod.Mode);
        writer.WriteUInt32(mknod.Major);
        writer.WriteUInt32(mknod.Minor);
        writer.WriteUInt32(mknod.Gid);
    }

    private static void WriteRgetattr(ref WireWriter writer, in Rgetattr attr)
    {
        writer.WriteUInt64((ulong)attr.Valid);
        writer.WriteQid(attr.Qid);
        writer.WriteUInt32(attr.Mode);
        writer.WriteUInt32(attr.Uid);
        writer.WriteUInt32(attr.Gid);
        writer.WriteUInt64(attr.NLink);
        writer.WriteUInt64(attr.Rdev);
        writer.WriteUInt64(attr.Size);
        writer.WriteUInt64(attr.BlkSize);
        writer.WriteUInt64(attr.Blocks);
        WriteTimeSpec(ref writer, attr.ATime);
        WriteTimeSpec(ref writer, attr.MTime);
        WriteTimeSpec(ref writer, attr.CTime);
        WriteTimeSpec(ref writer, attr.BTime);
        writer.WriteUInt64(attr.Gen);
        writer.WriteUInt64(attr.DataVersion);
    }

    private static void WriteTsetattr(ref WireWriter writer, in Tsetattr attr)
    {
        writer.WriteUInt32(attr.Fid);
        writer.WriteUInt32((uint)attr.Valid);
        writer.WriteUInt32(attr.Mode);
        writer.WriteUInt32(attr.Uid);
        writer.WriteUInt32(attr.Gid);
        writer.WriteUInt64(attr.Size);
        WriteTimeSpec(ref writer, attr.ATime);
        WriteTimeSpec(ref writer, attr.MTime);
    }

    private static void WriteTimeSpec(ref WireWriter writer, in TimeSpec time)
    {
        writer.WriteUInt64((ulong)time.Seconds);
        writer.WriteUInt64(time.Nanoseconds);
    }

    private static void WriteTlock(ref WireWriter writer, in Tlock lockMessage)
    {
        LockRequest request = lockMessage.Request;
        writer.WriteUInt32(lockMessage.Fid);
        writer.WriteUInt8((byte)request.Type);
        writer.WriteUInt32((uint)request.Flags);
        writer.WriteUInt64(request.Start);
        writer.WriteUInt64(request.Length);
        writer.WriteUInt32(request.ProcId);
        writer.WriteString(request.ClientId);
    }

    private static void WriteTgetlock(ref WireWriter writer, in Tgetlock getlock)
    {
        writer.WriteUInt32(getlock.Fid);
        writer.WriteUInt8((byte)getlock.Type);
        writer.WriteUInt64(getlock.Start);
        writer.WriteUInt64(getlock.Length);
        writer.WriteUInt32(getlock.ProcId);
        writer.WriteString(getlock.ClientId);
    }

    private static void WriteRgetlock(ref WireWriter writer, in Rgetlock getlock)
    {
        LockQueryResult result = getlock.Result;
        writer.WriteUInt8((byte)result.Type);
        writer.WriteUInt64(result.Start);
        writer.WriteUInt64(result.Length);
        writer.WriteUInt32(result.ProcId);
        writer.WriteString(result.ClientId);
    }

    private static void WriteTwalk(ref WireWriter writer, in Twalk walk)
    {
        IReadOnlyList<string> names = Names(in walk);
        writer.WriteUInt32(walk.Fid);
        writer.WriteUInt32(walk.NewFid);
        writer.WriteUInt16((ushort)names.Count);

        foreach (string name in names)
        {
            writer.WriteString(name);
        }
    }

    private static void WriteRwalk(ref WireWriter writer, in Rwalk walk)
    {
        IReadOnlyList<Qid> qids = Qids(walk);
        writer.WriteUInt16((ushort)qids.Count);

        foreach (Qid qid in qids)
        {
            writer.WriteQid(qid);
        }
    }

    private static IReadOnlyList<string> Names(in Twalk walk) => Bounded(walk.Wnames, "Twalk.Wnames");

    private static IReadOnlyList<Qid> Qids(in Rwalk walk) => Bounded(walk.Wqids, "Rwalk.Wqids");

    private static IReadOnlyList<T> Bounded<T>(IReadOnlyList<T>? elements, string what)
    {
        if (elements is null)
        {
            throw new ArgumentException(what + " must not be null", nameof(elements));
        }

        return elements.Count <= Constants.MAXWELEM
            ? elements
            : throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} may carry at most {1} elements, not {2}",
                    what,
                    Constants.MAXWELEM,
                    elements.Count),
                nameof(elements));
    }

    private static int Text(string value) => 2 + NinePText.GetByteCount(value);

    // n_uname[4] rides on Tauth and Tattach in .u and .L alike (reference §3.1).
    private static int NUnameSize(Dialect dialect) => dialect == Dialect.P9_2000 ? 0 : 4;

    private static void WriteNUname(ref WireWriter writer, Dialect dialect, uint nUname)
    {
        if (dialect != Dialect.P9_2000)
        {
            writer.WriteUInt32(nUname);
        }
    }

    // errno is signed in the API and unsigned on the wire; a negative value is not an errno, and
    // it is refused here rather than sent as a number in the billions the peer cannot map back.
    private static uint WireErrno(int errno, string what) =>
        errno >= 0
            ? (uint)errno
            : throw new ArgumentException(
                string.Format(CultureInfo.InvariantCulture, "{0} must not be negative, got {1}", what, errno));

    private static ArgumentException Unsupported<TMessage>(Dialect dialect)
        where TMessage : struct, IMessage =>
        new(string.Format(
            CultureInfo.InvariantCulture,
            "{0} has no encoder for {1}",
            MessageTypes.GetName(TMessage.Type),
            dialect));

    private static ref readonly TActual As<TMessage, TActual>(in TMessage message)
        where TMessage : struct
        where TActual : struct =>
        ref Unsafe.As<TMessage, TActual>(ref Unsafe.AsRef(in message));
}
