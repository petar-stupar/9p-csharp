using System.Buffers;
using System.Globalization;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Internal;
using NineP.Protocol.Messages;

namespace NineP.Server.Internal;

/// <summary>
/// Maps every legal T-message onto exactly one handler method (§5.8.1). Everything the reference
/// makes the server's business — fid state, walk semantics, open state, perm masking, the
/// clunk-even-on-error rule — happens here, so a handler never sees a T-message at all.
/// </summary>
internal sealed class Dispatcher(ServerSession session, OpenState openState)
{
    /// <summary>
    /// Reference §5.2: a server with no authenticator refuses <c>Tauth</c> with exactly this ename
    /// in 9P2000 and 9P2000.u and with <c>ECONNREFUSED</c> in <c>.L</c>. The wording is the
    /// reference's, not the table's row for the errno ("Connection refused"), so it is stated here.
    /// </summary>
    private static readonly NinePError AuthenticationNotRequired = new("authentication not required", Errno.ECONNREFUSED);

    private static readonly NinePError AuthenticationFailed = NinePError.FromEname("authentication failed");

    private const FilePermissions FilePermMask = FilePermissions.AllRead | FilePermissions.AllWrite;
    private const FilePermissions DirectoryPermMask =
        FilePermissions.OwnerAll | FilePermissions.GroupAll | FilePermissions.OtherAll;

    /// <summary>
    /// Every T-message this dispatcher routes, in the order of the table published as
    /// <c>docs/server.md</c>. <c>ServerDocTests</c> reads both and asserts they agree, so the
    /// document cannot fall behind the switch below.
    /// </summary>
    public static IReadOnlyList<MessageType> HandledTypes { get; } =
    [
        MessageType.Tversion,
        MessageType.Tauth,
        MessageType.Tattach,
        MessageType.Tflush,
        MessageType.Twalk,
        MessageType.Topen,
        MessageType.Tlopen,
        MessageType.Tcreate,
        MessageType.Tlcreate,
        MessageType.Tmkdir,
        MessageType.Tsymlink,
        MessageType.Tmknod,
        MessageType.Tread,
        MessageType.Treaddir,
        MessageType.Twrite,
        MessageType.Tclunk,
        MessageType.Tremove,
        MessageType.Tunlinkat,
        MessageType.Tstat,
        MessageType.Tgetattr,
        MessageType.Twstat,
        MessageType.Tsetattr,
        MessageType.Trename,
        MessageType.Trenameat,
        MessageType.Treadlink,
        MessageType.Tlink,
        MessageType.Tlock,
        MessageType.Tgetlock,
        MessageType.Txattrwalk,
        MessageType.Txattrcreate,
        MessageType.Tstatfs,
        MessageType.Tfsync,
    ];

    /// <summary>Answers one request, or suppresses the answer when a <c>Tflush</c> claimed it.</summary>
    /// <param name="type">The type peeked out of the frame.</param>
    /// <param name="frame">The complete frame.</param>
    /// <param name="pending">The tag's state, which decides whether a reply may still go out.</param>
    /// <param name="cancellationToken">Cancels the handler; a flush fires it.</param>
    /// <returns>A task that completes when the reply has been queued or suppressed.</returns>
    public async ValueTask HandleAsync(
        MessageType type,
        ReadOnlyMemory<byte> frame,
        PendingRequest pending,
        CancellationToken cancellationToken)
    {
        long started = session.Options.TimeProvider.GetTimestamp();

        try
        {
            pending.Summary = Summarise(type, frame);
            using FidLease lease = await session.Fids.AcquireAsync(
                ExistingFids(type, frame), cancellationToken).ConfigureAwait(false);
            // Admission must enqueue same-fid operations in wire order (including split UTF-8
            // writes). Run handler code on the pool after that ordering has been established.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            if (type is not (MessageType.Tattach or MessageType.Tread or MessageType.Twrite or MessageType.Tclunk)
                && lease.Entries.Any(entry => entry.IsAuth))
            {
                throw new NinePException(NinePError.FromErrno(Errno.EPERM));
            }

            using IDisposable? namespaceLease = UsesNamespace(type)
                && (type != MessageType.Tclunk || lease.Entries.Any(entry => entry.Flags.HasFlag(OpenFlags.RemoveOnClose)))
                ? await openState.Paths.AcquireAsync(() => NamespacePaths(type, frame, lease), cancellationToken).ConfigureAwait(false)
                : null;
            await RouteAsync(type, frame, pending, cancellationToken).ConfigureAwait(false);
        }
        // A malformed frame is not a handler's error: reference §8 rule 2 answers it EPROTO and
        // then closes the connection, and only the session can close. It is rethrown past every
        // catch below to ServerSession.WorkAsync, which owns both halves of that answer.
        catch (NinePProtocolException)
        {
            throw;
        }
        catch (NinePException failure)
        {
            await FailAsync(pending, failure.Error).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A flush cancelled the handler; §6.6 says its reply must never be sent, and the CAS
            // in TryComplete is what guarantees that even if the handler finished first.
        }

        // CA1031: a handler is the developer's code. Letting it take the connection down would
        // make one bad file a denial of service for every fid on it, so anything unexpected is
        // logged and answered "i/o error" — never the exception's message, which could carry a
        // path or an untrusted string (reference §8 rule 10).
#pragma warning disable CA1031
        catch (Exception failure)
#pragma warning restore CA1031
        {
            session.Options.Logger.HandlerThrew(failure);
            await FailAsync(pending, NinePError.FromErrno(Errno.EIO)).ConfigureAwait(false);
        }

        Record(pending, type, started);
    }

    private static bool UsesNamespace(MessageType type) => type is
        MessageType.Tcreate or MessageType.Tlcreate or MessageType.Twstat
        or MessageType.Trename or MessageType.Trenameat or MessageType.Tremove or MessageType.Tunlinkat
        or MessageType.Tmkdir or MessageType.Tsymlink or MessageType.Tmknod or MessageType.Tlink
        or MessageType.Tclunk;

    private IEnumerable<ulong> NamespacePaths(MessageType type, ReadOnlyMemory<byte> frame, FidLease lease)
    {
        foreach (FidEntry entry in lease.Entries)
        {
            yield return entry.Handler.Qid.Path;
            if (type is MessageType.Twstat or MessageType.Tremove or MessageType.Tclunk
                || (type == MessageType.Trename && entry.Fid == Decode<Trename>(frame).Fid))
            {
                if (entry.Parent is { } parent)
                {
                    yield return parent.Qid.Path;
                }
            }
        }
    }

    /// <summary>
    /// Hands one completed request to the audit hook (architecture §4). Everything untrusted in it
    /// has already been escaped and capped at 256 bytes (reference §8 rule 11), and a sink that
    /// throws is contained here rather than taking the connection with it.
    /// </summary>
    /// <param name="pending">The request that finished.</param>
    /// <param name="type">The T-message type.</param>
    /// <param name="started">The timestamp the request began at.</param>
    private void Record(PendingRequest pending, MessageType type, long started)
    {
        if (session.Options.RequestLog is not { } sink)
        {
            return;
        }

        RequestLogEntry entry = new(
            pending.Identity,
            type,
            pending.Summary,
            pending.Reply,
            pending.Error,
            session.Options.TimeProvider.GetElapsedTime(started));

        try
        {
            sink.Record(in entry);
        }

        // CA1031: the hook is the developer's code and the contract says it must not throw; if it
        // does, the audit entry is lost rather than the connection.
#pragma warning disable CA1031
        catch (Exception failure)
#pragma warning restore CA1031
        {
            session.Options.Logger.RequestLogSinkThrew(failure);
        }
    }

    /// <summary>
    /// A short description of a request, with every untrusted string escaped and capped
    /// (reference §8 rule 11) so that a peer cannot forge log lines through a file name.
    /// </summary>
    /// <param name="type">The T-message type.</param>
    /// <param name="frame">The complete frame.</param>
    /// <returns>The summary the audit hook receives.</returns>
    private string Summarise(MessageType type, ReadOnlyMemory<byte> frame)
    {
        string name = MessageTypes.GetName(type);

        return type switch
        {
            MessageType.Twalk => Join(name, Decode<Twalk>(frame).Wnames),
            MessageType.Tattach => name + " " + UntrustedText.Sanitize(Decode<Tattach>(frame).Aname),
            MessageType.Tcreate => name + " " + UntrustedText.Sanitize(Decode<Tcreate>(frame).Name),
            MessageType.Tlcreate => name + " " + UntrustedText.Sanitize(Decode<Tlcreate>(frame).Name),
            MessageType.Tmkdir => name + " " + UntrustedText.Sanitize(Decode<Tmkdir>(frame).Name),
            MessageType.Tunlinkat => name + " " + UntrustedText.Sanitize(Decode<Tunlinkat>(frame).Name),
            _ => name,
        };
    }

    private static string Join(string name, IReadOnlyList<string> names) =>
        UntrustedText.Sanitize(name + " " + string.Join('/', names));

    private uint[] ExistingFids(MessageType type, ReadOnlyMemory<byte> frame) => type switch
    {
        MessageType.Tattach => Decode<Tattach>(frame).Afid is uint afid && afid != Constants.NOFID ? [afid] : [],
        MessageType.Trename => [Decode<Trename>(frame).Fid, Decode<Trename>(frame).Dfid],
        MessageType.Trenameat => [Decode<Trenameat>(frame).OldDirFid, Decode<Trenameat>(frame).NewDirFid],
        MessageType.Tlink => [Decode<Tlink>(frame).Fid, Decode<Tlink>(frame).Dfid],
        MessageType.Twalk => [Decode<Twalk>(frame).Fid],
        MessageType.Topen => [Decode<Topen>(frame).Fid],
        MessageType.Tlopen => [Decode<Tlopen>(frame).Fid],
        MessageType.Tcreate => [Decode<Tcreate>(frame).Fid],
        MessageType.Tlcreate => [Decode<Tlcreate>(frame).Fid],
        MessageType.Tmkdir => [Decode<Tmkdir>(frame).Dfid],
        MessageType.Tsymlink => [Decode<Tsymlink>(frame).Fid],
        MessageType.Tmknod => [Decode<Tmknod>(frame).Dfid],
        MessageType.Tread => [Decode<Tread>(frame).Fid],
        MessageType.Treaddir => [Decode<Treaddir>(frame).Fid],
        MessageType.Twrite => [Decode<Twrite>(frame).Fid],
        MessageType.Tclunk => [Decode<Tclunk>(frame).Fid],
        MessageType.Tremove => [Decode<Tremove>(frame).Fid],
        MessageType.Tstat => [Decode<Tstat>(frame).Fid],
        MessageType.Twstat => [Decode<Twstat>(frame).Fid],
        MessageType.Tgetattr => [Decode<Tgetattr>(frame).Fid],
        MessageType.Tsetattr => [Decode<Tsetattr>(frame).Fid],
        MessageType.Tunlinkat => [Decode<Tunlinkat>(frame).DirFid],
        MessageType.Treadlink => [Decode<Treadlink>(frame).Fid],
        MessageType.Tlock => [Decode<Tlock>(frame).Fid],
        MessageType.Tgetlock => [Decode<Tgetlock>(frame).Fid],
        MessageType.Txattrwalk => [Decode<Txattrwalk>(frame).Fid],
        MessageType.Txattrcreate => [Decode<Txattrcreate>(frame).Fid],
        MessageType.Tstatfs => [Decode<Tstatfs>(frame).Fid],
        MessageType.Tfsync => [Decode<Tfsync>(frame).Fid],
        _ => [],
    };

    private async ValueTask RouteAsync(
        MessageType type,
        ReadOnlyMemory<byte> frame,
        PendingRequest pending,
        CancellationToken cancellationToken)
    {
        switch (type)
        {
            case MessageType.Tauth:
                await AuthAsync(Decode<Tauth>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tattach:
                await AttachAsync(Decode<Tattach>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Twalk:
                await WalkAsync(Decode<Twalk>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Topen:
                await OpenAsync(Decode<Topen>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tlopen:
                await LopenAsync(Decode<Tlopen>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tcreate:
                await CreateAsync(Decode<Tcreate>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tlcreate:
                await LcreateAsync(Decode<Tlcreate>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tmkdir:
                await MkdirAsync(Decode<Tmkdir>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tsymlink:
                await SymlinkAsync(Decode<Tsymlink>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tmknod:
                await MknodAsync(Decode<Tmknod>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tread:
                await ReadAsync(Decode<Tread>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Treaddir:
                await ReaddirAsync(Decode<Treaddir>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Twrite:
                await WriteAsync(Decode<Twrite>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tstat:
                await StatAsync(Decode<Tstat>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Twstat:
                await WstatAsync(Decode<Twstat>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tgetattr:
                await GetattrAsync(Decode<Tgetattr>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tsetattr:
                await SetattrAsync(Decode<Tsetattr>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Trename:
                await RenameAsync(Decode<Trename>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Trenameat:
                await RenameatAsync(Decode<Trenameat>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tunlinkat:
                await UnlinkatAsync(Decode<Tunlinkat>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Treadlink:
                await ReadlinkAsync(Decode<Treadlink>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tlink:
                await LinkAsync(Decode<Tlink>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tlock:
                await LockAsync(Decode<Tlock>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tgetlock:
                await GetlockAsync(Decode<Tgetlock>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Txattrwalk:
                await XattrwalkAsync(Decode<Txattrwalk>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Txattrcreate:
                await XattrcreateAsync(Decode<Txattrcreate>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tstatfs:
                await StatfsAsync(Decode<Tstatfs>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tfsync:
                await FsyncAsync(Decode<Tfsync>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tclunk:
                await ClunkAsync(Decode<Tclunk>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            case MessageType.Tremove:
                await RemoveAsync(Decode<Tremove>(frame), pending, cancellationToken).ConfigureAwait(false);
                return;
            default:
                await FailAsync(pending, NinePError.FromErrno(Errno.EOPNOTSUPP)).ConfigureAwait(false);
                return;
        }
    }

    private async ValueTask AuthAsync(Tauth request, PendingRequest pending, CancellationToken cancellationToken)
    {
        // S-23: a server that does not authenticate refuses Tauth outright, in the wording each
        // dialect uses — "authentication not required" and ECONNREFUSED are one error value.
        if (session.Options.Authenticator is not { } authenticator)
        {
            throw new NinePException(AuthenticationNotRequired);
        }

        // Reference §8 rule 40: past the budget the exchange is refused before the authenticator
        // is asked, so the credential check — a PBKDF2 derivation that costs the same for an
        // unknown user as for a known one — is never paid for by a peer that is guessing.
        string? peer = session.PeerAddress;
        if (!session.AuthThrottle.MayAttempt(peer))
        {
            session.Metrics.AuthThrottled();
            session.Options.Logger.AuthAttemptsThrottled(
                UntrustedText.Sanitize(peer!), session.Options.Limits.MaxAuthFailuresPerAddress);
            throw new NinePException(AuthenticationFailed);
        }

        session.AuthThrottle.RecordAttempt(peer);

        AuthRequest binding = new(request.Uname, request.NUname, request.Aname);
        IAuthSession? exchange = await authenticator
            .BeginAsync(binding, session.PeerIdentity, cancellationToken).ConfigureAwait(false);

        if (exchange is null)
        {
            throw new NinePException(AuthenticationNotRequired);
        }

        AuthFileHandler file = new(exchange, session.Options.Limits, session.Options.TimeProvider);

        // CA2000: the entry owns the exchange from here and releases it when the afid is clunked.
#pragma warning disable CA2000
        FidEntry entry = new(request.Afid, file, Identity.Anonymous(request.Uname), request.Aname)
        {
            State = FidState.Auth,
            AuthBinding = binding,
            AuthSession = exchange,
            Generation = session.Generation,
        };
#pragma warning restore CA2000

        session.Fids.Bind(entry);
        await ReplyAsync(pending, new Rauth(pending.Tag, file.Qid)).ConfigureAwait(false);
    }

    private async ValueTask AttachAsync(
        Tattach request, PendingRequest pending, CancellationToken cancellationToken)
    {
        Identity identity = await ResolveIdentityAsync(request).ConfigureAwait(false);

        // Rule 40: an attach that got through is what clears the address's budget. Everything the
        // budget counts is an exchange that began and did not end here.
        session.AuthThrottle.RecordSuccess(session.PeerAddress);
        IDirectoryHandler root = await session.Filesystem
            .AttachAsync(identity, request.Aname, cancellationToken).ConfigureAwait(false);

        // CA2000: the entry belongs to the fid table from here on.
#pragma warning disable CA2000
        FidEntry entry = new(request.Fid, root, identity, request.Aname)
        {
            Name = "/",
            Generation = session.Generation,
        };
#pragma warning restore CA2000

        session.Fids.Bind(entry);
        pending.Identity = identity;
        await ReplyAsync(pending, new Rattach(pending.Tag, root.Qid)).ConfigureAwait(false);
    }

    private ValueTask<Identity> ResolveIdentityAsync(Tattach request)
    {
        if (request.Afid == Constants.NOFID)
        {
            // §5.2: without an afid the identity is the claim, and the server must be behind an
            // authenticated transport. A server that requires authentication says so instead.
            if (session.Options.Authenticator is { IsRequired: true })
            {
                throw new NinePException(AuthenticationFailed);
            }

            return ValueTask.FromResult(Identity.Anonymous(request.Uname, request.NUname));
        }

        FidEntry afid = session.Fids.Get(request.Afid);
        if (!afid.IsAuth || afid.AuthBinding is not AuthRequest binding)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
        }

        // §5.2 and S-22: the afid is bound to the triple it was created with. Without the
        // n_uname half, an afid obtained as (uname "", n_uname 1000) would satisfy an attach
        // claiming 1001, which is exactly the bypass the triple closes.
        bool unameAgrees = request.Uname.Length == 0
            || string.Equals(request.Uname, binding.Uname, StringComparison.Ordinal);
        bool uidAgrees = request.NUname == Constants.NONUNAME || request.NUname == binding.NUname;
        bool anameAgrees = string.Equals(request.Aname, binding.Aname, StringComparison.Ordinal);

        if (!unameAgrees || !uidAgrees || !anameAgrees)
        {
            throw new NinePException(AuthenticationFailed);
        }

        // The session runs as whoever the exchange proved, never as the uname the client claimed.
        return ValueTask.FromResult(afid.AuthSession?.Identity
            ?? throw new NinePException(AuthenticationFailed));
    }

    private async ValueTask WalkAsync(Twalk request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);
        Rwalk reply = await WalkHandler
            .WalkAsync(
                session.Fids, request with { Tag = pending.Tag }, entry, checkSearch: true, openState.Paths, session.Dialect, cancellationToken)
            .ConfigureAwait(false);

        await ReplyAsync(pending, reply).ConfigureAwait(false);
    }

    private ValueTask OpenAsync(Topen request, PendingRequest pending, CancellationToken cancellationToken)
    {
        (OpenMode mode, OpenFlags flags) = OpenState.Decode(request.Mode);
        return OpenFidAsync(request.Fid, mode, flags, pending, legacy: true, cancellationToken);
    }

    private ValueTask LopenAsync(Tlopen request, PendingRequest pending, CancellationToken cancellationToken)
    {
        (OpenMode mode, OpenFlags flags) = OpenState.DecodeLinux(request.Flags);
        return OpenFidAsync(request.Fid, mode, flags, pending, legacy: false, cancellationToken);
    }

    private async ValueTask OpenFidAsync(
        uint fid,
        OpenMode mode,
        OpenFlags flags,
        PendingRequest pending,
        bool legacy,
        CancellationToken cancellationToken)
    {
        FidEntry entry = session.Fids.Get(fid);
        using IDisposable fileLease = await openState.AcquireFileAsync(entry.Handler.Qid.Path, cancellationToken)
            .ConfigureAwait(false);
        Attr attr = await entry.Handler.GetAttrAsync(cancellationToken).ConfigureAwait(false);
        OpenState.Validate(entry, attr, mode, flags);

        // Architecture §4: the permission check runs before the handler is called, so a
        // handler that forgot to check is still not reachable without permission.
        PermissionChecker.Require(attr, entry.Identity, PermissionChecker.ForOpen(mode, flags), session.Dialect);
        if (attr.Flags.HasFlag(FileFlags.Append))
        {
            flags = (flags | OpenFlags.Append) & ~OpenFlags.Truncate;
        }

        // §5.7: ORCLOSE needs remove permission in the parent, which is write on it.
        if (flags.HasFlag(OpenFlags.RemoveOnClose))
        {
            IDirectoryHandler parent = entry.Parent
                ?? throw new NinePException(NinePError.FromErrno(Errno.EPERM));
            await RequireWriteAsync(parent, entry.Identity, cancellationToken).ConfigureAwait(false);
        }

        openState.Acquire(attr);

        try
        {
            if (entry.Handler is IFileHandler file)
            {
                entry.Open = await file.OpenAsync(mode, flags, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            openState.Release(entry, attr.Flags.HasFlag(FileFlags.Exclusive));
            throw;
        }

        entry.State = FidState.Open;
        entry.Mode = mode;
        entry.Flags = flags;
        entry.HoldsExclusive = attr.Flags.HasFlag(FileFlags.Exclusive);
        entry.DirOffset = 0;
        entry.DirCount = 0;

        // S-25: iounit is msize - IOHDRSZ on every open and create reply.
        uint iounit = (uint)session.MaxPayload;
        if (legacy)
        {
            await ReplyAsync(pending, new Ropen(pending.Tag, attr.Qid, iounit)).ConfigureAwait(false);
            return;
        }

        await ReplyAsync(pending, new Rlopen(pending.Tag, attr.Qid, iounit)).ConfigureAwait(false);
    }

    private async ValueTask CreateAsync(
        Tcreate request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FileKind kind = KindOfCreatePerm(request.Perm, request.Extension);
        (OpenMode mode, OpenFlags flags) = OpenState.Decode(request.Mode);

        // §8 rule 19: DMAPPEND, DMEXCL and DMTMP are what open(2) lets a create ask for, and
        // they reach the handler as CreateRequest.FileFlags. DMAUTH and DMMOUNT are the server's
        // own, and MaskAgainstParent would drop them without a word, so a create asking for
        // either is refused rather than answered Rcreate for a file that has neither.
        if ((request.Perm & ModeBits.ServerOwnedFlagBits) != 0)
        {
            throw new NinePException(new NinePError("create cannot set DMAUTH or DMMOUNT", Errno.EPERM));
        }

        FileFlags fileFlags = AttrProjector.FlagsOf(request.Perm) & AttrProjector.SettableFlags;

        // open(5): a create that makes a directory must open it for reading.
        if (kind == FileKind.Directory && mode != OpenMode.Read)
        {
            throw new NinePException(NinePError.FromEname("bad open mode"));
        }

        // §8 rule 25: a create is judged exactly as the open it performs, and open(5) allows a
        // directory neither OTRUNC nor ORCLOSE — the same refusal OpenState.Validate gives.
        if (kind == FileKind.Directory
            && (flags.HasFlag(OpenFlags.Truncate) || flags.HasFlag(OpenFlags.RemoveOnClose)))
        {
            throw new NinePException(NinePError.FromErrno(Errno.EISDIR));
        }

        // §5.5 and §8 rule 24: in .u the extension is the symlink's target for DMSYMLINK and
        // "b maj min" / "c maj min" for DMDEVICE. Passing that text on as a target left rdev null
        // and the device numbers lost, so it is parsed here and a malformed one is refused.
        DeviceId? rdev = null;
        string? target = request.Extension;

        if (kind is FileKind.CharDevice or FileKind.BlockDevice)
        {
            rdev = ParseDevice(request.Extension)
                ?? throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
            target = null;
        }

        IHandler created = await CreateChildAsync(
            request.Fid, request.Name, kind, AttrProjector.MaskCreatePerm(request.Perm), mode, flags,
            fileFlags, target, rdev,
            Constants.NONUNAME, adopt: true, cancellationToken).ConfigureAwait(false);

        await ReplyAsync(pending, new Rcreate(pending.Tag, created.Qid, (uint)session.MaxPayload))
            .ConfigureAwait(false);
    }

    private async ValueTask LcreateAsync(
        Tlcreate request, PendingRequest pending, CancellationToken cancellationToken)
    {
        (OpenMode mode, OpenFlags flags) = OpenState.DecodeLinux(request.Flags);
        IHandler created = await CreateChildAsync(
            request.Fid, request.Name, FileKind.File, AttrProjector.MaskCreatePerm(request.Mode),
            mode, flags, FileFlags.None, null, null, request.Gid, adopt: true, cancellationToken)
            .ConfigureAwait(false);

        await ReplyAsync(pending, new Rlcreate(pending.Tag, created.Qid, (uint)session.MaxPayload))
            .ConfigureAwait(false);
    }

    private async ValueTask MkdirAsync(Tmkdir request, PendingRequest pending, CancellationToken cancellationToken)
    {
        IHandler created = await CreateChildAsync(
            request.Dfid, request.Name, FileKind.Directory, AttrProjector.MaskCreatePerm(request.Mode),
            OpenMode.Read, OpenFlags.None, FileFlags.None, null, null, request.Gid, adopt: false, cancellationToken)
            .ConfigureAwait(false);

        await ReplyAsync(pending, new Rmkdir(pending.Tag, created.Qid)).ConfigureAwait(false);
    }

    private async ValueTask SymlinkAsync(
        Tsymlink request, PendingRequest pending, CancellationToken cancellationToken)
    {
        IHandler created = await CreateChildAsync(
            request.Fid, request.Name, FileKind.Symlink, DirectoryPermMask, OpenMode.Read,
            OpenFlags.None, FileFlags.None, request.Symtgt, null, request.Gid, adopt: false, cancellationToken)
            .ConfigureAwait(false);

        await ReplyAsync(pending, new Rsymlink(pending.Tag, created.Qid)).ConfigureAwait(false);
    }

    private async ValueTask MknodAsync(Tmknod request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FileKind kind = AttrProjector.PosixKindOf(request.Mode);
        DeviceId? rdev = kind is FileKind.CharDevice or FileKind.BlockDevice
            ? new DeviceId(request.Major, request.Minor)
            : null;

        IHandler created = await CreateChildAsync(
            request.Dfid, request.Name, kind, AttrProjector.MaskCreatePerm(request.Mode),
            OpenMode.Read, OpenFlags.None, FileFlags.None, null, rdev, request.Gid, adopt: false, cancellationToken)
            .ConfigureAwait(false);

        await ReplyAsync(pending, new Rmknod(pending.Tag, created.Qid)).ConfigureAwait(false);
    }

    private async ValueTask<IHandler> CreateChildAsync(
        uint fid,
        string name,
        FileKind kind,
        FilePermissions perm,
        OpenMode mode,
        OpenFlags flags,
        FileFlags fileFlags,
        string? target,
        DeviceId? rdev,
        uint gid,
        bool adopt,
        CancellationToken cancellationToken)
    {
        FidEntry entry = session.Fids.Get(fid);
        if (entry.State == FidState.Open || entry.IsAuth)
        {
            throw new NinePException(NinePError.FromEname("bad open mode"));
        }

        if (entry.Handler is not IDirectoryHandler directory)
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOTDIR));
        }

        Attr parent = await directory.GetAttrAsync(cancellationToken).ConfigureAwait(false);

        // §5.5: a create needs write permission on the directory it creates in.
        PermissionChecker.Require(parent, entry.Identity, Access.Write, session.Dialect);

        CreateRequest create = new()
        {
            Name = name,
            Kind = kind,
            Perm = MaskAgainstParent(perm, parent.Perm, kind == FileKind.Directory),
            Mode = mode,
            Flags = flags,
            FileFlags = fileFlags,
            Target = target,
            Rdev = rdev,
            Gid = gid,
            Identity = entry.Identity,
        };

        IHandler created = await directory.CreateAsync(create, cancellationToken).ConfigureAwait(false);
        Attr made = await created.GetAttrAsync(cancellationToken).ConfigureAwait(false);

        // §8 rule 19: a success reply is a statement that the file has the flags the create
        // asked for. A handler that took the request and made a plain file -- one written before
        // CreateRequest.FileFlags existed, say -- is not answered Rcreate for it: the file is
        // removed again and the create refused, so the client learns that this tree cannot give
        // it an append-only, exclusive or temporary file.
        if ((made.Flags & AttrProjector.SettableFlags) != fileFlags)
        {
            await RemoveUnflaggedAsync(directory, name, made.Kind, cancellationToken).ConfigureAwait(false);
            throw new NinePException(NinePError.FromErrno(Errno.EOPNOTSUPP));
        }

        // §5.5: after a Tcreate or a Tlcreate the same fid represents the new, opened file.
        // Tmkdir, Tsymlink and Tmknod create without opening, so the fid is left alone.
        if (adopt)
        {
            await AdoptAsync(entry, directory, name, created, made, kind, mode, flags, cancellationToken)
                .ConfigureAwait(false);
        }

        return created;
    }

    /// <summary>
    /// Removes a file a handler created without the flags the create asked for, before the create
    /// is refused. A removal that fails is logged and the refusal stands: the client is told the
    /// truth about the flags either way, and a file left behind is the handler's defect, not a
    /// reason to answer <c>Rcreate</c>.
    /// </summary>
    /// <param name="directory">The parent the file was created in.</param>
    /// <param name="name">The name it was created under.</param>
    /// <param name="kind">What the handler made.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns>A task that completes when the removal was attempted.</returns>
    private async ValueTask RemoveUnflaggedAsync(
        IDirectoryHandler directory, string name, FileKind kind, CancellationToken cancellationToken)
    {
        try
        {
            await directory.RemoveAsync(name, kind, cancellationToken).ConfigureAwait(false);
        }
        catch (NinePException failure)
        {
            session.Options.Logger.CleanupFailedSafely(failure);
        }
    }

    /// <summary>
    /// Makes the fid the new file, which §5.5 requires of a <c>Tcreate</c> and a <c>Tlcreate</c>.
    /// <para>
    /// Reference §8 rule 23 forbids an open fid with no open file behind it, and a .u
    /// <c>Tcreate</c> of a symlink, fifo or socket is exactly that: reference §5.5 requires those
    /// creates to work — it is how v9fs spells <c>symlink(2)</c> and <c>mknod(2)</c> on a .u mount,
    /// and it sends <c>OREAD</c> and clunks — so refusing them would break the operation the
    /// message exists for. The object is created and the fid does name it; what it does not do is
    /// claim to be open. A later <c>Tread</c> is then <c>"bad open mode"</c> rather than a
    /// perpetual zero-byte answer. A directory keeps its existing path: it is open, and its reads
    /// go through the directory packer rather than an <c>IOpenFile</c>.
    /// </para>
    /// <para>
    /// The open a create performs is an open like any other, so a <c>DMEXCL</c> file created here
    /// is held exclusively by the creating fid from the moment it exists (§5.5), exactly as
    /// <see cref="OpenFidAsync"/> would hold it; the lock is released with the fid.
    /// </para>
    /// </summary>
    /// <param name="entry">The fid that becomes the new file.</param>
    /// <param name="directory">The parent the file was created in.</param>
    /// <param name="name">The name it was created under.</param>
    /// <param name="created">The new handler.</param>
    /// <param name="made">The attributes the handler answered for the new file.</param>
    /// <param name="kind">The kind the create asked for.</param>
    /// <param name="mode">The access mode the create opens with.</param>
    /// <param name="flags">The flags accompanying it.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>A task that completes when the fid names the new file.</returns>
    private async ValueTask AdoptAsync(
        FidEntry entry,
        IDirectoryHandler directory,
        string name,
        IHandler created,
        Attr made,
        FileKind kind,
        OpenMode mode,
        OpenFlags flags,
        CancellationToken cancellationToken)
    {
        entry.Path = new FidPath(directory, name, entry.Path, created.Qid.Path);
        entry.Parent = directory;
        entry.Name = name;
        entry.Handler = created;

        if (created is IFileHandler file)
        {
            bool exclusive = made.Flags.HasFlag(FileFlags.Exclusive);
            openState.Acquire(made);

            try
            {
                entry.Open = await file.OpenAsync(mode, flags, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                openState.Release(entry, exclusive);
                throw;
            }

            entry.State = FidState.Open;
            entry.Mode = mode;
            entry.Flags = flags;
            entry.HoldsExclusive = exclusive;
            return;
        }

        if (kind == FileKind.Directory)
        {
            entry.State = FidState.Open;
            entry.Mode = mode;
            entry.Flags = flags;
        }
    }

    private async ValueTask ReadAsync(Tread request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);
        if (entry.Handler is AuthFileHandler auth)
        {
            // read(5) on an afid is the client collecting the server's half of the exchange;
            // it is never an open file, so the open-state checks below do not apply to it.
            int authBudget = (int)Math.Min(request.Count, (uint)session.MaxPayload);
            await ReplyAsync(pending, new Rread(
                pending.Tag,
                await auth.ReadAsync(authBudget, cancellationToken).ConfigureAwait(false)))
                .ConfigureAwait(false);
            return;
        }

        RequireReadable(entry);

        // §8 rule 4 and S-26: msize - IOHDRSZ is a service bound, so the count is clamped
        // silently here and never rejected.
        int budget = (int)Math.Min(request.Count, (uint)session.MaxPayload);
        Attr attr = await entry.Handler.GetAttrAsync(cancellationToken).ConfigureAwait(false);

        if (attr.Kind == FileKind.Directory)
        {
            await ReplyAsync(pending, new Rread(
                pending.Tag,
                await ReadDirectoryAsync(entry, request.Offset, budget, cancellationToken)
                    .ConfigureAwait(false)))
                .ConfigureAwait(false);
            return;
        }

        // §9 (binding): no per-message allocation of payload copies in the hot path. Two
        // things keep this read off the allocator. The payload is a rental from the shared
        // pool, returned as soon as the reply has been encoded into its own rental — the
        // encode is the first thing CompleteAsync does, before it claims anything, so the
        // bytes are already copied by the time the await returns. And the rental is no bigger
        // than the file has left to give: the shipped client asks for a whole iounit on every
        // read and so does v9fs, so a budget-sized array per Tread cost a 1 MiB allocation to
        // carry five bytes. A file that reports no size at or past the offset — a synthetic
        // one whose length is not its content — still gets the whole budget, which is the cap.
        //
        // A pooled buffer arrives with whatever the last renter left in it, so only the bytes
        // the handler says it wrote are sent: IOpenFile.ReadAsync's count is the contract.
        int wanted = budget;
        if (attr.Size > request.Offset && attr.Size - request.Offset < (ulong)wanted)
        {
            wanted = (int)(attr.Size - request.Offset);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(wanted);

        try
        {
            // §8 rule 23: an open fid with no open file behind it is refused at open and at
            // create, so this is unreachable — and if a future path ever reaches it, the
            // answer is the error, never the zero bytes that read as an empty file.
            int read = entry.Open is null
                ? throw new NinePException(NinePError.FromErrno(Errno.ENXIO))
                : await entry.Open
                    .ReadAsync(request.Offset, buffer.AsMemory(0, wanted), cancellationToken)
                    .ConfigureAwait(false);

            await ReplyAsync(pending, new Rread(pending.Tag, buffer.AsMemory(0, Math.Clamp(read, 0, wanted))))
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<byte[]> ReadDirectoryAsync(
        FidEntry entry, ulong offset, int budget, CancellationToken cancellationToken)
    {
        // Reference §5.9 and §6.6 of the dialect table: in .L a directory is read with Treaddir
        // alone, so a Tread on one is an error rather than a second listing format.
        if (session.Dialect == Dialect.P9_2000_L)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EISDIR));
        }

        return entry.Handler is IDirectoryHandler directory
            ? await DirectoryPacker
                .PackStatRecordsAsync(directory, entry, session.Dialect, offset, budget, cancellationToken)
                .ConfigureAwait(false)
            : throw new NinePException(NinePError.FromErrno(Errno.ENOTDIR));
    }

    private async ValueTask ReaddirAsync(
        Treaddir request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);
        RequireReadable(entry);

        if (entry.Handler is not IDirectoryHandler directory)
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOTDIR));
        }

        // §6.7: Rreaddir's own header allowance, not IOHDRSZ.
        int budget = (int)Math.Min(request.Count, (uint)((int)session.Msize - Constants.READDIRHDRSZ));
        byte[] data = await DirectoryPacker
            .PackDirentsAsync(directory, request.Offset, budget, cancellationToken)
            .ConfigureAwait(false);

        await ReplyAsync(pending, new Rreaddir(pending.Tag, data)).ConfigureAwait(false);
    }

    private static void RequireReadable(FidEntry entry)
    {
        // read(5): a Tread needs a fid opened for reading, searching, or both ways.
        if (entry.State != FidState.Open)
        {
            throw new NinePException(NinePError.FromEname("bad open mode"));
        }

        if (entry.Mode == OpenMode.Write)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EACCES));
        }
    }

    private async ValueTask ClunkAsync(Tclunk request, PendingRequest pending, CancellationToken cancellationToken)
    {
        // CA2000: the finally below releases the entry through FidTable.ReleaseAsync, which is
        // what clunk(5) requires and what the analyzer cannot see through.
#pragma warning disable CA2000
        if (!session.Fids.Remove(request.Fid, out FidEntry? entry) || entry is null)
#pragma warning restore CA2000
        {
            throw new NinePException(NinePError.FromErrno(Errno.EBADF));
        }

        await FinalizeAsync(entry, cancellationToken).ConfigureAwait(false);

        await ReplyAsync(pending, new Rclunk(pending.Tag)).ConfigureAwait(false);
    }

    /// <summary>Finalizes every fid during reset or connection shutdown, retaining cleanup failures in logs.</summary>
    /// <param name="cancellationToken">Cancels cleanup when the caller explicitly requests it.</param>
    /// <returns>A task that completes after all entries have been finalized.</returns>
    public async ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        await session.Fids.ClearAsync(cancellationToken, FinalizeForShutdownAsync).ConfigureAwait(false);
    }

    private async ValueTask FinalizeForShutdownAsync(FidEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            using IDisposable? namespaceLease = entry.Flags.HasFlag(OpenFlags.RemoveOnClose)
                ? await openState.Paths.AcquireAsync(
                    () => entry.Parent is { } parent ? new[] { parent.Qid.Path, entry.Handler.Qid.Path }
                        : new[] { entry.Handler.Qid.Path }, cancellationToken).ConfigureAwait(false)
                : null;
            await FinalizeAsync(entry, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Cleanup must continue for other fids even when a user handler fails.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            session.Options.Logger.CleanupFailedSafely(failure);
        }
    }

    private async ValueTask FinalizeAsync(FidEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await CommitXattrAsync(entry, cancellationToken).ConfigureAwait(false);
            if (entry.Flags.HasFlag(OpenFlags.RemoveOnClose) && entry.Parent is { } parent)
            {
                Attr attr = await entry.Handler.GetAttrAsync(cancellationToken).ConfigureAwait(false);
                await parent.RemoveAsync(entry.Name, attr.Kind, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await FidTable.ReleaseAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                openState.Release(entry, entry.HoldsExclusive);
            }
        }
    }

    private async ValueTask RemoveAsync(Tremove request, PendingRequest pending, CancellationToken cancellationToken)
    {
        // CA2000: the finally below releases the entry through FidTable.ReleaseAsync, which is
        // what clunk(5) requires and what the analyzer cannot see through.
#pragma warning disable CA2000
        if (!session.Fids.Remove(request.Fid, out FidEntry? entry) || entry is null)
#pragma warning restore CA2000
        {
            throw new NinePException(NinePError.FromErrno(Errno.EBADF));
        }

        try
        {
            if (entry.Parent is not { } parent)
            {
                // remove(5) works through the parent directory; the root of a tree has none.
                throw new NinePException(NinePError.FromErrno(Errno.EPERM));
            }

            // remove(5): the removal needs write permission in the parent, not on the file.
            await RequireWriteAsync(parent, entry.Identity, cancellationToken).ConfigureAwait(false);

            Attr attr = await entry.Handler.GetAttrAsync(cancellationToken).ConfigureAwait(false);
            await parent.RemoveAsync(entry.Name, attr.Kind, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.Flags &= ~OpenFlags.RemoveOnClose;
            await FinalizeAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        await ReplyAsync(pending, new Rremove(pending.Tag)).ConfigureAwait(false);
    }


    private async ValueTask WriteAsync(Twrite request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);
        if (entry.Handler is AuthFileHandler auth)
        {
            // write(5) on an afid delivers the client's credential; the core bounds the
            // exchange, and the authenticator decides what the bytes mean.
            int accepted = await auth
                .WriteAsync(request.Data, cancellationToken).ConfigureAwait(false);
            await ReplyAsync(pending, new Rwrite(pending.Tag, (uint)accepted)).ConfigureAwait(false);
            return;
        }

        RequireWritable(entry);
        using IDisposable fileLease = await openState.AcquireFileAsync(entry.Handler.Qid.Path, cancellationToken)
            .ConfigureAwait(false);

        // S-26: msize - IOHDRSZ shortens a write and is reported back as a short write; it is
        // never a validity bound, which is why the codec accepted a larger frame at all.
        int budget = Math.Min(request.Data.Length, session.MaxPayload);
        ulong offset = request.Offset;

        if (entry.Flags.HasFlag(OpenFlags.Append) && entry.Open is { } appendTarget)
        {
            // §4.4: an append-only fid ignores the offset and writes at the end.
            offset = await appendTarget.GetSizeAsync(cancellationToken).ConfigureAwait(false);
        }

        int written = entry.Open is null
            ? throw new NinePException(NinePError.FromErrno(Errno.EINVAL))
            : await entry.Open.WriteAsync(offset, request.Data[..budget], cancellationToken)
                .ConfigureAwait(false);

        await ReplyAsync(pending, new Rwrite(pending.Tag, (uint)Math.Min(written, budget)))
            .ConfigureAwait(false);
    }

    private async ValueTask StatAsync(Tstat request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);

        // stat(5): a Tstat needs no permission of its own; walking to the file already needed
        // search permission on every directory on the way.
        Attr attr = await entry.Handler.GetAttrAsync(cancellationToken).ConfigureAwait(false);
        StatRecord record = AttrProjector.ToStat(attr, entry.Name, session.Dialect);

        await ReplyAsync(pending, new Rstat(pending.Tag, record)).ConfigureAwait(false);
    }

    private async ValueTask WstatAsync(Twstat request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);
        // §4.2: an all-don't-touch wstat is a request to commit the file to stable storage,
        // and it is the one shape that needs to know nothing about the file.
        if (request.Stat.IsAllDontTouch)
        {
            await entry.Handler.FsyncAsync(false, cancellationToken).ConfigureAwait(false);
            await ReplyAsync(pending, new Rwstat(pending.Tag)).ConfigureAwait(false);
            return;
        }

        // Everything else is judged against what a Tstat would answer for this file right
        // now: a client that fills the record from the one it just read is asking for no
        // change in the fields it copied, and §5.8's unsettable fields are refused only when
        // they would actually change something.
        Attr current = await entry.Handler.GetAttrAsync(cancellationToken).ConfigureAwait(false);
        StatRecord? asRead = session.Dialect == Dialect.P9_2000_L
            ? null
            : AttrProjector.ToStat(current, entry.Name, session.Dialect);

        SetAttr update = AttrProjector.FromWstat(request.Stat, session.Dialect, asRead);

        // §5.8: the DMDIR bit cannot change. It is the one unsettable field the projector
        // cannot judge alone, because whether the bit is a change depends on what the file
        // already is.
        if (request.Stat.Mode != uint.MaxValue)
        {
            bool asksForDirectory = (request.Stat.Mode & ModeBits.DMDIR) != 0;

            if (asksForDirectory != (current.Kind == FileKind.Directory))
            {
                throw new NinePException(new NinePError("wstat cannot change DMDIR", (int)Errno.EPERM));
            }

            // §5.8 and §8 rule 19: DMAPPEND, DMEXCL and DMTMP are settable -- stat(5) says the
            // directory bit is the one mode bit a wstat cannot change -- and reach the handler as
            // SetAttr.Flags. They are judged against the file's own flags, in every dialect: a
            // client that fills a Twstat from the Rstat it just read echoes the bits back and
            // asks for no change in them, so the handler is given a value only for a real
            // change. DMAUTH and DMMOUNT are the server's, refused like the other unsettable
            // fields when they would change.
            FileFlags asked = AttrProjector.FlagsOf(request.Stat.Mode);
            FileFlags held = current.Flags;

            if ((asked & ~AttrProjector.SettableFlags) != (held & ~AttrProjector.SettableFlags))
            {
                throw new NinePException(new NinePError("wstat cannot set DMAUTH or DMMOUNT", (int)Errno.EPERM));
            }

            if ((asked & AttrProjector.SettableFlags) != (held & AttrProjector.SettableFlags))
            {
                update = update with { Flags = asked & AttrProjector.SettableFlags };
            }
        }

        await ApplyAsync(entry, update, cancellationToken).ConfigureAwait(false);
        await ReplyAsync(pending, new Rwstat(pending.Tag)).ConfigureAwait(false);
    }

    private async ValueTask GetattrAsync(
        Tgetattr request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);
        Attr attr = await entry.Handler.GetAttrAsync(cancellationToken).ConfigureAwait(false);

        await ReplyAsync(
            pending, AttrProjector.ToGetattr(pending.Tag, attr, request.RequestMask, SuppliedBy(attr)))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// What the handler actually supplied, for <c>Rgetattr.valid</c> (reference §8 rule 22).
    /// <c>Attr</c> has no "unknown", so for <c>btime</c>, <c>gen</c> and <c>data_version</c> — the
    /// three fields a POSIX <c>stat(2)</c> does not have and most trees never fill in — a zero is
    /// read as "not supplied" and the bit is left out. Everything else is derived from the qid,
    /// the kind and the permission bits every handler must answer with, so it is always valid; the
    /// reply is still the full 160 bytes and the qid is valid whatever the mask says.
    /// </summary>
    /// <param name="attr">The attributes the handler answered with.</param>
    /// <returns>The mask of fields this reply may mark valid.</returns>
    private static GetAttrMask SuppliedBy(Attr attr)
    {
        GetAttrMask supplied =
            GetAttrMask.All & ~(GetAttrMask.BTime | GetAttrMask.Gen | GetAttrMask.DataVersion);

        supplied |= attr.BTime == default ? GetAttrMask.None : GetAttrMask.BTime;
        supplied |= attr.Gen == 0 ? GetAttrMask.None : GetAttrMask.Gen;
        supplied |= attr.DataVersion == 0 ? GetAttrMask.None : GetAttrMask.DataVersion;

        return supplied;
    }

    private async ValueTask SetattrAsync(
        Tsetattr request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);
        // Workspace §4.6: modifiers require their base time bit. Refuse the whole update
        // before a handler can apply another field or mistake an empty projection for fsync.
        if ((request.Valid.HasFlag(SetAttrMask.ATimeSet) && !request.Valid.HasFlag(SetAttrMask.ATime))
            || (request.Valid.HasFlag(SetAttrMask.MTimeSet) && !request.Valid.HasFlag(SetAttrMask.MTime)))
        {
            throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
        }
        // Unlike the all-don't-touch Twstat, a literal zero mask is only a no-change request.
        if (request.Valid == SetAttrMask.None)
        {
            await ReplyAsync(pending, new Rsetattr(pending.Tag)).ConfigureAwait(false);
            return;
        }
        SetAttr update = AttrProjector.FromSetattr(in request);
        await ApplyAsync(entry, update, cancellationToken).ConfigureAwait(false);
        await ReplyAsync(pending, new Rsetattr(pending.Tag)).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies one update after the checks stat(5) requires: the owner may change mode, group and
    /// times; a length change needs write permission; a rename needs write permission in the
    /// parent. The whole update is checked before any of it is applied, because a wstat is atomic.
    /// </summary>
    /// <param name="entry">The fid being changed.</param>
    /// <param name="update">The fields to change.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>A task that completes when the handler has applied it.</returns>
    private async ValueTask ApplyAsync(FidEntry entry, SetAttr update, CancellationToken cancellationToken)
    {
        using IDisposable fileLease = await openState.AcquireFileAsync(entry.Handler.Qid.Path, cancellationToken)
            .ConfigureAwait(false);
        Attr attr = await entry.Handler.GetAttrAsync(cancellationToken).ConfigureAwait(false);
        bool owner = PermissionChecker.IsOwner(attr, entry.Identity);

        if ((update.Perm is not null || update.Flags is not null || update.Gid is not null
            || update.GroupName is not null || update.Uid is not null || update.MTime is not null
            || update.MTimeToNow) && !owner)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EPERM));
        }

        if (update.Size is not null)
        {
            PermissionChecker.Require(attr, entry.Identity, Access.Write, session.Dialect);
            // stat(5) forbids nonzero directory length; .L size uses truncate semantics.
            // A legacy zero-length update remains subject to the handler's own policy.
            if (attr.Kind == FileKind.Directory && (session.Dialect == Dialect.P9_2000_L || update.Size != 0))
            {
                throw new NinePException(NinePError.FromErrno(Errno.EISDIR));
            }
        }

        if (update.Name is not null)
        {
            IDirectoryHandler parent = entry.Parent
                ?? throw new NinePException(NinePError.FromErrno(Errno.EPERM));
            await RequireWriteAsync(parent, entry.Identity, cancellationToken).ConfigureAwait(false);
        }

        string oldName = entry.Name;
        SetAttr resolved = AttrProjector.ResolveServerTimes(update, session.Options.TimeProvider);
        await entry.Handler.SetAttrAsync(resolved, cancellationToken).ConfigureAwait(false);

        if (resolved.Name is { } renamed)
        {
            if (entry.Path is { } path)
            {
                openState.Paths.Move(entry.Handler, path.Directory, oldName, path.Directory, renamed, path.Previous);
            }
            entry.Name = renamed;
        }

        // §8 rule 19: the reply says the file now has these flags, so the file is read back. A
        // handler that answered the update without applying them -- one written before
        // SetAttr.Flags existed, say -- has not done the work, and the client is told so rather
        // than answered Rwstat.
        if (resolved.Flags is FileFlags wanted)
        {
            Attr after = await entry.Handler.GetAttrAsync(cancellationToken).ConfigureAwait(false);

            if ((after.Flags & AttrProjector.SettableFlags) != wanted)
            {
                throw new NinePException(NinePError.FromErrno(Errno.EOPNOTSUPP));
            }
        }
    }

    private async ValueTask RenameAsync(Trename request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);
        FidEntry destination = Fid(pending, request.Dfid);

        IDirectoryHandler source = entry.Parent
            ?? throw new NinePException(NinePError.FromErrno(Errno.EPERM));
        IDirectoryHandler target = destination.Handler as IDirectoryHandler
            ?? throw new NinePException(NinePError.FromErrno(Errno.ENOTDIR));

        await RequireWriteAsync(source, entry.Identity, cancellationToken).ConfigureAwait(false);
        await RequireWriteAsync(target, entry.Identity, cancellationToken).ConfigureAwait(false);
        PathState.ValidateMove(entry.Handler, destination);
        string oldName = entry.Name;
        await source.RenameAsync(oldName, target, request.Name, cancellationToken).ConfigureAwait(false);
        openState.Paths.Move(entry.Handler, source, oldName, target, request.Name, destination.Path);

        await ReplyAsync(pending, new Rrename(pending.Tag)).ConfigureAwait(false);
    }

    private async ValueTask RenameatAsync(
        Trenameat request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry source = Fid(pending, request.OldDirFid);
        FidEntry destination = Fid(pending, request.NewDirFid);

        IDirectoryHandler from = source.Handler as IDirectoryHandler
            ?? throw new NinePException(NinePError.FromErrno(Errno.ENOTDIR));
        IDirectoryHandler to = destination.Handler as IDirectoryHandler
            ?? throw new NinePException(NinePError.FromErrno(Errno.ENOTDIR));

        await RequireWriteAsync(from, source.Identity, cancellationToken).ConfigureAwait(false);
        await RequireWriteAsync(to, source.Identity, cancellationToken).ConfigureAwait(false);
        IHandler child = await from.LookupAsync(request.OldName, cancellationToken).ConfigureAwait(false)
            ?? throw new NinePException(NinePError.FromErrno(Errno.ENOENT));
        PathState.ValidateMove(child, destination);
        await from.RenameAsync(request.OldName, to, request.NewName, cancellationToken).ConfigureAwait(false);
        openState.Paths.Move(child, from, request.OldName, to, request.NewName, destination.Path);

        await ReplyAsync(pending, new Rrenameat(pending.Tag)).ConfigureAwait(false);
    }

    private async ValueTask UnlinkatAsync(
        Tunlinkat request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.DirFid);
        IDirectoryHandler directory = entry.Handler as IDirectoryHandler
            ?? throw new NinePException(NinePError.FromErrno(Errno.ENOTDIR));

        // §8 rule 20: AT_REMOVEDIR is the only flag Tunlinkat defines, so a word carrying any
        // other bit means something this server does not implement and is refused rather than
        // ignored — a removal is not the sort of thing to guess at.
        if ((request.Flags & ~LinuxAbi.AT_REMOVEDIR) != 0)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
        }

        await RequireWriteAsync(directory, entry.Identity, cancellationToken).ConfigureAwait(false);

        IHandler? child = await directory.LookupAsync(request.Name, cancellationToken).ConfigureAwait(false)
            ?? throw new NinePException(NinePError.FromErrno(Errno.ENOENT));
        Attr attr = await child.GetAttrAsync(cancellationToken).ConfigureAwait(false);

        // §8 rule 20: the flag and the kind must agree, exactly as unlinkat(2) requires — the flag
        // is how the client says which of rmdir(2) and unlink(2) it meant, and a server that
        // ignored it would answer an rmdir of a file, or an unlink of a directory, with success.
        bool removeDirectory = (request.Flags & LinuxAbi.AT_REMOVEDIR) != 0;

        if (attr.Kind == FileKind.Directory && !removeDirectory)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EISDIR));
        }

        if (attr.Kind != FileKind.Directory && removeDirectory)
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOTDIR));
        }

        // §5.9: Tunlinkat does not clunk any fid referring to the file, unlike Tremove.
        await directory.RemoveAsync(request.Name, attr.Kind, cancellationToken).ConfigureAwait(false);
        await ReplyAsync(pending, new Runlinkat(pending.Tag)).ConfigureAwait(false);
    }

    private async ValueTask ReadlinkAsync(
        Treadlink request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);
        string target = entry.Handler is ISymlinkHandler link
            ? await link.ReadlinkAsync(cancellationToken).ConfigureAwait(false)
            : throw new NinePException(NinePError.FromErrno(Errno.EINVAL));

        await ReplyAsync(pending, new Rreadlink(pending.Tag, target)).ConfigureAwait(false);
    }

    private async ValueTask LinkAsync(Tlink request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry directory = Fid(pending, request.Dfid);
        FidEntry target = Fid(pending, request.Fid);

        IDirectoryHandler parent = directory.Handler as IDirectoryHandler
            ?? throw new NinePException(NinePError.FromErrno(Errno.ENOTDIR));
        await RequireWriteAsync(parent, directory.Identity, cancellationToken).ConfigureAwait(false);

        ILinkCapability capability = parent as ILinkCapability ?? throw Unsupported();
        await capability.LinkAsync(request.Name, target.Handler, cancellationToken).ConfigureAwait(false);

        await ReplyAsync(pending, new Rlink(pending.Tag)).ConfigureAwait(false);
    }

    private async ValueTask LockAsync(Tlock request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);
        ILockCapability capability = entry.Handler as ILockCapability ?? throw Unsupported();

        LockStatus status = await capability.LockAsync(request.Request, cancellationToken).ConfigureAwait(false);
        await ReplyAsync(pending, new Rlock(pending.Tag, status)).ConfigureAwait(false);
    }

    private async ValueTask GetlockAsync(
        Tgetlock request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);
        ILockCapability capability = entry.Handler as ILockCapability ?? throw Unsupported();

        LockRequest query = new(
            request.Type, LockFlags.None, request.Start, request.Length, request.ProcId, request.ClientId);
        LockQueryResult result = await capability.GetLockAsync(query, cancellationToken).ConfigureAwait(false);

        await ReplyAsync(pending, new Rgetlock(pending.Tag, result)).ConfigureAwait(false);
    }

    private async ValueTask XattrwalkAsync(
        Txattrwalk request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);
        Attr attr = await entry.Handler.GetAttrAsync(cancellationToken).ConfigureAwait(false);
        PermissionChecker.Require(attr, entry.Identity, Access.Read, session.Dialect);
        IXattrHandler xattrs = entry.Handler as IXattrHandler ?? throw Unsupported();

        session.Fids.RequireFree(request.NewFid, request.Fid);

        // §5.9: an empty name walks onto the NUL-separated list of attribute names.
        ReadOnlyMemory<byte> value = request.Name.Length == 0
            ? await xattrs.ListXattrAsync(cancellationToken).ConfigureAwait(false)
            : await xattrs.GetXattrAsync(request.Name, cancellationToken).ConfigureAwait(false);

        // CA2000: the entry and its reader belong to the fid table from here on.
#pragma warning disable CA2000
        FidEntry bound = new(request.NewFid, entry.Handler, entry.Identity, entry.Aname)
        {
            Name = entry.Name,
            Parent = entry.Parent,
            Path = entry.Path,
            AttachRoot = entry.AttachRoot,
            Generation = entry.Generation,
            State = FidState.Open,
            Mode = OpenMode.Read,
            XattrName = request.Name,
            Open = new ByteFile(value.ToArray()),
        };
#pragma warning restore CA2000

        session.Fids.Bind(bound);
        await ReplyAsync(pending, new Rxattrwalk(pending.Tag, (ulong)value.Length)).ConfigureAwait(false);
    }

    private async ValueTask XattrcreateAsync(
        Txattrcreate request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);
        if (entry.Handler is not IXattrHandler)
        {
            throw Unsupported();
        }

        Attr attr = await entry.Handler.GetAttrAsync(cancellationToken).ConfigureAwait(false);
        PermissionChecker.Require(attr, entry.Identity, Access.Write, session.Dialect);

        // §5.9: the fid becomes a sink; the value is written and committed on clunk, and a
        // byte count that disagrees with attr_size is an error there.
        entry.State = FidState.Open;
        entry.Mode = OpenMode.Write;
        entry.XattrName = request.Name;
        entry.XattrSize = request.AttrSize;
        entry.XattrFlags = request.Flags;
        entry.XattrBuffer = [];
        entry.Open = new XattrSink(entry);

        await ReplyAsync(pending, new Rxattrcreate(pending.Tag)).ConfigureAwait(false);
    }

    private async ValueTask StatfsAsync(Tstatfs request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);
        IStatFsCapability capability = entry.Handler as IStatFsCapability
            ?? session.Filesystem as IStatFsCapability
            ?? throw Unsupported();

        StatFs statistics = await capability.StatFsAsync(cancellationToken).ConfigureAwait(false);
        await ReplyAsync(pending, new Rstatfs(pending.Tag, statistics)).ConfigureAwait(false);
    }

    private async ValueTask FsyncAsync(Tfsync request, PendingRequest pending, CancellationToken cancellationToken)
    {
        FidEntry entry = Fid(pending, request.Fid);

        await entry.Handler.FsyncAsync(request.Datasync != 0, cancellationToken).ConfigureAwait(false);
        await ReplyAsync(pending, new Rfsync(pending.Tag)).ConfigureAwait(false);
    }

    private static async ValueTask CommitXattrAsync(FidEntry entry, CancellationToken cancellationToken)
    {
        if (entry.XattrName is not { } name || entry.XattrSize is not ulong promised)
        {
            return;
        }

        List<byte> written = entry.XattrBuffer ?? [];
        entry.XattrBuffer = null;
        entry.XattrSize = null;

        if ((ulong)written.Count != promised)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
        }

        if (entry.Handler is not IXattrHandler xattrs)
        {
            return;
        }

        // §8 rule 21: an attr_size of zero is how v9fs and diod spell removexattr(2), so the
        // attribute is removed. Storing an empty value instead left the name in the listing and
        // answered a getxattr that should have been ENODATA.
        if (promised == 0)
        {
            await xattrs.RemoveXattrAsync(name, cancellationToken).ConfigureAwait(false);
            return;
        }

        await xattrs.SetXattrAsync(name, written.ToArray(), entry.XattrFlags, cancellationToken)
            .ConfigureAwait(false);
    }

    private static NinePException Unsupported() =>
        new(NinePError.FromErrno(Errno.EOPNOTSUPP));

    private static void RequireWritable(FidEntry entry)
    {
        if (entry.State != FidState.Open)
        {
            throw new NinePException(NinePError.FromEname("bad open mode"));
        }

        // read(5): a Twrite needs a fid opened for writing or both ways.
        if (entry.Mode is not (OpenMode.Write or OpenMode.ReadWrite))
        {
            throw new NinePException(NinePError.FromErrno(Errno.EACCES));
        }
    }

    private async ValueTask RequireWriteAsync(
        IDirectoryHandler directory, Identity identity, CancellationToken cancellationToken)
    {
        Attr attr = await directory.GetAttrAsync(cancellationToken).ConfigureAwait(false);
        PermissionChecker.Require(attr, identity, Access.Write, session.Dialect);
    }

    /// <summary>A read-only file over bytes the core already holds, which is what an xattr fid is.</summary>
    private sealed class ByteFile(byte[] data) : IOpenFile
    {
        public ValueTask<int> ReadAsync(
            ulong offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int at = (int)Math.Min(offset, (ulong)data.Length);
            int count = Math.Min(buffer.Length, data.Length - at);
            data.AsMemory(at, count).CopyTo(buffer);
            return ValueTask.FromResult(count);
        }

        public ValueTask<int> WriteAsync(
            ulong offset, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) =>
            throw new NinePException(NinePError.FromErrno(Errno.EACCES));

        public ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult((ulong)data.Length);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>The sink a <c>Txattrcreate</c> fid becomes; the value is committed on clunk.</summary>
    private sealed class XattrSink(FidEntry entry) : IOpenFile
    {
        public ValueTask<int> ReadAsync(
            ulong offset, Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new NinePException(NinePError.FromErrno(Errno.EACCES));

        public ValueTask<int> WriteAsync(
            ulong offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<byte> buffer = entry.XattrBuffer ??= [];
            int at = (int)offset;
            while (buffer.Count < at)
            {
                buffer.Add(0);
            }

            for (int i = 0; i < data.Length; i++)
            {
                if (at + i < buffer.Count)
                {
                    buffer[at + i] = data.Span[i];
                    continue;
                }

                buffer.Add(data.Span[i]);
            }

            return ValueTask.FromResult(data.Length);
        }

        public ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult((ulong)(entry.XattrBuffer?.Count ?? 0));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static FilePermissions MaskAgainstParent(
        FilePermissions requested, FilePermissions parentPerm, bool directory)
    {
        // §5.5: perm & (~0777 | (dir.perm & 0777)) for a directory, and the 0666 form for a file.
        FilePermissions mask = directory ? DirectoryPermMask : FilePermMask;
        return requested & (~mask | (parentPerm & mask)) & FilePermissions.Mask;
    }

    /// <summary>
    /// Parses a .u <c>Tcreate.extension</c> of the form <c>"b maj min"</c> or <c>"c maj min"</c>
    /// into the device numbers (reference §5.5). <c>AttrProjector</c> has the same parser for the
    /// stat records it decodes, but it is private to the codec, and this is the one place a server
    /// needs it; a missing or malformed extension yields null, which the caller refuses.
    /// </summary>
    /// <param name="extension">The extension field the client sent.</param>
    /// <returns>The device numbers, or null when the text is not a device specification.</returns>
    private static DeviceId? ParseDevice(string? extension)
    {
        string[] parts = (extension ?? string.Empty).Split(' ');

        return parts.Length == 3
            && parts[0] is "b" or "c"
            && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint major)
            && uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint minor)
            ? new DeviceId(major, minor)
            : null;
    }

    private static FileKind KindOfCreatePerm(uint perm, string? extension) => perm switch
    {
        _ when (perm & 0x80000000u) != 0 => FileKind.Directory,
        _ when (perm & 0x02000000u) != 0 => FileKind.Symlink,
        _ when (perm & 0x00200000u) != 0 => FileKind.Fifo,
        _ when (perm & 0x00100000u) != 0 => FileKind.Socket,
        _ when (perm & 0x00800000u) != 0 =>
            extension is not null && extension.StartsWith("b ", StringComparison.Ordinal)
                ? FileKind.BlockDevice
                : FileKind.CharDevice,
        _ => FileKind.File,
    };

    private TMessage Decode<TMessage>(ReadOnlyMemory<byte> frame)
        where TMessage : struct, IMessage
    {
        TMessage message = MessageCodec.Decode<TMessage>(frame, session.Dialect);
        RequestNameLimit.Validate(in message, session.Options.Limits.MaxNameLength);
        return message;
    }

    /// <summary>Looks a fid up and records whose it is, so the audit hook sees the real user.</summary>
    /// <param name="pending">The request being answered.</param>
    /// <param name="fid">The fid the message named.</param>
    /// <returns>The entry.</returns>
    private FidEntry Fid(PendingRequest pending, uint fid)
    {
        FidEntry entry = session.Fids.Get(fid);
        pending.Identity = entry.Identity;
        return entry;
    }

    private ValueTask ReplyAsync<TMessage>(PendingRequest pending, TMessage message)
        where TMessage : struct, IMessage =>
        // §6.6: exactly one of "send the reply" and "suppress it" wins, and the session's
        // complete-and-enqueue step is where that is decided.
        session.CompleteAsync(pending, message);

    private ValueTask FailAsync(PendingRequest pending, NinePError error) =>
        session.CompleteAsync(pending, error);

}
