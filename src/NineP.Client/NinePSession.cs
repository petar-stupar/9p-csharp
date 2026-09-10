using System.Collections.Concurrent;
using NineP.Client.Internal;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;

namespace NineP.Client;

// RS0026: §5.7 fixes both AttachAsync overloads. The ambiguity the rule guards against cannot
// arise: one takes no positional argument at all and the other takes two required strings, so no
// call site can bind to both.
#pragma warning disable RS0026

/// <summary>
/// A negotiated 9P session: the tag multiplexer, the attach root and the high-level file API.
/// Every request is pipelined — the session never waits for one reply before writing the next —
/// and every fid it hands out is released deterministically, by disposal rather than by collection.
/// </summary>
public sealed class NinePSession : IAsyncDisposable
{
    private readonly TagMultiplexer _multiplexer;
    private readonly INinePConnection _connection;
    private readonly ClientOptions _options;
    private readonly ConcurrentQueue<uint> _freeFids = new();
    private readonly ConcurrentDictionary<uint, NinePFid> _live = new();
    private long _nextFid;
    private NinePFid? _root;
    private int _disposed;

    internal NinePSession(
        INinePConnection connection, TagMultiplexer multiplexer, Dialect dialect, uint msize, ClientOptions options)
    {
        _connection = connection;
        _multiplexer = multiplexer;
        _options = options;
        Dialect = dialect;
        Msize = msize;
        Messages = new MessageApi(multiplexer, options);
    }

    /// <summary>The dialect that was negotiated.</summary>
    public Dialect Dialect { get; }

    /// <summary>The msize both sides agreed on.</summary>
    public uint Msize { get; }

    /// <summary>The largest payload one <c>Tread</c> or <c>Twrite</c> carries: msize - IOHDRSZ.</summary>
    public int MaxPayload => (int)Msize - Constants.IOHDRSZ;

    /// <summary>The root the first attach on this session bound, whichever overload made it.</summary>
    /// <exception cref="InvalidOperationException">Nothing has attached on this session yet.</exception>
    public NinePFid Root =>
        _root ?? throw new InvalidOperationException("the session has not attached yet; call AttachAsync first");

    /// <summary>The typed one-method-per-T-message API, for callers that want the wire directly.</summary>
    public INinePMessages Messages { get; }

    /// <summary>The bounds and the clock this session was configured with.</summary>
    internal ClientOptions Options => _options;

    /// <summary>The multiplexer, so the fid and transfer layers can share the one writer.</summary>
    internal TagMultiplexer Multiplexer => _multiplexer;

    /// <summary>Fids this session has handed out and not yet clunked.</summary>
    internal int LiveFids => _live.Count;

    /// <summary>
    /// Runs the afid exchange from <see cref="ClientOptions.Credential"/>, attaches, and returns
    /// the root fid, which is also published as <see cref="Root"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The root fid; the session clunks it on disposal.</returns>
    /// <exception cref="NinePException">The server refused the auth or the attach.</exception>
    public ValueTask<NinePFid> AttachAsync(CancellationToken cancellationToken = default) =>
        AttachAsync(_options.Uname, _options.Aname, _options.Credential, cancellationToken);

    /// <summary>
    /// Attaches a tree, and publishes the fid as <see cref="Root"/> if this session has none yet.
    /// A second attach on the same session is a second tree or a second user and leaves the first
    /// root where it is; the exchange below is what makes both true of the one method.
    /// </summary>
    /// <param name="uname">The user name to claim; the identity still comes from the authenticator.</param>
    /// <param name="aname">The tree to attach to.</param>
    /// <param name="credential">The credential to run over an afid; null attaches with NOFID.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The fid bound to the root of that tree; the caller disposes it.</returns>
    /// <exception cref="NinePException">The server refused the auth or the attach.</exception>
    public async ValueTask<NinePFid> AttachAsync(
        string uname,
        string aname,
        ICredential? credential = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uname);
        ArgumentNullException.ThrowIfNull(aname);

        uint afid = credential is null
            ? Constants.NOFID
            : await RunAuthAsync(uname, aname, credential, cancellationToken).ConfigureAwait(false);

        try
        {
            uint fid = RentFid();
            try
            {
                Rattach reply = await Messages
                    .AttachAsync(new Tattach(0, fid, afid, uname, aname, _options.NUname), cancellationToken)
                    .ConfigureAwait(false);

                NinePFid handle = new(this, fid, reply.Qid);
                Track(handle);

                // Every attach publishes the root, not only the one whose arguments came from the
                // options: a caller that fell back to a NOFID attach after a refused Tauth — which
                // is what the cli's --auth-optional does — has attached this session just as much,
                // and leaving Root null there made the next walk throw.
                Interlocked.CompareExchange(ref _root, handle, null);
                return handle;
            }
            catch
            {
                ReturnFid(fid);
                throw;
            }
        }
        finally
        {
            if (afid != Constants.NOFID)
            {
                // attach(5): the afid may be clunked once the attach has been answered, and a
                // client that left it open would hold an authentication file per session.
                await ClunkQuietlyAsync(afid).ConfigureAwait(false);
            }
        }
    }


    /// <summary>Walks a slash-separated path from the root and returns the fid.</summary>
    /// <param name="path">The path, for example "a/b/c"; "" and "/" name the root.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The fid the path names; the caller disposes it.</returns>
    /// <exception cref="NinePException">The path did not resolve.</exception>
    public ValueTask<NinePFid> WalkAsync(string path, CancellationToken cancellationToken = default) =>
        Root.WalkAsync(Split(path), cancellationToken);

    /// <summary>Opens a file by path and returns an open fid.</summary>
    /// <param name="path">The path to open.</param>
    /// <param name="mode">The access mode.</param>
    /// <param name="flags">Flags that modify the open.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open fid; the caller disposes it.</returns>
    /// <exception cref="NinePException">The path did not resolve, or the open was refused.</exception>
    public async ValueTask<NinePFid> OpenFileAsync(
        string path,
        OpenMode mode,
        OpenFlags flags = OpenFlags.None,
        CancellationToken cancellationToken = default)
    {
        NinePFid fid = await WalkAsync(path, cancellationToken).ConfigureAwait(false);

        try
        {
            await fid.OpenAsync(mode, flags, cancellationToken).ConfigureAwait(false);
            return fid;
        }
        catch
        {
            await fid.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reads a whole file, chunked at the payload maximum with the in-flight window.</summary>
    /// <param name="path">The path to read.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>Everything the file held.</returns>
    public async ValueTask<byte[]> ReadFileAsync(
        string path, CancellationToken cancellationToken = default)
    {
        NinePFid fid = await OpenFileAsync(path, OpenMode.Read, OpenFlags.None, cancellationToken)
            .ConfigureAwait(false);

        await using (fid.ConfigureAwait(false))
        {
            return await fid.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Replaces a file's contents, truncating first.</summary>
    /// <param name="path">The path to write.</param>
    /// <param name="data">The new contents.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>A task that completes when every byte has been acknowledged.</returns>
    public async ValueTask WriteFileAsync(
        string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        NinePFid fid = await OpenFileAsync(path, OpenMode.Write, OpenFlags.Truncate, cancellationToken)
            .ConfigureAwait(false);

        await using (fid.ConfigureAwait(false))
        {
            await fid.WriteAllAsync(data, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Lists a directory as unified entries, in both dialect record formats.</summary>
    /// <param name="path">The directory to list.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The entries, in the order the server packed them.</returns>
    public async ValueTask<IReadOnlyList<DirEntry>> ReadDirAsync(
        string path, CancellationToken cancellationToken = default)
    {
        NinePFid fid = await OpenFileAsync(path, OpenMode.Read, OpenFlags.None, cancellationToken)
            .ConfigureAwait(false);

        await using (fid.ConfigureAwait(false))
        {
            List<DirEntry> entries = [];
            await foreach (DirEntry entry in fid.ReadDirAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(entry);
            }

            return entries;
        }
    }

    /// <summary>Creates a directory.</summary>
    /// <param name="path">The directory to create; its parent must exist.</param>
    /// <param name="perm">The permission bits.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the directory exists.</returns>
    public async ValueTask MkdirAsync(
        string path, uint perm = 0x1ED, CancellationToken cancellationToken = default)
    {
        (string[] parent, string name) = SplitParent(path);
        NinePFid directory = await Root.WalkAsync(parent, cancellationToken).ConfigureAwait(false);

        await using (directory.ConfigureAwait(false))
        {
            if (Dialect == Dialect.P9_2000_L)
            {
                await Messages
                    .MkdirAsync(new Tmkdir(0, directory.Fid, name, perm, _options.NUname), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            // 9P2000 and .u have no Tmkdir: DMDIR in the create permission is what makes a
            // directory, and open(5) then requires the mode to be OREAD.
            await directory
                .CreateAsync(name, perm | 0x80000000u, OpenMode.Read, OpenFlags.None, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Creates an empty regular file and returns it open for writing.</summary>
    /// <param name="path">The file to create; its parent must exist.</param>
    /// <param name="perm">The permission bits.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The new file, open for writing; the caller disposes it.</returns>
    public async ValueTask<NinePFid> CreateFileAsync(
        string path, uint perm = 0x1A4, CancellationToken cancellationToken = default)
    {
        (string[] parent, string name) = SplitParent(path);
        NinePFid fid = await Root.WalkAsync(parent, cancellationToken).ConfigureAwait(false);

        try
        {
            // Tcreate turns the directory fid into the new, open file, so the walk above is
            // already the fid the caller gets back.
            await fid.CreateAsync(name, perm, OpenMode.Write, OpenFlags.None, cancellationToken)
                .ConfigureAwait(false);
            return fid;
        }
        catch
        {
            await fid.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Removes a file or an empty directory.</summary>
    /// <param name="path">The path to remove.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the server answered.</returns>
    public async ValueTask RemoveAsync(string path, CancellationToken cancellationToken = default)
    {
        NinePFid fid = await WalkAsync(path, cancellationToken).ConfigureAwait(false);
        await fid.RemoveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renames or moves a path: <c>Trenameat</c> in .L, <c>Twstat.name</c> otherwise.</summary>
    /// <param name="oldPath">The path as it is now.</param>
    /// <param name="newPath">The path it should have.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the server answered.</returns>
    /// <exception cref="NinePException">A 9P2000 or .u session was asked to move across directories.</exception>
    public async ValueTask RenameAsync(
        string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        (string[] oldParent, string oldName) = SplitParent(oldPath);
        (string[] newParent, string newName) = SplitParent(newPath);

        if (Dialect != Dialect.P9_2000_L)
        {
            // stat(5): a wstat renames within one directory and cannot move a file, so a move is
            // refused rather than performed as a rename that silently ignored the destination.
            if (!oldParent.SequenceEqual(newParent, StringComparer.Ordinal))
            {
                throw new NinePException(NinePError.FromEname("cannot rename across directories"));
            }

            NinePFid target = await WalkAsync(oldPath, cancellationToken).ConfigureAwait(false);
            await using (target.ConfigureAwait(false))
            {
                await target.SetAttrAsync(new SetAttr { Name = newName }, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        NinePFid source = await Root.WalkAsync(oldParent, cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            NinePFid destination = await Root.WalkAsync(newParent, cancellationToken).ConfigureAwait(false);
            await using (destination.ConfigureAwait(false))
            {
                try
                {
                    await Messages
                        .RenameatAsync(
                            new Trenameat(0, source.Fid, oldName, destination.Fid, newName), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (NinePException refused) when (refused.Error.Errno == Errno.EOPNOTSUPP)
                {
                    // diod, the most deployed 9P2000.L server, implements Trename and not
                    // Trenameat and answers the latter EOPNOTSUPP (measured against 1.0.24, see
                    // docs/interop.md). Linux v9fs falls back the same way, so a client that did
                    // not would fail a rename every v9fs mount performs. The fallback names the
                    // file by a fid of its own and the destination directory by the fid already
                    // held; it is attempted once, and any other refusal is the caller's.
                    NinePFid file = await source.WalkAsync([oldName], cancellationToken).ConfigureAwait(false);
                    await using (file.ConfigureAwait(false))
                    {
                        await Messages
                            .RenameAsync(new Trename(0, file.Fid, destination.Fid, newName), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
        }
    }

    /// <summary>Fetches the unified attributes of a path.</summary>
    /// <param name="path">The path to stat.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The attributes.</returns>
    public async ValueTask<Attr> GetAttrAsync(string path, CancellationToken cancellationToken = default)
    {
        NinePFid fid = await WalkAsync(path, cancellationToken).ConfigureAwait(false);

        await using (fid.ConfigureAwait(false))
        {
            return await fid.GetAttrAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Applies a partial attribute update to a path.</summary>
    /// <param name="path">The path to change.</param>
    /// <param name="update">The fields to change.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the server answered.</returns>
    public async ValueTask SetAttrAsync(
        string path, SetAttr update, CancellationToken cancellationToken = default)
    {
        NinePFid fid = await WalkAsync(path, cancellationToken).ConfigureAwait(false);

        await using (fid.ConfigureAwait(false))
        {
            await fid.SetAttrAsync(update, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Creates a symbolic link (.L only).</summary>
    /// <param name="path">The link to create.</param>
    /// <param name="target">The text the link points at.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the link exists.</returns>
    /// <exception cref="NinePException">The session is not .L.</exception>
    public async ValueTask SymlinkAsync(
        string path, string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (Dialect != Dialect.P9_2000_L)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EOPNOTSUPP));
        }

        (string[] parent, string name) = SplitParent(path);
        NinePFid directory = await Root.WalkAsync(parent, cancellationToken).ConfigureAwait(false);

        await using (directory.ConfigureAwait(false))
        {
            await Messages
                .SymlinkAsync(
                    new Tsymlink(0, directory.Fid, name, target, _options.NUname), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Reads a symbolic link's target: <c>Treadlink</c> in .L, the stat extension in .u.</summary>
    /// <param name="path">The link to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The text the link points at.</returns>
    /// <exception cref="NinePException">The session is 9P2000, which cannot represent a link.</exception>
    public async ValueTask<string> ReadlinkAsync(
        string path, CancellationToken cancellationToken = default)
    {
        if (Dialect == Dialect.P9_2000)
        {
            throw new NinePException(NinePError.FromEname("symlinks not supported"));
        }

        NinePFid fid = await WalkAsync(path, cancellationToken).ConfigureAwait(false);

        await using (fid.ConfigureAwait(false))
        {
            if (Dialect == Dialect.P9_2000_L)
            {
                Rreadlink reply = await Messages
                    .ReadlinkAsync(new Treadlink(0, fid.Fid), cancellationToken).ConfigureAwait(false);

                return reply.Target;
            }

            Attr attr = await fid.GetAttrAsync(cancellationToken).ConfigureAwait(false);
            return attr.Kind == FileKind.Symlink && attr.SymlinkTarget is { } target
                ? target
                : throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
        }
    }

    /// <summary>Filesystem statistics (.L only).</summary>
    /// <param name="path">Any path on the filesystem being asked about.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The statistics.</returns>
    /// <exception cref="NinePException">The session is not .L.</exception>
    public async ValueTask<StatFs> StatFsAsync(
        string path, CancellationToken cancellationToken = default)
    {
        if (Dialect != Dialect.P9_2000_L)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EOPNOTSUPP));
        }

        NinePFid fid = await WalkAsync(path, cancellationToken).ConfigureAwait(false);

        await using (fid.ConfigureAwait(false))
        {
            Rstatfs reply = await Messages
                .StatfsAsync(new Tstatfs(0, fid.Fid), cancellationToken).ConfigureAwait(false);

            return reply.Stat;
        }
    }

    /// <summary>
    /// Clunks every outstanding fid and closes the transport. The clunks go out <b>together</b>
    /// and share one deadline — <see cref="ClientOptions.DisposeTimeout"/>, or
    /// <see cref="ClientOptions.RequestTimeout"/> when that is shorter — rather than being tried
    /// one after another. Sequentially, a peer that is connected but silent costs
    /// <c>2 × RequestTimeout</c> per fid (the clunk, then the <c>Tflush</c> that cancels it), so
    /// disposal grew with the number of open fids: twelve minutes at the defaults with five fids.
    /// Whatever the peer has not answered when the deadline passes is abandoned, which loses
    /// nothing: the transport close below makes the server forget every fid on the connection.
    /// </summary>
    /// <returns>A task that completes when the connection has closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task clunking = Task.WhenAll([.. _live.Values.Select(ClunkQuietlyAsync)]);

        try
        {
            TimeSpan bound = DisposeBound();

            if (bound > TimeSpan.Zero)
            {
                await clunking.WaitAsync(bound).ConfigureAwait(false);
            }
            else
            {
                await clunking.ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            // The peer stopped answering. The clunks that are still in flight are observed by
            // the continuation below, so their failures do not surface as unobserved exceptions
            // once the multiplexer faults them.
            _ = clunking.ContinueWith(
                static abandoned => _ = abandoned.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        finally
        {
            // The reader task and the socket are released whatever the clunks did. Reaching this
            // only on the happy path leaked both against a server that had stopped answering.
            await _multiplexer.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How long disposal may spend on the clunks in total: the smaller of the two configured
    /// deadlines, or none at all when both have been opted out of. It is also the bound
    /// <see cref="NinePFid.WalkAsync"/> puts on the cleanup clunk of a walk that failed, which is
    /// the same situation seen from one fid rather than all of them: a clunk nobody is waiting on,
    /// against a peer that may not answer.
    /// </summary>
    /// <returns>The bound, or <see cref="TimeSpan.Zero"/> for "wait as long as it takes".</returns>
    internal TimeSpan DisposeBound()
    {
        TimeSpan dispose = _options.DisposeTimeout;
        TimeSpan request = _options.RequestTimeout;

        return dispose <= TimeSpan.Zero ? request
            : request <= TimeSpan.Zero ? dispose
            : dispose < request ? dispose : request;
    }

    /// <summary>Clunks one fid, swallowing what a connection that is going away has to say.</summary>
    /// <param name="fid">The fid to clunk.</param>
    /// <returns>A task that completes when the clunk has been answered or given up on.</returns>
    private static async Task ClunkQuietlyAsync(NinePFid fid)
    {
        try
        {
            await fid.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is NinePException or IOException
            or ObjectDisposedException or InvalidOperationException or TimeoutException
            or OperationCanceledException)
        {
            // The connection is going away; a fid the server will forget anyway is not news, and
            // one clunk's failure must not stop the others or the socket being closed. The list
            // covers a socket that broke under the writer as well as a session already declared
            // terminated: the send path rethrows what the transport raised without wrapping it, so
            // an IOException from a real socket -- or an InvalidOperationException from a pipe
            // whose writer was completed -- arrives here unchanged, and only a NinePException does
            // when the reader noticed the death first. Catching the second but not the first made
            // disposal throw or not depending on which of the two won a race.
        }
    }

    /// <summary>Takes a fid number the server does not already know about.</summary>
    /// <returns>The number to use.</returns>
    /// <exception cref="NinePProtocolException">Every fid number is in use.</exception>
    internal uint RentFid()
    {
        if (_freeFids.TryDequeue(out uint reused))
        {
            return reused;
        }

        long next = Interlocked.Increment(ref _nextFid) - 1;
        return next < Constants.NOFID
            ? (uint)next
            : throw new NinePProtocolException(
                ProtocolErrorKind.Overflow, "every fid number on this connection is in use");
    }

    /// <summary>Gives a fid number back, so a later request may use it.</summary>
    /// <param name="fid">The number to release.</param>
    internal void ReturnFid(uint fid)
    {
        _live.TryRemove(fid, out _);
        _freeFids.Enqueue(fid);
    }

    /// <summary>Registers a fid this session handed out, so disposal can clunk it.</summary>
    /// <param name="fid">The fid to track.</param>
    internal void Track(NinePFid fid)
    {
        ArgumentNullException.ThrowIfNull(fid);

        _live[fid.Fid] = fid;
    }

    /// <summary>Forgets a fid the server has freed.</summary>
    /// <param name="fid">The fid that is gone.</param>
    internal void Release(NinePFid fid)
    {
        ArgumentNullException.ThrowIfNull(fid);

        ReturnFid(fid.Fid);
    }

    private async ValueTask<uint> RunAuthAsync(
        string uname, string aname, ICredential credential, CancellationToken cancellationToken)
    {
        uint afid = RentFid();

        try
        {
            await Messages
                .AuthAsync(new Tauth(0, afid, uname, aname, _options.NUname), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // A refusal reaches the caller as the server's own error. Falling back to a NOFID
            // attach here would silently downgrade an authenticated session to an anonymous one;
            // a caller who wants that asks for it by configuring no credential at all.
            ReturnFid(afid);
            throw;
        }

        using CancellationTokenSource budget =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.Limits.AuthTimeout > TimeSpan.Zero)
        {
            budget.CancelAfter(_options.Limits.AuthTimeout);
        }

        try
        {
            await credential
                .AuthenticateAsync(new AuthChannel(this, afid, _options.Limits), budget.Token)
                .ConfigureAwait(false);
            return afid;
        }
        catch
        {
            await ClunkQuietlyAsync(afid).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask ClunkQuietlyAsync(uint fid)
    {
        try
        {
            await Messages.ClunkAsync(new Tclunk(0, fid), CancellationToken.None).ConfigureAwait(false);
        }
        catch (NinePException)
        {
            // clunk(5): the fid is gone whatever the reply says.
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException
            or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            // The same dead connection the fid overload above describes, on the afid path. The
            // fid is returned in the finally either way.
        }
        finally
        {
            ReturnFid(fid);
        }
    }

    /// <summary>Splits a slash-separated path into its elements, dropping empty ones.</summary>
    /// <param name="path">The path; "", "/" and "//" all name the root.</param>
    /// <returns>The elements, in order.</returns>
    internal static string[] Split(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Splits a path into the elements of its parent and its final component.</summary>
    /// <param name="path">The path; it must name something inside a directory.</param>
    /// <returns>The parent's elements and the final name.</returns>
    /// <exception cref="ArgumentException">The path names the root, which has no final component.</exception>
    internal static (string[] Parent, string Name) SplitParent(string path)
    {
        string[] elements = Split(path);
        return elements.Length == 0
            ? throw new ArgumentException("the root has no name to create, remove or rename", nameof(path))
            : (elements[..^1], elements[^1]);
    }
}
#pragma warning restore RS0026
