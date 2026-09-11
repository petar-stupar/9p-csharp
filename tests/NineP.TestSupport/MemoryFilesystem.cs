using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Server;

namespace NineP.TestSupport;

/// <summary>
/// An in-memory tree written entirely in terms of the handler model of architecture §4. It is what
/// the server tests serve, and it exists to prove the point of that model: nothing here knows a
/// dialect, a fid, a tag or an error shape, and the same handlers answer 9P2000, .u and .L.
/// </summary>
public sealed class MemoryFilesystem : IFilesystem, IStatFsCapability
{
    private ulong _nextPath = 1;

    /// <summary>Creates a tree with an empty root owned by "glenda".</summary>
    public MemoryFilesystem() => Root = NewDirectory("/", Perms.P0777);

    /// <summary>The root every attach resolves to.</summary>
    public MemoryDirectory Root { get; }

    /// <summary>The identity of the last attach, so a test can assert what the core passed in.</summary>
    public Identity? LastIdentity { get; private set; }

    /// <summary>The tree name of the last attach.</summary>
    public string? LastAname { get; private set; }

    /// <summary>Set to refuse every attach with this error, for the permission tests.</summary>
    public NinePError? RefuseAttach { get; set; }

    /// <summary>Returns the root for this identity and tree name.</summary>
    /// <param name="identity">Who the session runs as.</param>
    /// <param name="aname">The tree the client asked for.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The root directory.</returns>
    /// <exception cref="NinePException">The tree was configured to refuse attaches.</exception>
    public ValueTask<IDirectoryHandler> AttachAsync(
        Identity identity, string aname, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        LastIdentity = identity;
        LastAname = aname;

        return RefuseAttach is NinePError refusal
            ? throw new NinePException(refusal)
            : ValueTask.FromResult<IDirectoryHandler>(Root);
    }

    /// <summary>Filesystem statistics; a synthetic server reports V9FS_MAGIC (reference §4.9).</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The statistics.</returns>
    public ValueTask<StatFs> StatFsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(new StatFs(
            StatFs.V9fsMagic, 4096, 1024, 1024, 1024, 1024, 1024, 1, (uint)Constants.MaxNameLength));
    }

    /// <summary>Creates a directory node belonging to this tree.</summary>
    /// <param name="name">The directory's name.</param>
    /// <param name="perm">Its permission bits.</param>
    /// <returns>The new directory.</returns>
    public MemoryDirectory NewDirectory(string name, FilePermissions perm) =>
        new(name, perm, _nextPath++, this);

    /// <summary>Creates a file node belonging to this tree.</summary>
    /// <param name="name">The file's name.</param>
    /// <param name="perm">Its permission bits.</param>
    /// <returns>The new file.</returns>
    public MemoryFile NewFile(string name, FilePermissions perm) => new(name, perm, _nextPath++);

    /// <summary>Creates a symbolic link belonging to this tree.</summary>
    /// <param name="name">The link's name.</param>
    /// <param name="target">What it points at.</param>
    /// <returns>The new link.</returns>
    public MemorySymlink NewSymlink(string name, string target) => new(name, target, _nextPath++);
}

/// <summary>What every node of a <see cref="MemoryFilesystem"/> has in common.</summary>
public abstract class MemoryNode : IHandler
{
    private ulong _version;

    /// <summary>Creates a node.</summary>
    /// <param name="name">Its name inside its parent.</param>
    /// <param name="kind">What kind of file it is.</param>
    /// <param name="perm">Its permission bits.</param>
    /// <param name="path">Its qid path, unique in the tree.</param>
    protected MemoryNode(string name, FileKind kind, FilePermissions perm, ulong path)
    {
        Name = name;
        Kind = kind;
        Perm = perm;
        Path = path;
    }

    /// <summary>The node's name inside its parent.</summary>
    public string Name { get; set; }

    /// <summary>The directory this node lives in, so a rename can rekey it there.</summary>
    public MemoryDirectory? Parent { get; internal set; }

    /// <summary>What kind of file this is.</summary>
    public FileKind Kind { get; }

    /// <summary>The permission bits; the core checks them before calling a handler.</summary>
    public FilePermissions Perm { get; set; }

    /// <summary>The qid path: this node's identity for the life of the tree.</summary>
    public ulong Path { get; }

    /// <summary>The textual owner.</summary>
    public string Owner { get; set; } = "glenda";

    /// <summary>The textual group.</summary>
    public string Group { get; set; } = "glenda";

    /// <summary>The numeric owner.</summary>
    public uint Uid { get; set; } = 1000;

    /// <summary>The numeric group.</summary>
    public uint Gid { get; set; } = 1000;

    /// <summary>The node's file flags: append-only, exclusive use, temporary.</summary>
    public FileFlags Flags { get; set; }

    /// <summary>True for a DMEXCL file: one open fid at a time across every client.</summary>
    public bool Exclusive
    {
        get => Flags.HasFlag(FileFlags.Exclusive);
        set => Flags = value ? Flags | FileFlags.Exclusive : Flags & ~FileFlags.Exclusive;
    }

    /// <summary>
    /// Set to answer a <see cref="SetAttr"/> that carries <see cref="SetAttr.Flags"/> with
    /// success while leaving the flags alone: the handler reference §8 rule 19 guards against,
    /// one written before the member existed.
    /// </summary>
    public bool DropsFlagUpdates { get; set; }

    /// <summary>
    /// The creation time, which a tree that does not track one leaves at zero. Reference §8 rule
    /// 22 makes that zero "not supplied", so an <c>Rgetattr</c> does not mark <c>btime</c> valid
    /// until a test sets this.
    /// </summary>
    public TimeSpec BTime { get; set; }

    /// <summary>The generation number, zero when this tree does not keep one (rule 22).</summary>
    public ulong Gen { get; set; }

    /// <summary>The data version, zero when this tree does not keep one (rule 22).</summary>
    public ulong DataVersion { get; set; }

    /// <summary>
    /// Set to refuse the clunk of every fid on this node with this error, for the tests of
    /// reference §8 rule 26: the fid is freed regardless and the refusal is the reply.
    /// </summary>
    public NinePError? ClunkFailure { get; set; }

    /// <summary>How many times a handler method was called with an unsupported request.</summary>
    public int Refusals { get; private set; }

    /// <summary>The last update the core handed this node, fsync requests aside.</summary>
    public SetAttr? LastUpdate { get; private set; }

    /// <summary>True once the core has clunked the last fid on this node.</summary>
    public bool WasClunked { get; private set; }

    /// <summary>How many times the core asked this node to reach stable storage.</summary>
    public int Fsyncs { get; private set; }

    /// <summary>The qid the protocol layer projects.</summary>
    public Qid Qid => new(QidTypeOf(Kind), (uint)_version, Path);

    /// <summary>The node's length in bytes; zero for anything without contents.</summary>
    public virtual ulong Size => 0;

    /// <summary>The dialect-neutral attributes the core projects into the session's shape.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The attributes.</returns>
    public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(new Attr
        {
            Qid = Qid,
            Kind = Kind,
            Perm = Perm,
            Flags = Flags,
            NLink = 1,
            UserName = Owner,
            GroupName = Group,
            ModifierName = Owner,
            Uid = Uid,
            Gid = Gid,
            Size = Size,
            BlockSize = 4096,
            Blocks = (Size + 511) / 512,
            ATime = new TimeSpec(1_700_000_000, 0),
            MTime = new TimeSpec(1_700_000_000, 0),
            CTime = new TimeSpec(1_700_000_000, 0),
            BTime = BTime,
            Gen = Gen,
            DataVersion = DataVersion,
            SymlinkTarget = (this as MemorySymlink)?.Target,
        });
    }

    /// <summary>Applies a partial update; the fields this tree cannot change are refused.</summary>
    /// <param name="update">The fields to change.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the change has been made.</returns>
    /// <exception cref="NinePException">The update names something this node cannot change.</exception>
    public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();

        if (update.IsFsyncRequest)
        {
            Fsyncs++;
            return ValueTask.CompletedTask;
        }

        if (update.Size is > int.MaxValue)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EFBIG));
        }
        LastUpdate = update;

        // stat(5): a wstat is atomic, so everything is validated before anything is applied.
        if (update.Size is not null && Kind == FileKind.Directory)
        {
            Refusals++;
            throw new NinePException(NinePError.FromErrno(Errno.EISDIR));
        }

        if (update.Perm is FilePermissions perm)
        {
            Perm = perm;
        }

        if (update.Flags is FileFlags flags && !DropsFlagUpdates)
        {
            Flags = flags;
        }

        if (update.Gid is uint gid)
        {
            Gid = gid;
        }

        if (update.GroupName is { } group)
        {
            Group = group;
        }

        if (update.Name is { } name)
        {
            // stat(5): a wstat name change is a rename within the same directory, so the entry
            // moves in the parent as well as on the node.
            Parent?.Rekey(Name, name);
            Name = name;
        }

        Truncate(update.Size);
        _version++;
        return ValueTask.CompletedTask;
    }

    /// <summary>Records that the core released the last fid on this node.</summary>
    /// <param name="wasOpen">True when the fid had been opened.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A completed task.</returns>
    /// <exception cref="NinePException"><see cref="ClunkFailure"/> is set.</exception>
    public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        WasClunked = true;
        _ = wasOpen;

        return ClunkFailure is { } refusal
            ? throw new NinePException(refusal)
            : ValueTask.CompletedTask;
    }

    /// <summary>Records a request to reach stable storage; memory is already as stable as it gets.</summary>
    /// <param name="dataOnly">True to commit data without metadata.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A completed task.</returns>
    public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Fsyncs++;
        _ = dataOnly;
        return ValueTask.CompletedTask;
    }

    /// <summary>Applies a length change, for the node types that have contents.</summary>
    /// <param name="size">The new length, or null to leave it alone.</param>
    protected virtual void Truncate(ulong? size) => _ = size;

    private static QidType QidTypeOf(FileKind kind) => kind switch
    {
        FileKind.Directory => QidType.QTDIR,
        FileKind.Symlink => QidType.QTSYMLINK,
        _ => QidType.QTFILE,
    };
}

/// <summary>A directory in a <see cref="MemoryFilesystem"/>.</summary>
public sealed class MemoryDirectory(string name, FilePermissions perm, ulong path, MemoryFilesystem tree)
    : MemoryNode(name, FileKind.Directory, perm, path), IDirectoryHandler, ILinkCapability
{
    private readonly Dictionary<string, MemoryNode> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _cursors = new(StringComparer.Ordinal);
    private readonly SortedList<ulong, string> _entries = [];
    private ulong _lastCursor;

    /// <summary>Injects ENOSPC after this many children, without filling disk or RAM.</summary>
    public int Capacity { get; set; } = int.MaxValue;

    private void Put(string name, MemoryNode child)
    {
        if (!_cursors.ContainsKey(name))
        {
            ulong cursor = ++_lastCursor;
            _cursors.Add(name, cursor);
            _entries.Add(cursor, name);
        }
        _children[name] = child;
    }

    private bool Take(string name)
    {
        if (!_children.Remove(name))
        {
            return false;
        }

        if (_cursors.Remove(name, out ulong cursor))
        {
            _entries.Remove(cursor);
        }

        return true;
    }

    /// <summary>The children, by name.</summary>
    public IReadOnlyDictionary<string, MemoryNode> Children => _children;

    /// <summary>
    /// The last <see cref="CreateRequest"/> the core built for this directory, so a test can
    /// assert on the fields this tree does not keep — the device numbers of a <c>DMDEVICE</c>
    /// create, say, which reference §8 rule 24 parses out of the .u extension.
    /// </summary>
    public CreateRequest? LastCreate { get; private set; }

    /// <summary>
    /// Set to create a plain file whatever <see cref="CreateRequest.FileFlags"/> asks for: the
    /// handler reference §8 rule 19 guards against, one written before the member existed.
    /// </summary>
    public bool DropsFileFlags { get; set; }

    /// <summary>Adds a child under its own name.</summary>
    /// <typeparam name="TNode">The node type, so a caller keeps the concrete handle.</typeparam>
    /// <param name="child">The child to add.</param>
    /// <returns>The child, for chaining.</returns>
    public TNode Add<TNode>(TNode child)
        where TNode : MemoryNode
    {
        ArgumentNullException.ThrowIfNull(child);

        Put(child.Name, child);
        child.Parent = this;
        return child;
    }

    /// <summary>Moves a child from one name to another inside this directory.</summary>
    /// <param name="oldName">The name it has now.</param>
    /// <param name="newName">The name it takes.</param>
    internal void Rekey(string oldName, string newName)
    {
        if (_children.TryGetValue(oldName, out MemoryNode? child))
        {
            ulong cursor = _cursors[oldName];
            Take(oldName);
            _children[newName] = child;
            _cursors.Add(newName, cursor);
            _entries.Add(cursor, newName);
        }
    }

    /// <summary>Takes a child out of the directory behind the server's back, for the tests that
    /// need a removal to fail after a fid was already opened on it.</summary>
    /// <param name="name">The child to detach.</param>
    /// <returns>True when the child was there.</returns>
    public bool Detach(string name) => Take(name);

    /// <summary>Resolves one path element.</summary>
    /// <param name="name">The element; the core has already validated it.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The child, or null when the name does not exist.</returns>
    public ValueTask<IHandler?> LookupAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_children.TryGetValue(name, out MemoryNode? child)
            ? (IHandler?)child
            : null);
    }

    /// <summary>Returns a page of entries starting at a cursor.</summary>
    /// <param name="cursor">Where to resume; 0 starts the listing.</param>
    /// <param name="max">The most entries the core can use.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The page.</returns>
    public ValueTask<DirectoryListing> ReadDirAsync(
        ulong cursor, int max, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int low = 0;
        int high = _entries.Count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (_entries.Keys[mid] <= cursor)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }
        List<DirEntry> page = [];
        int at = low;
        while (at < _entries.Count && page.Count < Math.Max(max, 0))
        {
            string name = _entries.Values[at];
            MemoryNode child = _children[name];
            page.Add(new DirEntry(name, child.Qid, child.Kind, _entries.Keys[at]));
            at++;
        }
        ulong next = page.Count == 0 ? cursor : page[^1].Cursor;
        return ValueTask.FromResult(new DirectoryListing(page, next, at == _entries.Count));
    }

    /// <summary>Creates a child of any kind.</summary>
    /// <param name="request">Everything the create messages carry, unified.</param>
    /// <param name="cancellationToken">Cancels the create.</param>
    /// <returns>The new child's handler.</returns>
    /// <exception cref="NinePException">The name already exists.</exception>
    public ValueTask<IHandler> CreateAsync(
        CreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        LastCreate = request;

        if (_children.ContainsKey(request.Name))
        {
            throw new NinePException(NinePError.FromErrno(Errno.EEXIST));
        }

        if (_children.Count >= Capacity)
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOSPC));
        }

        MemoryNode child = request.Kind switch
        {
            FileKind.Directory => tree.NewDirectory(request.Name, request.Perm),
            FileKind.Symlink => tree.NewSymlink(request.Name, request.Target ?? string.Empty),
            _ => tree.NewFile(request.Name, request.Perm),
        };

        child.Owner = request.Identity.User;
        child.Uid = request.Identity.Uid != Constants.NONUNAME ? request.Identity.Uid : child.Uid;
        child.Flags = DropsFileFlags ? FileFlags.None : request.FileFlags;
        child.Parent = this;
        Put(request.Name, child);

        return ValueTask.FromResult<IHandler>(child);
    }

    /// <summary>Removes a child.</summary>
    /// <param name="name">The child's name.</param>
    /// <param name="kind">What the core resolved it to.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns>A task that completes when the child is gone.</returns>
    /// <exception cref="NinePException">The child does not exist, or a directory is not empty.</exception>
    public ValueTask RemoveAsync(string name, FileKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_children.TryGetValue(name, out MemoryNode? child))
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOENT));
        }

        // remove(5): a directory with anything in it is not removed.
        if (child is MemoryDirectory { _children.Count: > 0 })
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOTEMPTY));
        }

        _ = kind;
        Take(name);
        return ValueTask.CompletedTask;
    }

    /// <summary>Renames a child into another directory of this tree.</summary>
    /// <param name="oldName">The child's name here.</param>
    /// <param name="newParent">Where it goes; may be this directory.</param>
    /// <param name="newName">The name it takes there.</param>
    /// <param name="cancellationToken">Cancels the rename.</param>
    /// <returns>A task that completes when the child has moved.</returns>
    /// <exception cref="NinePException">The source is missing or the destination exists.</exception>
    public ValueTask RenameAsync(
        string oldName, IDirectoryHandler newParent, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newParent);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_children.TryGetValue(oldName, out MemoryNode? child))
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOENT));
        }

        MemoryDirectory destination = (MemoryDirectory)newParent;
        if (destination._children.ContainsKey(newName))
        {
            throw new NinePException(NinePError.FromErrno(Errno.EEXIST));
        }

        Take(oldName);
        child.Name = newName;
        child.Parent = destination;
        destination.Put(newName, child);
        return ValueTask.CompletedTask;
    }

    /// <summary>Creates a hard link to an existing node in this directory.</summary>
    /// <param name="name">The name the link takes.</param>
    /// <param name="target">The node being linked to.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the link exists.</returns>
    /// <exception cref="NinePException">The name already exists, or the target is from another tree.</exception>
    public ValueTask LinkAsync(string name, IHandler target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        if (_children.ContainsKey(name))
        {
            throw new NinePException(NinePError.FromErrno(Errno.EEXIST));
        }

        Put(name, target as MemoryNode
            ?? throw new NinePException(NinePError.FromErrno(Errno.EINVAL)));

        return ValueTask.CompletedTask;
    }
}

/// <summary>A regular file in a <see cref="MemoryFilesystem"/>.</summary>
public sealed class MemoryFile(string name, FilePermissions perm, ulong path)
    : MemoryNode(name, FileKind.File, perm, path), IFileHandler, IXattrHandler, ILockCapability
{
    private readonly Dictionary<string, byte[]> _xattrs = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Client, uint Proc), LockRequest> _locks = [];
    private int _readsStarted;

    // CA1819: the contents are the point of this type; a copy per access would make a test that
    // writes and reads back assert against a snapshot rather than against the file.
#pragma warning disable CA1819
    /// <summary>The file's contents.</summary>
    public byte[] Data { get; set; } = [];
#pragma warning restore CA1819

    /// <summary>The file's length.</summary>
    public override ulong Size => (ulong)Data.Length;

    /// <summary>How many open instances the core has taken and not yet disposed.</summary>
    public int Opens { get; private set; }

    /// <summary>
    /// The largest buffer the core has handed a read of this file. Architecture §9 bounds it by
    /// what the file has left to give rather than by the count the client asked for, and this is
    /// how a test can see that directly instead of inferring it from the allocator.
    /// </summary>
    public int LargestReadBuffer { get; private set; }

    /// <summary>
    /// When set, every read blocks on it. It is how a test holds a handler in flight long enough
    /// to flush it or to fill the connection's window.
    /// </summary>
    public TaskCompletionSource? ReadGate { get; set; }

    /// <summary>
    /// When set, every read blocks on it <b>without observing its cancellation token</b>. It is
    /// how a handler that does not notice a <c>Tflush</c> is modelled: the request stays in the
    /// server's hands, and its tag stays held by the worker, until the test releases the gate —
    /// which is exactly the window in which a client that has its <c>Rflush</c> is entitled to
    /// reuse the old tag (reference §5.3).
    /// </summary>
    public TaskCompletionSource? DeafReadGate { get; set; }

    /// <summary>How many reads have entered the handler, gate included.</summary>
    public int ReadsStarted => Volatile.Read(ref _readsStarted);

    /// <summary>Opens the file; the core has already checked permissions and open state.</summary>
    /// <param name="mode">The access mode.</param>
    /// <param name="flags">The flags accompanying the open.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open instance.</returns>
    public ValueTask<IOpenFile> OpenAsync(
        OpenMode mode, OpenFlags flags, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (flags.HasFlag(OpenFlags.Truncate))
        {
            Data = [];
        }

        Opens++;
        _ = mode;

        // CA2000: the open instance is the core's to dispose, which is what IOpenFile's contract
        // says and what the clunk path does.
#pragma warning disable CA2000
        return ValueTask.FromResult<IOpenFile>(new Handle(this));
#pragma warning restore CA2000
    }

    /// <summary>Grants every lock; a synthetic file has no other holder to conflict with.</summary>
    /// <param name="request">The range, type and owner.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Always success.</returns>
    public ValueTask<LockStatus> LockAsync(
        LockRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _locks[(request.ClientId, request.ProcId)] = request;
        return ValueTask.FromResult(
            request.Type == LockType.Unlock ? LockStatus.Success : LockStatus.Success);
    }

    /// <summary>Reports no conflicting lock, which Unlock is how reference §4.8 says it.</summary>
    /// <param name="request">The range and type the caller would like to take.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A result whose type is Unlock.</returns>
    public ValueTask<LockQueryResult> GetLockAsync(
        LockRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(new LockQueryResult(
            LockType.Unlock, request.Start, request.Length, request.ProcId, request.ClientId));
    }

    /// <summary>The NUL-separated list of attribute names.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The packed list.</returns>
    public ValueTask<ReadOnlyMemory<byte>> ListXattrAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<byte> packed = [];
        foreach (string name in _xattrs.Keys)
        {
            packed.AddRange(System.Text.Encoding.UTF8.GetBytes(name));
            packed.Add(0);
        }

        return ValueTask.FromResult<ReadOnlyMemory<byte>>(packed.ToArray());
    }

    /// <summary>Reads one attribute.</summary>
    /// <param name="name">The attribute's name.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The attribute's bytes.</returns>
    /// <exception cref="NinePException">There is no such attribute.</exception>
    public ValueTask<ReadOnlyMemory<byte>> GetXattrAsync(
        string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return _xattrs.TryGetValue(name, out byte[]? value)
            ? ValueTask.FromResult<ReadOnlyMemory<byte>>(value)
            : throw new NinePException(NinePError.FromErrno(Errno.ENODATA));
    }

    /// <summary>Writes one attribute.</summary>
    /// <param name="name">The attribute's name.</param>
    /// <param name="value">The bytes to store.</param>
    /// <param name="flags">Whether the attribute must or must not already exist.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A completed task.</returns>
    /// <exception cref="NinePException">The flags contradict what is there.</exception>
    public ValueTask SetXattrAsync(
        string name, ReadOnlyMemory<byte> value, XattrFlags flags, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool exists = _xattrs.ContainsKey(name);
        if (flags.HasFlag(XattrFlags.Create) && exists)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EEXIST));
        }

        if (flags.HasFlag(XattrFlags.Replace) && !exists)
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENODATA));
        }

        _xattrs[name] = value.ToArray();
        return ValueTask.CompletedTask;
    }

    /// <summary>Removes one attribute.</summary>
    /// <param name="name">The attribute's name.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A completed task.</returns>
    public ValueTask RemoveXattrAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _xattrs.Remove(name);
        return ValueTask.CompletedTask;
    }

    /// <summary>Applies a length change.</summary>
    /// <param name="size">The new length, or null to leave it alone.</param>
    protected override void Truncate(ulong? size)
    {
        if (size is ulong length)
        {
            byte[] resized = new byte[length];
            Data.AsSpan(0, (int)Math.Min((ulong)Data.Length, length)).CopyTo(resized);
            Data = resized;
        }
    }

    private sealed class Handle(MemoryFile file) : IOpenFile
    {
        public async ValueTask<int> ReadAsync(
            ulong offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref file._readsStarted);

            if (file.ReadGate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (file.DeafReadGate is { } deaf)
            {
                await deaf.Task.ConfigureAwait(false);
            }

            file.LargestReadBuffer = Math.Max(file.LargestReadBuffer, buffer.Length);

            byte[] data = file.Data;
            int at = (int)Math.Min(offset, (ulong)data.Length);
            int count = Math.Min(buffer.Length, data.Length - at);
            data.AsMemory(at, count).CopyTo(buffer);
            return count;
        }

        public ValueTask<int> WriteAsync(
            ulong offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (data.IsEmpty)
            {
                return ValueTask.FromResult(0);
            }
            if (offset > int.MaxValue || (ulong)data.Length > (ulong)int.MaxValue - offset)
            {
                throw new NinePException(NinePError.FromErrno(Errno.EFBIG));
            }
            int at = (int)offset;
            int end = at + data.Length;
            if (file.Data.Length < end)
            {
                byte[] grown = new byte[end];
                file.Data.CopyTo(grown, 0);
                file.Data = grown;
            }

            data.CopyTo(file.Data.AsMemory(at));
            return ValueTask.FromResult(data.Length);
        }

        public ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult((ulong)file.Data.Length);
        }

        public ValueTask DisposeAsync()
        {
            file.Opens--;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A symbolic link in a <see cref="MemoryFilesystem"/>.</summary>
public sealed class MemorySymlink(string name, string target, ulong path)
    : MemoryNode(name, FileKind.Symlink, Perms.P0777, path), ISymlinkHandler
{
    /// <summary>What the link points at.</summary>
    public string Target { get; } = target;

    /// <summary>The link's target.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The text the link points at.</returns>
    public ValueTask<string> ReadlinkAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(Target);
    }
}
