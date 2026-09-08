using System.Globalization;
using NineP.Protocol;
using NineP.Server;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>A list's <c>name</c> file: free text, replaced by a write.</summary>
internal sealed class TodoListNameFile(
    TodoSession session, UserRow owner, ListRow list)
    : TodoFile(session)
{
    /// <summary>The file's qid.</summary>
    public override Qid Qid => TodoQid.ListName(list.Id, list.UpdatedAt);

    /// <summary>The list's current name.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The name, which is empty until it is written.</returns>
    protected internal override async Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        ListRow? current = await Session.Store
            .FindListAsync(owner.Id, list.Index, cancellationToken).ConfigureAwait(false);

        return current?.Name ?? throw new NinePException(NinePError.FromErrno(Errno.ENOENT));
    }

    /// <summary>Replaces the list's name.</summary>
    /// <param name="value">The new name.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the row has changed.</returns>
    /// <exception cref="NinePException">This identity does not own the list.</exception>
    protected internal override async Task WriteAsync(
        string value, CancellationToken cancellationToken = default)
    {
        if (!await Session.Store.SetListNameAsync(owner.Id, list.Id, value, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOENT));
        }
    }
}
