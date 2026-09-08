using System.Globalization;
using NineP.Protocol;
using NineP.Server;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>
/// <c>/users/&lt;user&gt;/&lt;n&gt;/&lt;m&gt;</c>: one item, which is three files and nothing else.
/// The item is virtual — it is one row — so removing it removes the three files with it.
/// </summary>
internal sealed class TodoItemDirectory(
    TodoSession session, UserRow owner, ListRow list, ItemRow item)
    : TodoDirectory(session)
{
    private static readonly (string Name, TodoField Field)[] Fields =
    [
        ("label", TodoField.Label),
        ("description", TodoField.Description),
        ("status", TodoField.Status),
    ];

    /// <summary>The item's qid.</summary>
    public override Qid Qid =>
        TodoQid.For(TodoQid.ItemsTag, item.Id, item.UpdatedAt, QidType.QTDIR);

    /// <summary>Resolves one of the item's three files.</summary>
    /// <param name="name">The entry name.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The child, or null.</returns>
    public override ValueTask<IHandler?> LookupAsync(
        string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach ((string entry, TodoField which) in Fields)
        {
            if (string.Equals(entry, name, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<IHandler?>(
                    new TodoItemFieldFile(Session, owner, list, item, which));
            }
        }

        return ValueTask.FromResult<IHandler?>(null);
    }

    /// <summary>The item's three files.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The entries.</returns>
    protected override ValueTask<IReadOnlyList<DirEntry>> EntriesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<DirEntry> entries = [];
        foreach ((string entry, TodoField which) in Fields)
        {
            entries.Add(new DirEntry(
                entry, TodoQid.ItemField(item.Id, which, item.UpdatedAt), FileKind.File, 0));
        }

        return ValueTask.FromResult<IReadOnlyList<DirEntry>>(entries);
    }
}
