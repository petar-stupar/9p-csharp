using System.Text;
using NineP.Protocol;
using NineP.Server;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>What every node of the todofs tree has in common (architecture §4).</summary>
internal abstract class TodoNode : IHandler
{
    /// <summary>Permission bits every file in the tree reports.</summary>
    protected const uint FilePerm = 0x1A4;

    /// <summary>Permission bits every directory in the tree reports.</summary>
    protected const uint DirectoryPerm = 0x1ED;

    /// <summary>Creates a node of one attach's tree.</summary>
    /// <param name="session">The attach's state.</param>
    protected TodoNode(TodoSession session)
    {
        Session = session;
    }

    /// <summary>The qid; a directory's type byte is QTDIR and a file's is QTFILE.</summary>
    public abstract Qid Qid { get; }

    /// <summary>The attach's state.</summary>
    protected TodoSession Session { get; }

    /// <summary>What this fid may see.</summary>
    protected TodoView View => Session.View;

    /// <summary>What kind of file this node is.</summary>
    protected abstract FileKind Kind { get; }

    /// <summary>The node's length in bytes; zero for a directory.</summary>
    protected virtual ulong Length => 0;

    /// <summary>The dialect-neutral attributes the core projects into the session's shape.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The attributes.</returns>
    public virtual ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TimeSpec now = new(Session.Clock.GetUtcNow().ToUnixTimeSeconds(), 0);
        TodoView view = View;
        ulong size = Length;

        return ValueTask.FromResult(new Attr
        {
            Qid = Qid,
            Kind = Kind,
            Perm = Kind == FileKind.Directory ? DirectoryPerm : FilePerm,
            UserName = view.Identity.User,
            GroupName = view.Identity.User,
            ModifierName = view.Identity.User,
            Uid = view.User is UserRow row ? (uint)row.Id : Constants.NONUNAME,
            Gid = view.User is UserRow owner ? (uint)owner.Id : Constants.NONUNAME,
            Size = size,
            Blocks = (size + 511) / 512,
            ATime = now,
            MTime = now,
            CTime = now,
        });
    }

    /// <summary>
    /// A todofs <b>directory</b> has nothing a client may set: the mode, the owner and the times
    /// are derived from the row and the attaching identity, and stat(5) keeps a directory's length
    /// at zero. Reference §8 rule 27 makes the answer all-or-nothing, so an update that names one
    /// field this tree cannot hold is refused whole. It used to answer success for
    /// <c>Size = 0</c>, which dropped every other field of the same update on the floor.
    /// </summary>
    /// <param name="update">The fields to change.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the change has been made.</returns>
    /// <exception cref="NinePException">The update names something todofs cannot change.</exception>
    public virtual ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();

        // A wstat that changes nothing is a request to reach stable storage (reference §4.2).
        if (update.IsFsyncRequest)
        {
            return ValueTask.CompletedTask;
        }

        if (NamesADerivedField(update))
        {
            throw Unsupported();
        }

        if (update.Size is not 0)
        {
            // stat(5): a directory's length must stay zero, which is what truncate(2) answers
            // EISDIR for.
            throw new NinePException(NinePError.FromErrno(Errno.EISDIR));
        }

        // Only a length of zero was asked for, and a directory's length is already zero: the
        // update is performed in full by doing nothing.
        return ValueTask.CompletedTask;
    }

    /// <summary>Nothing to release: the tree is the database and it outlives every fid.</summary>
    /// <param name="wasOpen">Ignored.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A completed task.</returns>
    public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <summary>SQLite in WAL mode has already committed by the time a write is answered.</summary>
    /// <param name="dataOnly">Ignored.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A completed task.</returns>
    public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// True when the update names a field no todofs node stores. The mode, the owner, the group
    /// and the times are all derived, and a field file has no name of its own to change, so an
    /// update carrying one of them is refused whole under reference §8 rule 27.
    /// </summary>
    /// <param name="update">The update to inspect.</param>
    /// <returns>True when at least one derived field is set.</returns>
    protected static bool NamesADerivedField(SetAttr update)
    {
        ArgumentNullException.ThrowIfNull(update);

        return update.Perm is not null
            || update.Uid is not null
            || update.Gid is not null
            || update.GroupName is not null
            || update.Name is not null
            || update.ATime is not null
            || update.MTime is not null
            || update.ATimeToNow
            || update.MTimeToNow
            || update.CTimeToNow;
    }

    /// <summary>Refuses a request naming an operation this tree does not have.</summary>
    /// <returns>The exception to throw.</returns>
    protected static NinePException Unsupported() =>
        new(NinePError.FromErrno(Errno.EOPNOTSUPP));

    /// <summary>Refuses a request this identity may not make.</summary>
    /// <returns>The exception to throw.</returns>
    protected static NinePException Denied() =>
        new(NinePError.FromErrno(Errno.EACCES));

    /// <summary>Refuses a request that names something the tree does not offer.</summary>
    /// <returns>The exception to throw.</returns>
    protected static NinePException Invalid() =>
        new(NinePError.FromErrno(Errno.EINVAL));

    /// <summary>The row this fid runs as, or a refusal when the identity has none.</summary>
    /// <returns>The attaching user's row.</returns>
    /// <exception cref="NinePException">The identity owns no row.</exception>
    protected UserRow RequireUser() =>
        View.User ?? throw Denied();
}
