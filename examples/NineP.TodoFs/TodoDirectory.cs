using System.Text;
using NineP.Protocol;
using NineP.Server;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>A directory that answers nothing but lookup and listing.</summary>
internal abstract class TodoDirectory : TodoNode, IDirectoryHandler
{
    /// <summary>Creates a directory node.</summary>
    /// <param name="session">The attach's state.</param>
    protected TodoDirectory(TodoSession session)
        : base(session)
    {
    }

    /// <summary>A container is a directory in every dialect.</summary>
    protected override FileKind Kind => FileKind.Directory;

    /// <summary>Resolves one entry name.</summary>
    /// <param name="name">The name; the core has already validated it.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The child's handler, or null when there is no such entry.</returns>
    public abstract ValueTask<IHandler?> LookupAsync(
        string name, CancellationToken cancellationToken = default);

    /// <summary>Returns a page of entries starting at a cursor.</summary>
    /// <param name="cursor">Where to resume; 0 starts the listing.</param>
    /// <param name="max">The most entries the core can use.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The page.</returns>
    public async ValueTask<DirectoryListing> ReadDirAsync(
        ulong cursor, int max, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DirEntry> all = await EntriesAsync(cancellationToken).ConfigureAwait(false);

        int start = cursor > (ulong)all.Count ? all.Count : (int)cursor;
        int count = Math.Min(max, all.Count - start);
        List<DirEntry> page = [];

        for (int i = 0; i < count; i++)
        {
            DirEntry entry = all[start + i];
            page.Add(entry with { Cursor = (ulong)(start + i + 1) });
        }

        return new DirectoryListing(page, (ulong)(start + count), start + count >= all.Count);
    }

    /// <summary>Creates a child; most of the tree's directories have nothing to create.</summary>
    /// <param name="request">Everything the five create messages carry, unified.</param>
    /// <param name="cancellationToken">Cancels the create.</param>
    /// <returns>The new child's handler.</returns>
    /// <exception cref="NinePException">This directory does not create children.</exception>
    public virtual ValueTask<IHandler> CreateAsync(
        CreateRequest request, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EPERM));

    /// <summary>Removes a child; most of the tree's directories have nothing to remove.</summary>
    /// <param name="name">The child's name.</param>
    /// <param name="kind">What the core resolved the child to.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns>A task that completes when the child is gone.</returns>
    /// <exception cref="NinePException">This directory does not remove children.</exception>
    public virtual ValueTask RemoveAsync(
        string name, FileKind kind, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EPERM));

    /// <summary>todofs has no renames: every name in the tree is derived from a row.</summary>
    /// <param name="oldName">The child's name here.</param>
    /// <param name="newParent">The directory to move it into.</param>
    /// <param name="newName">The name it would take.</param>
    /// <param name="cancellationToken">Cancels the rename.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NinePException">Always: the tree's names are not the client's to choose.</exception>
    public ValueTask RenameAsync(
        string oldName,
        IDirectoryHandler newParent,
        string newName,
        CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EPERM));

    /// <summary>Every entry of this directory, in listing order.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The entries.</returns>
    protected abstract ValueTask<IReadOnlyList<DirEntry>> EntriesAsync(
        CancellationToken cancellationToken);
}
