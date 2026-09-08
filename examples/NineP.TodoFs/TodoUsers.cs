using System.Globalization;
using NineP.Protocol;
using NineP.Server;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>
/// <c>/users</c>: the control file, and the attaching user's own directory and no other. The
/// visibility is a query, not a name comparison, so that removing the user-id scope from it is
/// what <c>TodoFsIsolationTests</c> catches (AC-c, RK-52).
/// </summary>
internal sealed class TodoUsers(TodoSession session) : TodoDirectory(session)
{
    private const int UsersNode = 1;

    /// <summary>The directory's qid.</summary>
    public override Qid Qid => TodoQid.Fixed(UsersNode, QidType.QTDIR);

    /// <summary>Resolves <c>ctl</c> or the attaching user's own name.</summary>
    /// <param name="name">The entry name.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The child, or null when this identity may not see it.</returns>
    public override async ValueTask<IHandler?> LookupAsync(
        string name, CancellationToken cancellationToken = default)
    {
        if (name == TodoCtl.Name)
        {
            return new TodoCtl(Session);
        }

        if (View.User is not UserRow attached)
        {
            return null;
        }

        UserRow? visible = await Session.Store
            .FindVisibleUserAsync(attached.Id, name, cancellationToken).ConfigureAwait(false);

        return visible is UserRow row ? new TodoUserDirectory(Session, row) : null;
    }

    /// <summary>The control file, plus this identity's own directory when it has one.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The entries.</returns>
    protected override async ValueTask<IReadOnlyList<DirEntry>> EntriesAsync(
        CancellationToken cancellationToken)
    {
        List<DirEntry> entries =
        [
            new DirEntry(TodoCtl.Name, TodoQid.Fixed(TodoCtl.Node, QidType.QTFILE), FileKind.File, 0),
        ];

        if (View.User is not UserRow attached)
        {
            return entries;
        }

        foreach (UserRow row in await Session.Store
            .ListVisibleUsersAsync(attached.Id, cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new DirEntry(
                row.Name,
                TodoQid.For(TodoQid.UsersTag, row.Id, row.UpdatedAt, QidType.QTDIR),
                FileKind.Directory,
                0));
        }

        return entries;
    }
}
