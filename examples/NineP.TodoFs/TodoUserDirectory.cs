using System.Globalization;
using NineP.Protocol;
using NineP.Server;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>
/// <c>/users/&lt;user&gt;</c>: one directory per list, named by the list's own number. A list is
/// created by <c>mkdir</c> at the <b>next</b> number and by nothing else, so that the names of the
/// lists that are already there never move.
/// </summary>
internal sealed class TodoUserDirectory(TodoSession session, UserRow user)
    : TodoDirectory(session)
{
    /// <summary>The user's qid.</summary>
    public override Qid Qid =>
        TodoQid.For(TodoQid.UsersTag, user.Id, user.UpdatedAt, QidType.QTDIR);

    /// <summary>Resolves one of this user's lists by its number.</summary>
    /// <param name="name">The list's number as text.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The list, or null.</returns>
    public override async ValueTask<IHandler?> LookupAsync(
        string name, CancellationToken cancellationToken = default)
    {
        if (!TodoIndex.TryParse(name, out long index))
        {
            return null;
        }

        ListRow? found = await Session.Store
            .FindListAsync(Owner.Id, index, cancellationToken).ConfigureAwait(false);

        return found is ListRow row ? new TodoListDirectory(Session, Owner, row) : null;
    }

    /// <summary>Creates a list at the next number.</summary>
    /// <param name="request">Everything the create messages carry, unified.</param>
    /// <param name="cancellationToken">Cancels the create.</param>
    /// <returns>The new list's handler.</returns>
    /// <exception cref="NinePException">
    /// The name is not the next number, the kind is wrong, or the user is at <c>--max-lists</c>
    /// (<c>ENOSPC</c>). The quota is the store's to decide, inside the create's own transaction:
    /// a count here would be a second, racing, check (E2).
    /// </exception>
    public override async ValueTask<IHandler> CreateAsync(
        CreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Kind != FileKind.Directory)
        {
            throw Invalid();
        }

        IReadOnlyList<ListRow> existing = await Session.Store
            .ListListsAsync(Owner.Id, cancellationToken).ConfigureAwait(false);

        long next = existing.Count == 0 ? 0 : existing[^1].Index + 1;
        if (!TodoIndex.TryParse(request.Name, out long index) || index != next)
        {
            throw Invalid();
        }

        ListRow created;
        try
        {
            created = await Session.Store
                .CreateListAsync(Owner.Id, index, cancellationToken).ConfigureAwait(false);
        }
        catch (TodoQuotaException)
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOSPC));
        }

        return new TodoListDirectory(Session, Owner, created);
    }

    /// <summary>Removes an empty list.</summary>
    /// <param name="name">The list's number as text.</param>
    /// <param name="kind">What the core resolved it to.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns>A task that completes when the list is gone.</returns>
    /// <exception cref="NinePException">There is no such list, or it still has items.</exception>
    public override async ValueTask RemoveAsync(
        string name, FileKind kind, CancellationToken cancellationToken = default)
    {
        if (!TodoIndex.TryParse(name, out long index))
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOENT));
        }

        ListRow row = await Session.Store.FindListAsync(Owner.Id, index, cancellationToken)
            .ConfigureAwait(false) ?? throw new NinePException(NinePError.FromErrno(Errno.ENOENT));

        IReadOnlyList<ItemRow> items = await Session.Store
            .ListItemsAsync(Owner.Id, row.Id, cancellationToken).ConfigureAwait(false);

        if (items.Count > 0)
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOTEMPTY));
        }

        await Session.Store.RemoveListAsync(Owner.Id, row.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>This user's lists, by number.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The entries.</returns>
    protected override async ValueTask<IReadOnlyList<DirEntry>> EntriesAsync(
        CancellationToken cancellationToken)
    {
        List<DirEntry> entries = [];

        foreach (ListRow row in await Session.Store
            .ListListsAsync(Owner.Id, cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new DirEntry(
                row.Index.ToString(CultureInfo.InvariantCulture),
                TodoQid.For(TodoQid.ListsTag, row.Id, row.UpdatedAt, QidType.QTDIR),
                FileKind.Directory,
                0));
        }

        return entries;
    }

    /// <summary>
    /// The row every query below is scoped by. It is the <b>attaching</b> user, not the directory
    /// this handler was built for: a fid that reached this directory did so through
    /// <see cref="TodoUsers.LookupAsync"/>, which only ever hands out the attaching user's own.
    /// </summary>
    private UserRow Owner => RequireUser();
}
