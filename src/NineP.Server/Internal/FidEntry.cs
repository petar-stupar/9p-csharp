using NineP.Protocol;
using NineP.Protocol.Auth;

namespace NineP.Server.Internal;

/// <summary>
/// One fid's whole state (§6.5). The gate is what makes a fid a state machine: operations on one
/// fid are serialised, while different fids run concurrently — without it a <c>Tread</c> and a
/// <c>Tclunk</c> on the same fid could interleave and the read would outlive its own open file.
/// </summary>
internal sealed class FidEntry : IAsyncDisposable
{
    /// <summary>Creates an entry bound to a file.</summary>
    /// <param name="fid">The number the client chose.</param>
    /// <param name="handler">The file this fid names.</param>
    /// <param name="identity">The implicit user of every request on this fid (reference §5.2).</param>
    /// <param name="aname">The tree the attach that created this fid named.</param>
    public FidEntry(uint fid, IHandler handler, Identity identity, string aname)
    {
        Fid = fid;
        Handler = handler;
        AttachRoot = handler;
        Identity = identity;
        Aname = aname;
    }

    /// <summary>The number the client chose.</summary>
    public uint Fid { get; }

    /// <summary>The file this fid names.</summary>
    public IHandler Handler { get; set; }

    /// <summary>The directory the file lives in, when the core walked to it.</summary>
    public IDirectoryHandler? Parent { get => Path?.Directory ?? _parent; set => _parent = value; }

    private IDirectoryHandler? _parent;

    /// <summary>The name the file has inside <see cref="Parent"/>.</summary>
    public string Name { get => Path?.Name ?? _name; set => _name = value; }

    private string _name = string.Empty;

    /// <summary>The attach boundary inherited by every cloned or walked fid.</summary>
    public IHandler AttachRoot { get; set; }

    /// <summary>The full ancestry, shared across clones and preserved across walk messages.</summary>
    public FidPath? Path { get; set; }

    /// <summary>The registry retaining this fid until finalization has completed.</summary>
    public PathState? Paths { get; set; }

    /// <summary>The implicit user of every request on this fid, never a per-message field.</summary>
    public Identity Identity { get; set; }

    /// <summary>The tree the attach that created this fid named.</summary>
    public string Aname { get; set; }

    /// <summary>Where the fid is in its state machine.</summary>
    public FidState State { get; set; } = FidState.Bound;

    /// <summary>The access mode the fid was opened with, or null while it is not open.</summary>
    public OpenMode? Mode { get; set; }

    /// <summary>The flags the fid was opened with.</summary>
    public OpenFlags Flags { get; set; }

    /// <summary>The open instance behind the fid, for a regular file.</summary>
    public IOpenFile? Open { get; set; }

    /// <summary>True when this fid holds the exclusive-use lock of a DMEXCL file (reference §5.5).</summary>
    public bool HoldsExclusive { get; set; }

    /// <summary>The offset the next 9P2000 directory read must use (reference §5.6).</summary>
    public ulong DirOffset { get; set; }

    /// <summary>The count the last directory read returned, which fixes the next legal offset.</summary>
    public ulong DirCount { get; set; }

    /// <summary>The handler cursor the next directory read resumes from.</summary>
    public ulong DirCursor { get; set; }

    /// <summary>The session generation this fid was created in; a Tversion invalidates older ones.</summary>
    public int Generation { get; set; }

    /// <summary>The triple an afid is bound to (reference §5.2, S-22).</summary>
    public AuthRequest? AuthBinding { get; set; }

    /// <summary>The exchange behind an afid, whose identity an attach must find non-null.</summary>
    public IAuthSession? AuthSession { get; set; }

    /// <summary>The xattr this fid was walked onto, when it was.</summary>
    public string? XattrName { get; set; }

    /// <summary>The bytes a <c>Txattrcreate</c> promised, which the clunk checks.</summary>
    public ulong? XattrSize { get; set; }

    /// <summary>The flags a <c>Txattrcreate</c> carried, honoured when the value is committed.</summary>
    public XattrFlags XattrFlags { get; set; }

    /// <summary>The bytes written to an xattr fid so far.</summary>
    public List<byte>? XattrBuffer { get; set; }

    /// <summary>Serialises operations on this fid; different fids run concurrently.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>True when this fid is an afid, which accepts read, write and clunk only.</summary>
    public bool IsAuth => State == FidState.Auth;

    /// <summary>Releases the open file and the gate.</summary>
    /// <returns>A task that completes when the open instance has been disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Open is not null)
            {
                await Open.DisposeAsync().ConfigureAwait(false);
                Open = null;
            }

            if (AuthSession is not null)
            {
                await AuthSession.DisposeAsync().ConfigureAwait(false);
                AuthSession = null;
            }

        }
        finally
        {
            Paths?.Remove(this);
            Paths = null;
        }

        // SemaphoreSlim owns no native handle unless AvailableWaitHandle is requested (it never
        // is here). A retired entry can still have queued operation leases; leaving the managed
        // gate to GC lets those waiters wake, observe retirement and return EBADF safely.
    }
}
