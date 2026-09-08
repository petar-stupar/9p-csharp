using System.Globalization;
using NineP.Protocol;
using NineP.Server;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>
/// <c>/users/&lt;user&gt;/&lt;n&gt;</c>: one list. Its entries are its <c>name</c> file and one
/// directory per item, named by the item's own number.
/// </summary>
internal sealed class TodoListDirectory(
    TodoSession session, UserRow owner, ListRow list)
    : TodoDirectory(session)
{
    private const string NameFile = "name";

    /// <summary>The list's qid.</summary>
    public override Qid Qid =>
        TodoQid.For(TodoQid.ListsTag, list.Id, list.UpdatedAt, QidType.QTDIR);

    /// <summary>Resolves the <c>name</c> file or one item by its number.</summary>
    /// <param name="name">The entry name.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The child, or null.</returns>
    public override async ValueTask<IHandler?> LookupAsync(
        string name, CancellationToken cancellationToken = default)
    {
        if (name == NameFile)
        {
            return new TodoListNameFile(Session, owner, list);
        }

        if (!TodoIndex.TryParse(name, out long index))
        {
            return null;
        }

        ItemRow? found = await Session.Store
            .FindItemAsync(owner.Id, list.Id, index, cancellationToken).ConfigureAwait(false);

        return found is ItemRow row ? new TodoItemDirectory(Session, owner, list, row) : null;
    }

    /// <summary>Creates an item at the next number, with empty fields and status <c>open</c>.</summary>
    /// <param name="request">Everything the create messages carry, unified.</param>
    /// <param name="cancellationToken">Cancels the create.</param>
    /// <returns>The new item's handler.</returns>
    /// <exception cref="NinePException">The name is not the next number, or the kind is wrong.</exception>
    public override async ValueTask<IHandler> CreateAsync(
        CreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Kind != FileKind.Directory)
        {
            throw Invalid();
        }

        IReadOnlyList<ItemRow> existing = await Session.Store
            .ListItemsAsync(owner.Id, list.Id, cancellationToken).ConfigureAwait(false);

        long next = existing.Count == 0 ? 0 : existing[^1].Index + 1;
        if (!TodoIndex.TryParse(request.Name, out long index) || index != next)
        {
            throw Invalid();
        }

        ItemRow created = await Session.Store
            .CreateItemAsync(owner.Id, list.Id, index, cancellationToken).ConfigureAwait(false);

        return new TodoItemDirectory(Session, owner, list, created);
    }

    /// <summary>Removes an item; its three field files go with it.</summary>
    /// <param name="name">The item's number as text.</param>
    /// <param name="kind">What the core resolved it to.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns>A task that completes when the item is gone.</returns>
    /// <exception cref="NinePException">There is no such item, or the name is the list's own file.</exception>
    public override async ValueTask RemoveAsync(
        string name, FileKind kind, CancellationToken cancellationToken = default)
    {
        if (name == NameFile)
        {
            // The name file is part of the list, not a member of it: it goes when the list does.
            throw new NinePException(NinePError.FromErrno(Errno.EPERM));
        }

        if (!TodoIndex.TryParse(name, out long index))
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOENT));
        }

        ItemRow row = await Session.Store.FindItemAsync(owner.Id, list.Id, index, cancellationToken)
            .ConfigureAwait(false) ?? throw new NinePException(NinePError.FromErrno(Errno.ENOENT));

        await Session.Store.RemoveItemAsync(owner.Id, row.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The <c>name</c> file and this list's items.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The entries.</returns>
    protected override async ValueTask<IReadOnlyList<DirEntry>> EntriesAsync(
        CancellationToken cancellationToken)
    {
        List<DirEntry> entries =
        [
            new DirEntry(NameFile, TodoQid.ListName(list.Id, list.UpdatedAt), FileKind.File, 0),
        ];

        foreach (ItemRow row in await Session.Store
            .ListItemsAsync(owner.Id, list.Id, cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new DirEntry(
                row.Index.ToString(CultureInfo.InvariantCulture),
                TodoQid.For(TodoQid.ItemsTag, row.Id, row.UpdatedAt, QidType.QTDIR),
                FileKind.Directory,
                0));
        }

        return entries;
    }
}
