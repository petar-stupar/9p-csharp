using System.Globalization;
using System.Runtime.CompilerServices;
using NineP.Client.Internal;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Internal;
using NineP.Protocol.Messages;

namespace NineP.Client;

/// <summary>
/// A client fid: the handle a session holds on one file (architecture §6). Disposing it clunks the
/// fid, which is what makes the release deterministic — a fid that is only collected when a
/// finaliser runs is a fid the server still holds a handler open for. Once removal or disposal
/// begins, operations throw <see cref="ObjectDisposedException"/>. Closing waits for previously
/// started operations before freeing the number. Directory enumerators guard each wire request;
/// a resumed enumeration may throw after disposal, while already buffered entries remain readable.
/// </summary>
public sealed class NinePFid : IAsyncDisposable
{
    private readonly NinePSession _session;
    private ulong? _attributeSize;
    private readonly object _lifetimeGate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeOperations;
    private int _disposed;
    private bool _append;

    internal NinePFid(NinePSession session, uint fid, Qid qid)
    {
        _session = session;
        Fid = fid;
        Qid = qid;
        Iounit = session.MaxPayload;
    }

    /// <summary>The numeric fid, for tracing.</summary>
    public uint Fid { get; }

    /// <summary>The qid this fid refers to.</summary>
    public Qid Qid { get; private set; }

    /// <summary>True once the fid has been opened.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>
    /// The iounit the server reported at open, or <c>msize - IOHDRSZ</c> when it reported 0
    /// (reference §5.5).
    /// </summary>
    public int Iounit { get; private set; }

    /// <summary>The session this fid belongs to, for the layers built on it.</summary>
    internal NinePSession Session => _session;

    /// <summary>True once the fid has been clunked or removed.</summary>
    internal bool IsReleased => Volatile.Read(ref _disposed) != 0;


    /// <summary>Walks elements from this fid into a fresh fid; a partial walk throws.</summary>
    /// <param name="names">The path elements, in order; ".." is legal, "." is not.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The new fid; the caller disposes it.</returns>
    /// <exception cref="NinePException">An element did not resolve, so the walk was partial.</exception>
    public async ValueTask<NinePFid> WalkAsync(
        IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        using OperationLease operation = AcquireOperation();
        ArgumentNullException.ThrowIfNull(names);

        foreach (string name in names)
        {
            if (!NinePText.IsLegalName(name, allowParent: true))
            {
                throw new NinePProtocolException(
                    ProtocolErrorKind.Name,
                    string.Format(CultureInfo.InvariantCulture, "'{0}' is not a legal path element", UntrustedText.Sanitize(name)));
            }
        }

        foreach (string name in names)
        {
            RequestNameLimit.ValidateName(name, _session.Options.Limits.MaxNameLength);
        }
        NinePFid walked = await CloneCoreAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using OperationLease walkedOperation = walked.AcquireOperation();
            // Twalk carries at most MAXWELEM elements, so a longer path is several walks; each one
            // after the first walks the new fid onto itself, which walk(5) allows.
            for (int at = 0; at < names.Count;)
            {
                int end = at;
                int bytes = 17; // size, type, tag, fid, newfid, nwname
                while (end < names.Count && end - at < Constants.MAXWELEM)
                {
                    int next = 2 + NinePText.Utf8.GetByteCount(names[end]);
                    if (bytes + next > _session.Msize)
                    {
                        break;
                    }
                    bytes += next;
                    end++;
                }
                string[] step = [.. names.Skip(at).Take(end - at)];
                at = end;
                Rwalk reply = await _session.Messages
                    .WalkAsync(new Twalk(0, walked.Fid, walked.Fid, step), cancellationToken)
                    .ConfigureAwait(false);

                walked.Qid = Landing(reply, step, walked.Qid);
            }

            return walked;
        }
        catch
        {
            // The walk's own failure is the news. The cleanup clunk is best effort and its
            // exception must never take the caller's place -- see DisposeAsync below, which
            // swallows the ones it can name; this catch covers the rest.
            try
            {
                await CleanUpAsync(walked).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A cleanup failure must not replace the failure being reported.
            catch (Exception cleanup)
#pragma warning restore CA1031
            {
                _session.Options.Logger.WalkFidNotClunked(cleanup.Message);
            }

            throw;
        }
    }

    /// <summary>
    /// Clunks the fid a failed walk had cloned, under the session's disposal bound
    /// (<see cref="ClientOptions.DisposeTimeout"/>, or <see cref="ClientOptions.RequestTimeout"/>
    /// when that is shorter). Against a peer that has gone silent the clunk costs a request
    /// timeout and then another for the <c>Tflush</c> that cancels it, so a walk that had already
    /// failed for exactly that reason kept its caller waiting <c>4 × RequestTimeout</c> — four
    /// minutes at the defaults — for news the client had held for half of it. Nobody is waiting on
    /// this clunk's answer: the fid number returns to the pool when it finally unwinds, and
    /// closing the transport makes the server forget every fid on the connection anyway.
    /// </summary>
    /// <param name="walked">The cloned fid the walk was using.</param>
    /// <returns>A task that completes when the clunk has been answered or given up on.</returns>
    private async ValueTask CleanUpAsync(NinePFid walked)
    {
        TimeSpan bound = _session.DisposeBound();

        if (bound <= TimeSpan.Zero)
        {
            await walked.DisposeAsync().ConfigureAwait(false);
            return;
        }

        Task clunking = walked.DisposeAsync().AsTask();

        try
        {
            await clunking.WaitAsync(bound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Abandoned, not forgotten: the task is observed so that whatever settles it later --
            // the peer answering, or the session terminating -- is not an unobserved exception.
            _ = clunking.ContinueWith(
                static abandoned => _ = abandoned.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            throw;
        }
    }

    /// <summary>Clones this fid without walking; the fid must not be open (walk(5)).</summary>
    /// <param name="cancellationToken">Cancels the clone.</param>
    /// <returns>A second fid on the same file; the caller disposes it.</returns>
    /// <exception cref="NinePException">The server refused, for example because this fid is open.</exception>
    public async ValueTask<NinePFid> CloneAsync(CancellationToken cancellationToken = default)
    {
        using OperationLease operation = AcquireOperation();
        return await CloneCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<NinePFid> CloneCoreAsync(CancellationToken cancellationToken)
    {
        uint fid = _session.RentFid();

        try
        {
            Rwalk reply = await _session.Messages
                .WalkAsync(new Twalk(0, Fid, fid, []), cancellationToken).ConfigureAwait(false);

            if (reply.Wqids.Count != 0)
            {
                throw new NinePProtocolException(
                    ProtocolErrorKind.NWName, "the server answered a clone with qids");
            }

            NinePFid clone = new(_session, fid, Qid);
            _session.Track(clone);
            return clone;
        }
        catch
        {
            _session.ReturnFid(fid);
            throw;
        }
    }

    /// <summary>Opens this fid: <c>Topen</c> in 9P2000 and .u, <c>Tlopen</c> in .L.</summary>
    /// <param name="mode">The access mode.</param>
    /// <param name="flags">Flags that modify the open without changing the access mode.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>A task that completes once the fid is open.</returns>
    /// <exception cref="NinePException">The server refused the open.</exception>
    public async ValueTask OpenAsync(
        OpenMode mode, OpenFlags flags = OpenFlags.None, CancellationToken cancellationToken = default)
    {
        using OperationLease operation = AcquireOperation();
        if (_session.Dialect == Dialect.P9_2000_L)
        {
            Rlopen linux = await _session.Messages
                .LopenAsync(new Tlopen(0, Fid, ModeBits.ToLinuxFlags(mode, flags)), cancellationToken)
                .ConfigureAwait(false);

            MarkOpened(linux.Qid, linux.Iounit, flags);
            return;
        }

        Ropen reply = await _session.Messages
            .OpenAsync(new Topen(0, Fid, ModeBits.ToOpenByte(mode, flags)), cancellationToken)
            .ConfigureAwait(false);

        MarkOpened(reply.Qid, reply.Iounit, flags);
    }

    /// <summary>Creates a file in this directory fid; the fid then refers to the new, open file.</summary>
    /// <param name="name">The name to create.</param>
    /// <param name="kind">
    /// What to create: <see cref="FileKind.File"/> or <see cref="FileKind.Directory"/>. A
    /// symlink is <c>SymlinkAsync</c> and a device is <c>Tmknod</c>; both carry a payload
    /// <c>Tcreate</c> has no room for here, so asking for one is refused rather than sent as a
    /// create with the payload missing (reference §8 rule 15).
    /// </param>
    /// <param name="perm">The permission bits; the <c>07777</c> mask and nothing else.</param>
    /// <param name="mode">The access mode the new fid is opened with.</param>
    /// <param name="flags">Flags accompanying the create.</param>
    /// <param name="fileFlags">
    /// The file flags the new file is to carry: <see cref="FileFlags.Append"/>,
    /// <see cref="FileFlags.Exclusive"/> and <see cref="FileFlags.Temporary"/> are the settable
    /// ones (open(2); reference §8 rule 19). 9P2000 and .u spell them in <c>Tcreate.perm</c>;
    /// .L has no spelling at all, so a .L create that asks for one is refused.
    /// </param>
    /// <param name="cancellationToken">Cancels the create.</param>
    /// <returns>A task that completes once the fid names the new file.</returns>
    /// <exception cref="NinePException">The server refused the create, or the dialect cannot carry what was asked for.</exception>
    public async ValueTask CreateAsync(
        string name,
        FileKind kind,
        FilePermissions perm,
        OpenMode mode,
        OpenFlags flags = OpenFlags.None,
        FileFlags fileFlags = FileFlags.None,
        CancellationToken cancellationToken = default)
    {
        using OperationLease operation = AcquireOperation();
        RequireName(name);
        RequireCreatable(kind);
        RequirePermissions(perm);
        RequireSettableFlags(fileFlags);

        if (_session.Dialect == Dialect.P9_2000_L)
        {
            // Tlcreate.mode is a POSIX mode word for a plain file: a directory is Tmkdir's job,
            // and the file flags have no .L spelling at all. The server would mask them off and
            // answer Rlcreate for a plain file, which is the silent success rule 19 forbids.
            if (kind == FileKind.Directory)
            {
                throw new NinePException(new NinePError(
                    "9P2000.L creates a directory with Tmkdir, not Tlcreate; use MkdirAsync",
                    (int)Errno.EOPNOTSUPP));
            }

            if (fileFlags != FileFlags.None)
            {
                throw new NinePException(new NinePError(
                    "9P2000.L has no spelling for DMAPPEND, DMEXCL or DMTMP", (int)Errno.EOPNOTSUPP));
            }

            Rlcreate linux = await _session.Messages
                .LcreateAsync(new Tlcreate(
                    0,
                    Fid,
                    name,
                    ModeBits.ToLinuxFlags(mode, flags),
                    (uint)perm,
                    _session.Options.NUname), cancellationToken)
                .ConfigureAwait(false);

            MarkOpened(linux.Qid, linux.Iounit, flags);
            return;
        }

        Rcreate reply = await _session.Messages
            .CreateAsync(new Tcreate(
                0,
                Fid,
                name,
                CreatePerm(kind, perm, fileFlags),
                ModeBits.ToOpenByte(mode, flags),
                _session.Dialect == Dialect.P9_2000_u ? string.Empty : null), cancellationToken)
            .ConfigureAwait(false);

        MarkOpened(reply.Qid, reply.Iounit, flags);
    }

    /// <summary>Reads at an offset into the caller's buffer.</summary>
    /// <param name="offset">Where to read from.</param>
    /// <param name="buffer">Where the bytes go; at most <see cref="Iounit"/> of it is filled.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes read; 0 at end of file.</returns>
    /// <exception cref="NinePProtocolException">The reply carried more bytes than were asked for.</exception>
    public async ValueTask<int> ReadAsync(
        ulong offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using OperationLease operation = AcquireOperation();
        int count = Math.Min(buffer.Length, Iounit);
        Rread reply = await _session.Messages
            .ReadAsync(new Tread(0, Fid, offset, (uint)count), cancellationToken).ConfigureAwait(false);

        if (reply.Data.Length > count)
        {
            throw new NinePProtocolException(
                ProtocolErrorKind.Bounds, "the server answered a Tread with more bytes than asked for");
        }

        reply.Data.CopyTo(buffer);
        return reply.Data.Length;
    }

    /// <summary>Writes at an offset.</summary>
    /// <param name="offset">Where to write; ignored on an append-only file.</param>
    /// <param name="data">The bytes to write; at most <see cref="Iounit"/> of them go out.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The number of bytes the server reported writing.</returns>
    /// <exception cref="NinePProtocolException">The reply claimed more bytes than were sent.</exception>
    public async ValueTask<int> WriteAsync(
        ulong offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        using OperationLease operation = AcquireOperation();
        int count = Math.Min(data.Length, Iounit);
        Rwrite reply = await _session.Messages
            .WriteAsync(new Twrite(0, Fid, offset, data[..count]), cancellationToken).ConfigureAwait(false);

        if (reply.Count > (uint)count)
        {
            throw new NinePProtocolException(
                ProtocolErrorKind.Bounds, "the server claimed to have written more bytes than were sent");
        }

        return (int)reply.Count;
    }

    /// <summary>Reads the whole file, chunked with the in-flight window.</summary>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>Everything the file held.</returns>
    public async ValueTask<byte[]> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        using OperationLease operation = AcquireOperation();
        cancellationToken.ThrowIfCancellationRequested();
        ulong size = _attributeSize ?? (await GetAttrCoreAsync(cancellationToken).ConfigureAwait(false)).Size;
        int maximum = _session.Options.MaxReadAll;
        if (size > (ulong)maximum)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EFBIG));
        }
        return await ChunkedTransfer.ReadAllAsync(this, _session.Options.InFlightWindow, maximum, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the whole buffer with the in-flight window, except append opens and append-only
    /// qids use one outstanding write so short writes cannot reorder or duplicate appended bytes.
    /// An empty buffer sends no Twrite and never truncates. NinePSession.WriteFileAsync instead
    /// requests truncation when opening the file (ignored for append-only files).
    /// </summary>
    /// <param name="data">The bytes to write, starting at offset 0.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>A task that completes when every byte has been acknowledged.</returns>
    public async ValueTask WriteAllAsync(
        ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        using OperationLease operation = AcquireOperation();
        await ChunkedTransfer.WriteAllAsync(this, data, _append ? 1 : _session.Options.InFlightWindow, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Enumerates directory entries, unifying the two record formats: stat records in 9P2000 and
    /// .u, dirents in .L. Neither format ever yields "." or ".." to the caller (S-27).
    /// </summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The entries, in the order the server packed them.</returns>
    public IAsyncEnumerable<DirEntry> ReadDirAsync(CancellationToken cancellationToken = default) =>
        _session.Dialect == Dialect.P9_2000_L
            ? ReadDirentsAsync(cancellationToken)
            : ReadStatRecordsAsync(cancellationToken);

    /// <summary>Fetches unified attributes: <c>Tstat</c> in 9P2000 and .u, <c>Tgetattr</c> in .L.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The attributes.</returns>
    public async ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default)
    {
        using OperationLease operation = AcquireOperation();
        return await GetAttrCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Attr> GetAttrCoreAsync(CancellationToken cancellationToken)
    {
        if (_session.Dialect == Dialect.P9_2000_L)
        {
            Rgetattr linux = await _session.Messages
                .GetattrAsync(new Tgetattr(0, Fid, GetAttrMask.All), cancellationToken).ConfigureAwait(false);

            return AttrProjector.FromGetattr(in linux);
        }

        Rstat reply = await _session.Messages
            .StatAsync(new Tstat(0, Fid), cancellationToken).ConfigureAwait(false);

        return AttrProjector.FromStat(reply.Stat, _session.Dialect);
    }

    /// <summary>Applies a partial update: <c>Twstat</c> in 9P2000 and .u, <c>Tsetattr</c> in .L.</summary>
    /// <param name="update">The fields to change; a null member is "do not touch".</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the server confirmed the change.</returns>
    public async ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default)
    {
        using OperationLease operation = AcquireOperation();
        ArgumentNullException.ThrowIfNull(update);

        if (_session.Dialect == Dialect.P9_2000_L)
        {
            await _session.Messages
                .SetattrAsync(AttrProjector.ToSetattr(0, Fid, update), cancellationToken).ConfigureAwait(false);
            return;
        }

        // Everything the dialect cannot carry is refused here, before any frame goes out.
        AttrProjector.ValidateWstat(update, _session.Dialect);

        // Reference §8 rule 19: a Twstat mode word carries the permission bits and the file
        // flags together. An update stating one half is completed from the file's own record,
        // which is what Plan 9's chmod and Linux v9fs do, so a chmod never clears DMAPPEND and
        // setting a flag never zeroes the permissions.
        if ((update.Perm is null) != (update.Flags is null))
        {
            Rstat now = await _session.Messages
                .StatAsync(new Tstat(0, Fid), cancellationToken).ConfigureAwait(false);
            update = AttrProjector.CompleteMode(update, now.Stat, _session.Dialect);
        }

        StatRecord record = AttrProjector.ToWstat(update, _session.Dialect);
        await _session.Messages
            .WstatAsync(new Twstat(0, Fid, record), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes the file this fid names; the fid is freed even when the remove fails.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the server answered.</returns>
    /// <exception cref="NinePException">The server refused the removal.</exception>
    public async ValueTask RemoveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(!BeginClose(), this);
        try
        {
            await _drained.Task.ConfigureAwait(false);
            await _session.Messages
                .RemoveAsync(new Tremove(0, Fid), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // remove(5): the fid is gone whatever the reply says, so it is dropped here rather
            // than clunked — a Tclunk on it would be a use of a fid the server has forgotten.
            FinishClose();
        }
    }

    /// <summary>
    /// Flushes the file to stable storage: <c>Tfsync</c> in .L, and in 9P2000 and .u the
    /// all-don't-touch <c>Twstat</c> that stat(5) defines as exactly that request.
    /// </summary>
    /// <param name="dataOnly">
    /// True to commit data without metadata. Only <c>.L</c> has the distinction — <c>Tfsync</c>
    /// carries <c>datasync</c>, the all-don't-touch <c>Twstat</c> does not — so in 9P2000 and .u a
    /// data-only sync goes out as the full sync. That is not a dropped field: a full sync commits
    /// the data as well, so the weaker request is satisfied by the stronger one and nothing the
    /// caller asked for is left undone (reference §8 rule 16).
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the server confirmed.</returns>
    public async ValueTask FsyncAsync(
        bool dataOnly = false, CancellationToken cancellationToken = default)
    {
        using OperationLease operation = AcquireOperation();
        if (_session.Dialect == Dialect.P9_2000_L)
        {
            await _session.Messages
                .FsyncAsync(new Tfsync(0, Fid, dataOnly ? 1u : 0u), cancellationToken).ConfigureAwait(false);
            return;
        }

        await _session.Messages
            .WstatAsync(new Twstat(0, Fid, StatRecord.DontTouch), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Acquires or releases a byte-range lock (.L only).</summary>
    /// <param name="request">The range, type and owner.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the lock was granted, blocked, refused or in grace.</returns>
    /// <exception cref="NinePException">The session is not .L.</exception>
    public async ValueTask<LockStatus> LockAsync(
        LockRequest request, CancellationToken cancellationToken = default)
    {
        using OperationLease operation = AcquireOperation();
        RequireLinux();

        Rlock reply = await _session.Messages
            .LockAsync(new Tlock(0, Fid, request), cancellationToken).ConfigureAwait(false);

        return reply.Status;
    }

    /// <summary>Tests a byte-range lock (.L only).</summary>
    /// <param name="request">The range and type the caller would like to take.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The conflicting lock, or <see cref="LockType.Unlock"/> when there is none.</returns>
    /// <exception cref="NinePException">The session is not .L.</exception>
    public async ValueTask<LockQueryResult> GetLockAsync(
        LockRequest request, CancellationToken cancellationToken = default)
    {
        using OperationLease operation = AcquireOperation();
        RequireLinux();

        Rgetlock reply = await _session.Messages
            .GetlockAsync(
                new Tgetlock(0, Fid, request.Type, request.Start, request.Length, request.ProcId, request.ClientId),
                cancellationToken)
            .ConfigureAwait(false);

        return reply.Result;
    }

    /// <summary>Reads one extended attribute; an empty name returns the packed name list (.L only).</summary>
    /// <param name="name">The attribute's name, or "" for the NUL-separated list of names.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The attribute's bytes.</returns>
    /// <exception cref="NinePException">The session is not .L.</exception>
    public async ValueTask<byte[]> GetXattrAsync(
        string name, CancellationToken cancellationToken = default)
    {
        using OperationLease operation = AcquireOperation();
        ArgumentNullException.ThrowIfNull(name);
        RequireLinux();

        uint attribute = _session.RentFid();
        NinePFid handle;

        try
        {
            Rxattrwalk reply = await _session.Messages
                .XattrwalkAsync(new Txattrwalk(0, Fid, attribute, name), cancellationToken).ConfigureAwait(false);

            handle = new NinePFid(_session, attribute, Qid) { _attributeSize = reply.Size };
            _session.Track(handle);
        }
        catch
        {
            _session.ReturnFid(attribute);
            throw;
        }

        await using (handle.ConfigureAwait(false))
        {
            // Rxattrwalk reports the size, but the read still stops at the server's own end of
            // data: a value that shrank between the walk and the read is short, not a failure.
            return await handle.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes one extended attribute (.L only).</summary>
    /// <param name="name">The attribute's name.</param>
    /// <param name="value">The bytes to store.</param>
    /// <param name="flags">Whether the attribute must or must not already exist.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the value has been committed.</returns>
    /// <remarks>The final clunk commits the attribute; its refusal, timeout or cancellation is propagated.</remarks>
    /// <exception cref="NinePException">The session is not .L.</exception>
    public async ValueTask SetXattrAsync(
        string name,
        ReadOnlyMemory<byte> value,
        XattrFlags flags = XattrFlags.None,
        CancellationToken cancellationToken = default)
    {
        using OperationLease operation = AcquireOperation();
        RequireName(name);
        RequireLinux();

        // Txattrcreate turns the fid it names into the attribute's sink, so it goes to a clone:
        // the caller's fid must still refer to the file after the attribute has been written.
        NinePFid sink = await CloneCoreAsync(cancellationToken).ConfigureAwait(false);

        await using (sink.ConfigureAwait(false))
        {
            using (sink.AcquireOperation())
            {
                await _session.Messages
                    .XattrcreateAsync(
                        new Txattrcreate(0, sink.Fid, name, (ulong)value.Length, flags), cancellationToken)
                    .ConfigureAwait(false);
            }

            await sink.WriteAllAsync(value, cancellationToken).ConfigureAwait(false);
            await sink.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Clunks the fid. Idempotent, and safe to call from a <c>finally</c>: a refusal, a silent
    /// peer or a session that is already going away is swallowed, because a disposal that throws
    /// out of a cleanup path replaces the exception the caller was told about.
    /// </summary>
    /// <returns>A task that completes when the <c>Rclunk</c> has arrived.</returns>
    public async ValueTask DisposeAsync()
    {
        if (!BeginClose())
        {
            await _closed.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await _drained.Task.ConfigureAwait(false);
            // The clunk goes through the same multiplexer as every other request, so it is
            // serialised behind whatever is already being written rather than racing it.
            await _session.Messages
                .ClunkAsync(new Tclunk(0, Fid), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is NinePException or ObjectDisposedException
            or TimeoutException or OperationCanceledException)
        {
            // clunk(5): the fid is gone whatever the reply says, so a refusal changes nothing
            // this side has to undo. The number goes back to the pool below either way. A silent
            // peer -- which answers neither the Tclunk nor the Tflush that cancels it, and so
            // costs a TimeoutException after two request timeouts -- is the same kind of news:
            // this method is documented as safe to call from a finally, and a dispose that
            // throws there replaces whatever was actually being reported.
        }
        finally
        {
            FinishClose();
        }
    }

    private async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(!BeginClose(), this);
        try
        {
            await _drained.Task.ConfigureAwait(false);
            await _session.Messages.ClunkAsync(new Tclunk(0, Fid), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            FinishClose();
        }
    }

    /// <summary>Records what an open or create reply reported about this fid.</summary>
    /// <param name="qid">The qid the server answered with.</param>
    /// <param name="iounit">The iounit the server answered with; 0 means "the session maximum".</param>
    /// <param name="flags">The requested open flags.</param>
    internal void MarkOpened(Qid qid, uint iounit, OpenFlags flags = OpenFlags.None)
    {
        Qid = qid;
        IsOpen = true;
        _append = (flags & OpenFlags.Append) != 0 || (qid.Type & QidType.QTAPPEND) != 0;
        Iounit = iounit == 0 ? _session.MaxPayload : (int)Math.Min(iounit, (uint)_session.MaxPayload);
    }

    private OperationLease AcquireOperation()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _activeOperations++;
            return new OperationLease(this);
        }
    }

    private void EndOperation()
    {
        lock (_lifetimeGate)
        {
            _activeOperations--;
            if (_disposed != 0 && _activeOperations == 0)
            {
                _drained.TrySetResult();
            }
        }
    }

    private bool BeginClose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed != 0)
            {
                return false;
            }

            Volatile.Write(ref _disposed, 1);
            if (_activeOperations == 0)
            {
                _drained.TrySetResult();
            }

            return true;
        }
    }

    private void FinishClose()
    {
        _session.Release(this);
        _closed.TrySetResult();
    }

    private readonly struct OperationLease(NinePFid owner) : IDisposable
    {
        public void Dispose() => owner.EndOperation();
    }

    private void RequireName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        RequestNameLimit.ValidateName(name, _session.Options.Limits.MaxNameLength);

        if (!NinePText.IsLegalName(name, allowParent: false))
        {
            throw new NinePProtocolException(
                ProtocolErrorKind.Name,
                string.Format(CultureInfo.InvariantCulture, "'{0}' is not a legal name", UntrustedText.Sanitize(name)));
        }
    }

    /// <summary>
    /// Refuses a create of something <c>Tcreate</c> cannot carry here. A symlink's target and a
    /// device's numbers travel in the <c>.u</c> extension field, which this create does not send,
    /// so asking for one is refused rather than sent with the payload missing (reference §8
    /// rule 15).
    /// </summary>
    /// <param name="kind">What the caller asked to create.</param>
    /// <exception cref="NinePException">The kind is neither a regular file nor a directory.</exception>
    private static void RequireCreatable(FileKind kind)
    {
        if (kind is not (FileKind.File or FileKind.Directory))
        {
            throw new NinePException(new NinePError(
                "a create through a fid makes a regular file or a directory; a symlink is SymlinkAsync and a device is MknodAsync",
                (int)Errno.EOPNOTSUPP));
        }
    }

    /// <summary>
    /// Refuses a permission value carrying a bit outside the <c>07777</c> mask, which can only
    /// reach here through a cast. Masking it away would answer success for a create the caller
    /// did not ask for.
    /// </summary>
    /// <param name="perm">The permission value.</param>
    /// <exception cref="NinePException">The value carries a bit that is not a permission.</exception>
    private static void RequirePermissions(FilePermissions perm)
    {
        if ((perm & ~FilePermissions.Mask) != FilePermissions.None)
        {
            throw new NinePException(new NinePError(
                "a create carries the 07777 permission bits and nothing else", (int)Errno.EINVAL));
        }
    }

    /// <summary>
    /// Refuses <see cref="FileFlags.Auth"/> or <see cref="FileFlags.Mount"/> on a create: those
    /// are the server's own and stat(5) lets a client set only the other three (reference §8
    /// rule 19).
    /// </summary>
    /// <param name="fileFlags">The flags asked for.</param>
    /// <exception cref="NinePException">A server-owned flag was asked for.</exception>
    private static void RequireSettableFlags(FileFlags fileFlags)
    {
        if ((fileFlags & ~AttrProjector.SettableFlags) != FileFlags.None)
        {
            throw new NinePException(new NinePError(
                "a create cannot ask for DMAUTH or DMMOUNT", (int)Errno.EPERM));
        }
    }

    /// <summary>The <c>Tcreate.perm</c> word: the permission bits, DMDIR, and the file flags.</summary>
    /// <param name="kind">What is being created.</param>
    /// <param name="perm">The permission bits.</param>
    /// <param name="fileFlags">The file flags the new file is to carry.</param>
    /// <returns>The word to send.</returns>
    private static uint CreatePerm(FileKind kind, FilePermissions perm, FileFlags fileFlags) =>
        (uint)perm
        | (kind == FileKind.Directory ? ModeBits.DMDIR : 0)
        | AttrProjector.HighFlagBits(fileFlags);

    private static Qid Landing(in Rwalk reply, string[] step, Qid current)
    {
        // Reference §8 rule 13: more qids than names is a protocol error, not a partial walk.
        if (reply.Wqids.Count > step.Length)
        {
            throw new NinePProtocolException(
                ProtocolErrorKind.NWName, "the server answered a Twalk with more qids than names");
        }

        // walk(5): fewer qids than names means the walk stopped early and newfid was not bound.
        if (reply.Wqids.Count < step.Length)
        {
            Qid last = reply.Wqids.Count == 0 ? current : reply.Wqids[^1];
            throw new NinePException(NinePError.FromErrno(
                last.Type.HasFlag(QidType.QTDIR) ? Errno.ENOENT : Errno.ENOTDIR));
        }

        return reply.Wqids.Count == 0 ? current : reply.Wqids[^1];
    }

    private void RequireLinux()
    {
        if (_session.Dialect != Dialect.P9_2000_L)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EOPNOTSUPP));
        }
    }

    private async IAsyncEnumerable<DirEntry> ReadDirentsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ulong cookie = 0;

        while (true)
        {
            uint count = (uint)Iounit;
            Rreaddir reply;
            using (AcquireOperation())
            {
                reply = await _session.Messages
                    .ReaddirAsync(new Treaddir(0, Fid, cookie, count), cancellationToken).ConfigureAwait(false);
            }

            if (reply.Data.Length > count)
            {
                throw new NinePProtocolException(
                    ProtocolErrorKind.Bounds, "the server answered a Treaddir with more bytes than asked for");
            }

            // Reference §4.3: count == 0 ends the listing.
            if (reply.Data.IsEmpty)
            {
                yield break;
            }

            // A trailing partial record is a protocol error (reference §8 rule 13); the reader
            // reports it rather than silently dropping the tail.
            if (!DirEntryCodec.TryReadAll(reply.Data, out IReadOnlyList<DirEntry> entries, out ProtocolErrorKind failure))
            {
                throw new NinePProtocolException(failure, "the server split a directory entry across replies");
            }

            if (entries.Count > 0 && entries[^1].Cursor == cookie)
            {
                throw new NinePProtocolException(ProtocolErrorKind.Bounds, "directory cookie did not advance");
            }

            foreach (DirEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cookie = entry.Cursor;
                if (!IsDotEntry(entry.Name))
                {
                    yield return entry;
                }
            }
        }
    }

    private async IAsyncEnumerable<DirEntry> ReadStatRecordsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ulong offset = 0;

        while (true)
        {
            uint count = (uint)Iounit;
            Rread reply;
            using (AcquireOperation())
            {
                reply = await _session.Messages
                    .ReadAsync(new Tread(0, Fid, offset, count), cancellationToken).ConfigureAwait(false);
            }

            if (reply.Data.Length > count)
            {
                throw new NinePProtocolException(
                    ProtocolErrorKind.Bounds, "the server answered a Tread with more bytes than asked for");
            }

            if (reply.Data.IsEmpty)
            {
                yield break;
            }

            // read(5): the next offset is this one plus this count, and the reply holds a whole
            // number of stat records — a split record is a protocol error, not a short read.
            offset += (ulong)reply.Data.Length;

            foreach (DirEntry entry in ReadRecords(reply.Data, _session.Dialect, offset))
            {
                if (!IsDotEntry(entry.Name))
                {
                    yield return entry;
                }
            }
        }
    }

    private static bool IsDotEntry(string name) =>
        name is "." or "..";

    private static List<DirEntry> ReadRecords(ReadOnlyMemory<byte> payload, Dialect dialect, ulong cursor)
    {
        List<DirEntry> entries = [];
        WireReader reader = new(payload);

        while (reader.Remaining > 0)
        {
            StatRecord record = StatCodec.ReadRecord(ref reader, dialect);
            if (reader.Failed)
            {
                throw new NinePProtocolException(
                    reader.Failure, "the server packed a malformed stat record into a directory read");
            }

            Attr attr = AttrProjector.FromStat(in record, dialect);
            entries.Add(new DirEntry(record.Name, record.Qid, attr.Kind, cursor));
        }

        return entries;
    }
}
