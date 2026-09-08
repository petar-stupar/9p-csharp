using Microsoft.Data.Sqlite;

namespace NineP.TodoFs.Storage;

/// <summary>
/// The list and item half of the store. <b>Every</b> query here carries the owning user's id, in
/// the <c>WHERE</c> clause or through the join to <c>lists</c>: that scoping is what makes user A
/// unable to name a row of user B, and it is the isolation the handler layer relies on
/// (<c>TodoFsIsolationTests</c>).
/// </summary>
internal sealed partial class TodoStore
{
    private const string ListColumns = "id, user_id, idx, name, created_at, updated_at";
    private const string ItemColumns =
        "items.id, items.list_id, items.idx, items.label, items.description, items.status, "
        + "items.created_at, items.updated_at";

    /// <summary>The lists of one user, by index.</summary>
    /// <param name="userId">The attaching user's id.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The rows.</returns>
    public Task<IReadOnlyList<ListRow>> ListListsAsync(
        long userId, CancellationToken cancellationToken = default) =>
        ReadAsync(
            "SELECT " + ListColumns + " FROM lists WHERE user_id = @uid ORDER BY idx",
            command => command.Parameters.AddWithValue("@uid", userId),
            ReadList,
            cancellationToken);

    /// <summary>One list of one user, by index.</summary>
    /// <param name="userId">The attaching user's id; the scope that makes this safe.</param>
    /// <param name="index">The list's number, which is its directory name.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The row, or null when this user has no such list.</returns>
    public async Task<ListRow?> FindListAsync(
        long userId, long index, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ListRow> found = await ReadAsync(
            "SELECT " + ListColumns + " FROM lists WHERE user_id = @uid AND idx = @idx",
            command =>
            {
                command.Parameters.AddWithValue("@uid", userId);
                command.Parameters.AddWithValue("@idx", index);
            },
            ReadList,
            cancellationToken).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    /// <summary>Creates a list at an index of this user's own numbering.</summary>
    /// <param name="userId">The attaching user's id.</param>
    /// <param name="index">The list's number.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The new row.</returns>
    /// <exception cref="SqliteException">The index is already taken.</exception>
    public async Task<ListRow> CreateListAsync(
        long userId, long index, CancellationToken cancellationToken = default)
    {
        long now = Now();
        await WriteAsync(
            "INSERT INTO lists (user_id, idx, name, created_at, updated_at) "
            + "VALUES (@uid, @idx, '', @now, @now)",
            command =>
            {
                command.Parameters.AddWithValue("@uid", userId);
                command.Parameters.AddWithValue("@idx", index);
                command.Parameters.AddWithValue("@now", now);
            },
            cancellationToken).ConfigureAwait(false);

        return await FindListAsync(userId, index, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("the list row vanished between insert and read");
    }

    /// <summary>Replaces a list's name.</summary>
    /// <param name="userId">The attaching user's id.</param>
    /// <param name="listId">The list.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>True when this user owned that list.</returns>
    public async Task<bool> SetListNameAsync(
        long userId, long listId, string name, CancellationToken cancellationToken = default)
    {
        int changed = await WriteAsync(
            "UPDATE lists SET name = @name, updated_at = @now WHERE id = @id AND user_id = @uid",
            command =>
            {
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@now", Now());
                command.Parameters.AddWithValue("@id", listId);
                command.Parameters.AddWithValue("@uid", userId);
            },
            cancellationToken).ConfigureAwait(false);

        return changed > 0;
    }

    /// <summary>Removes a list; its items go with it through the schema's cascade.</summary>
    /// <param name="userId">The attaching user's id.</param>
    /// <param name="listId">The list.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>True when this user owned that list.</returns>
    public async Task<bool> RemoveListAsync(
        long userId, long listId, CancellationToken cancellationToken = default)
    {
        int removed = await WriteAsync(
            "DELETE FROM lists WHERE id = @id AND user_id = @uid",
            command =>
            {
                command.Parameters.AddWithValue("@id", listId);
                command.Parameters.AddWithValue("@uid", userId);
            },
            cancellationToken).ConfigureAwait(false);

        return removed > 0;
    }

    /// <summary>The items of one list of one user, by index.</summary>
    /// <param name="userId">The attaching user's id.</param>
    /// <param name="listId">The list.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The rows.</returns>
    public Task<IReadOnlyList<ItemRow>> ListItemsAsync(
        long userId, long listId, CancellationToken cancellationToken = default) =>
        ReadAsync(
            "SELECT " + ItemColumns + " FROM items JOIN lists ON lists.id = items.list_id "
            + "WHERE items.list_id = @lid AND lists.user_id = @uid ORDER BY items.idx",
            command =>
            {
                command.Parameters.AddWithValue("@lid", listId);
                command.Parameters.AddWithValue("@uid", userId);
            },
            ReadItem,
            cancellationToken);

    /// <summary>One item, by its list and index.</summary>
    /// <param name="userId">The attaching user's id.</param>
    /// <param name="listId">The list.</param>
    /// <param name="index">The item's number.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The row, or null when this user has no such item.</returns>
    public async Task<ItemRow?> FindItemAsync(
        long userId, long listId, long index, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ItemRow> found = await ReadAsync(
            "SELECT " + ItemColumns + " FROM items JOIN lists ON lists.id = items.list_id "
            + "WHERE items.list_id = @lid AND items.idx = @idx AND lists.user_id = @uid",
            command =>
            {
                command.Parameters.AddWithValue("@lid", listId);
                command.Parameters.AddWithValue("@idx", index);
                command.Parameters.AddWithValue("@uid", userId);
            },
            ReadItem,
            cancellationToken).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    /// <summary>Creates an item with an empty label and description and status <c>open</c>.</summary>
    /// <param name="userId">The attaching user's id.</param>
    /// <param name="listId">The list.</param>
    /// <param name="index">The item's number.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The new row.</returns>
    /// <exception cref="SqliteException">The index is already taken.</exception>
    public async Task<ItemRow> CreateItemAsync(
        long userId, long listId, long index, CancellationToken cancellationToken = default)
    {
        long now = Now();
        await WriteAsync(
            "INSERT INTO items (list_id, idx, label, description, status, created_at, updated_at) "
            + "SELECT @lid, @idx, '', '', 'open', @now, @now FROM lists "
            + "WHERE lists.id = @lid AND lists.user_id = @uid",
            command =>
            {
                command.Parameters.AddWithValue("@lid", listId);
                command.Parameters.AddWithValue("@idx", index);
                command.Parameters.AddWithValue("@now", now);
                command.Parameters.AddWithValue("@uid", userId);
            },
            cancellationToken).ConfigureAwait(false);

        return await FindItemAsync(userId, listId, index, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("the item row vanished between insert and read");
    }

    /// <summary>Replaces one field of one item.</summary>
    /// <param name="userId">The attaching user's id.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="field">Which of the three files was written.</param>
    /// <param name="value">The new contents.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>True when this user owned that item.</returns>
    public async Task<bool> SetItemFieldAsync(
        long userId,
        long itemId,
        TodoField field,
        string value,
        CancellationToken cancellationToken = default)
    {
        // The column is chosen from a closed enum, never from anything a client sent: a field name
        // interpolated into SQL would be an injection however carefully it were checked.
        string column = field switch
        {
            TodoField.Label => "label",
            TodoField.Description => "description",
            TodoField.Status => "status",
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "no such item field"),
        };

        int changed = await WriteAsync(
            "UPDATE items SET " + column + " = @value, updated_at = @now "
            + "WHERE items.id = @id AND items.list_id IN "
            + "(SELECT lists.id FROM lists WHERE lists.user_id = @uid)",
            command =>
            {
                command.Parameters.AddWithValue("@value", value);
                command.Parameters.AddWithValue("@now", Now());
                command.Parameters.AddWithValue("@id", itemId);
                command.Parameters.AddWithValue("@uid", userId);
            },
            cancellationToken).ConfigureAwait(false);

        return changed > 0;
    }

    /// <summary>Removes one item.</summary>
    /// <param name="userId">The attaching user's id.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>True when this user owned that item.</returns>
    public async Task<bool> RemoveItemAsync(
        long userId, long itemId, CancellationToken cancellationToken = default)
    {
        int removed = await WriteAsync(
            "DELETE FROM items WHERE items.id = @id AND items.list_id IN "
            + "(SELECT lists.id FROM lists WHERE lists.user_id = @uid)",
            command =>
            {
                command.Parameters.AddWithValue("@id", itemId);
                command.Parameters.AddWithValue("@uid", userId);
            },
            cancellationToken).ConfigureAwait(false);

        return removed > 0;
    }

    private static ListRow ReadList(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetInt64(2),
        reader.GetString(3),
        reader.GetInt64(4),
        reader.GetInt64(5));

    private static ItemRow ReadItem(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetInt64(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetInt64(6),
        reader.GetInt64(7));
}
