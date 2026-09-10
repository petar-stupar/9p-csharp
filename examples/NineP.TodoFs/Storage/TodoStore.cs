using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NineP.TodoFs.Storage;

/// <summary>
/// The only place in this repository that speaks SQL (workspace architecture §8 item 7, enforced
/// by <c>RepoHygieneTests</c>). Every statement is parameterised — a name reaching this class came
/// from a 9P client and is never concatenated into a query.
/// </summary>
/// <remarks>
/// SQLite allows one writer and many readers under WAL, so the shape is exactly that: one writer
/// connection behind a <see cref="SemaphoreSlim"/> of one, and a read path that opens a pooled
/// connection per query. A second writer connection would meet <c>SQLITE_BUSY</c> under load, and
/// a <c>busy_timeout</c> would turn that into a stall rather than into correctness.
/// </remarks>
internal sealed partial class TodoStore : IAsyncDisposable
{
    /// <summary>The most bytes one field of one item may hold (§8.3).</summary>
    public const int MaxFieldBytes = 64 * 1024;

    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly SqliteConnection _writer;
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly TodoQuotas _quotas;

    private TodoStore(
        SqliteConnection writer, string connectionString, TimeProvider clock, TodoQuotas quotas)
    {
        _writer = writer;
        _connectionString = connectionString;
        _clock = clock;
        _quotas = quotas;
    }

    /// <summary>The quotas every create is checked against, inside its transaction.</summary>
    public TodoQuotas Quotas => _quotas;

    /// <summary>Opens or creates a database and brings its schema up to this build's version.</summary>
    /// <param name="path">The database file.</param>
    /// <param name="clock">The clock every timestamp comes from.</param>
    /// <param name="quotas">The list and item quotas; the defaults when null.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The open store.</returns>
    /// <exception cref="TodoSchemaException">The database is newer than this build.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A quota is below one.</exception>
    public static async Task<TodoStore> OpenAsync(
        string path,
        TimeProvider? clock = null,
        TodoQuotas? quotas = null,
        CancellationToken cancellationToken = default)
    {
        TodoQuotas limits = quotas ?? new TodoQuotas();
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxLists, 1, nameof(quotas));
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxItems, 1, nameof(quotas));

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        SqliteConnection writer = await ConnectAsync(connectionString, cancellationToken).ConfigureAwait(false);

        try
        {
            await TodoSchema.ApplyAsync(writer, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new TodoStore(writer, connectionString, clock ?? TimeProvider.System, limits);
    }

    /// <summary>Closes the writer connection.</summary>
    /// <returns>A task that completes when it is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writerGate.Dispose();
    }

    /// <summary>Returns the user with this name, creating the row when there is none (§8.3).</summary>
    /// <param name="name">The user name the authenticator proved.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The user's row.</returns>
    public async Task<UserRow> EnsureUserAsync(string name, CancellationToken cancellationToken = default)
    {
        if (await FindUserAsync(name, cancellationToken).ConfigureAwait(false) is UserRow existing)
        {
            return existing;
        }

        long now = Now();
        await WriteAsync(
            "INSERT OR IGNORE INTO users (name, created_at, updated_at) VALUES (@name, @now, @now)",
            command =>
            {
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@now", now);
            },
            cancellationToken).ConfigureAwait(false);

        return await FindUserAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("the user row vanished between insert and read");
    }

    /// <summary>Finds a user by name.</summary>
    /// <param name="name">The user name.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The row, or null when there is none.</returns>
    public async Task<UserRow?> FindUserAsync(string name, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<UserRow> found = await ReadAsync(
            "SELECT id, name, created_at, updated_at FROM users WHERE name = @name",
            command => command.Parameters.AddWithValue("@name", name),
            ReadUser,
            cancellationToken).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    /// <summary>
    /// The user with this name, <b>only</b> when it is the attaching user's own row. This is the
    /// isolation of §8.3: <c>/users</c> resolves a name through a query scoped by the attaching
    /// user's id, not through a name comparison, so deleting the scope is a change to one
    /// <c>WHERE</c> clause and <c>TodoFsIsolationTests</c> catches it (AC-c, RK-52).
    /// </summary>
    /// <param name="attachedUserId">The attaching user's id.</param>
    /// <param name="name">The name being looked up.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The row, or null when it is not this user's own.</returns>
    public async Task<UserRow?> FindVisibleUserAsync(
        long attachedUserId, string name, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<UserRow> found = await ReadAsync(
            "SELECT id, name, created_at, updated_at FROM users WHERE name = @name AND id = @uid",
            command =>
            {
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@uid", attachedUserId);
            },
            ReadUser,
            cancellationToken).ConfigureAwait(false);

        return found.Count == 0 ? null : found[0];
    }

    /// <summary>The users this attach may see, which is its own row and no other.</summary>
    /// <param name="attachedUserId">The attaching user's id.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The rows.</returns>
    public Task<IReadOnlyList<UserRow>> ListVisibleUsersAsync(
        long attachedUserId, CancellationToken cancellationToken = default) =>
        ReadAsync(
            "SELECT id, name, created_at, updated_at FROM users WHERE id = @uid",
            command => command.Parameters.AddWithValue("@uid", attachedUserId),
            ReadUser,
            cancellationToken);

    /// <summary>Every user, in creation order.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The rows.</returns>
    public Task<IReadOnlyList<UserRow>> ListUsersAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(
            "SELECT id, name, created_at, updated_at FROM users ORDER BY id",
            _ => { },
            ReadUser,
            cancellationToken);

    /// <summary>
    /// Removes a user and, by the schema's cascade, every list and item beneath them, in one
    /// transaction.
    /// </summary>
    /// <param name="name">The user to remove.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>True when there was such a user.</returns>
    public async Task<bool> RemoveUserAsync(string name, CancellationToken cancellationToken = default)
    {
        int removed = await WriteAsync(
            "DELETE FROM users WHERE name = @name",
            command => command.Parameters.AddWithValue("@name", name),
            cancellationToken).ConfigureAwait(false);

        return removed > 0;
    }

    private static UserRow ReadUser(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3));

    private static async Task<SqliteConnection> ConnectAsync(
        string connectionString, CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await PragmasAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The three pragmas every connection needs: WAL so that readers do not block the writer,
    /// foreign keys so that the schema's cascades actually cascade (SQLite has them off by
    /// default), and a busy timeout so that a lock contended for a moment waits instead of failing.
    /// </summary>
    /// <param name="connection">The freshly opened connection.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the pragmas are set.</returns>
    private static async Task PragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand pragmas = connection.CreateCommand();
        pragmas.CommandText =
            "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";

        await pragmas.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private long Now() => _clock.GetUtcNow().ToUnixTimeSeconds();

    /// <summary>Runs one statement on the single writer connection, under its gate.</summary>
    /// <param name="sql">The parameterised statement.</param>
    /// <param name="bind">Adds the parameters.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The number of rows affected.</returns>
    private async Task<int> WriteAsync(
        string sql, Action<SqliteCommand> bind, CancellationToken cancellationToken)
    {
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using SqliteCommand command = _writer.CreateCommand();

            // CA2100: every caller of this method passes a constant from this assembly. The only
            // interpolated fragment anywhere in the store is the column name in
            // SetItemFieldAsync, which comes from a closed enum and never from a client; values
            // are always parameters, which is the rule the analyzer is really about.
#pragma warning disable CA2100
            command.CommandText = sql;
#pragma warning restore CA2100
            bind(command);

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    /// <summary>
    /// Runs one insert on the writer connection, under its gate and inside one immediate
    /// transaction with the count that decides whether the insert may happen at all. The count
    /// and the insert see the same snapshot and no other writer can interleave between them, so
    /// two creates racing at the cap yield exactly one row: whichever runs second counts the
    /// first's row and is refused. A refusal rolls the transaction back with nothing written.
    /// </summary>
    /// <param name="countSql">The parameterised count of what the insert would add one to.</param>
    /// <param name="bindCount">Adds the count's parameters.</param>
    /// <param name="max">The most the count may already be for the insert to proceed.</param>
    /// <param name="insertSql">The parameterised insert.</param>
    /// <param name="bindInsert">Adds the insert's parameters.</param>
    /// <param name="refusal">The message of the exception when the count is at the cap.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The number of rows the insert affected.</returns>
    /// <exception cref="TodoQuotaException">The count is already at <paramref name="max"/>.</exception>
    private async Task<int> InsertWithinQuotaAsync(
        string countSql,
        Action<SqliteCommand> bindCount,
        int max,
        string insertSql,
        Action<SqliteCommand> bindInsert,
        string refusal,
        CancellationToken cancellationToken)
    {
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Immediate, not deferred: Microsoft.Data.Sqlite's default isolation level begins the
            // transaction with the write lock taken, so the count below is already the count no
            // other connection can change before the insert commits.
            await using SqliteTransaction transaction = (SqliteTransaction)
                await _writer.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (SqliteCommand count = _writer.CreateCommand())
            {
                count.Transaction = transaction;

                // CA2100: as in WriteAsync, the statement text is a constant of this assembly.
#pragma warning disable CA2100
                count.CommandText = countSql;
#pragma warning restore CA2100
                bindCount(count);

                object? held = await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (Convert.ToInt64(held, CultureInfo.InvariantCulture) >= max)
                {
                    // Disposing the transaction without a commit rolls it back.
                    throw new TodoQuotaException(refusal);
                }
            }

            int affected;
            await using (SqliteCommand insert = _writer.CreateCommand())
            {
                insert.Transaction = transaction;
#pragma warning disable CA2100
                insert.CommandText = insertSql;
#pragma warning restore CA2100
                bindInsert(insert);

                affected = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return affected;
        }
        finally
        {
            _writerGate.Release();
        }
    }

    /// <summary>Runs one query on a pooled read connection, outside the writer's gate.</summary>
    /// <typeparam name="TRow">The row type.</typeparam>
    /// <param name="sql">The parameterised query.</param>
    /// <param name="bind">Adds the parameters.</param>
    /// <param name="project">Turns one reader row into a record.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The rows.</returns>
    private async Task<IReadOnlyList<TRow>> ReadAsync<TRow>(
        string sql,
        Action<SqliteCommand> bind,
        Func<SqliteDataReader, TRow> project,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            await ConnectAsync(_connectionString, cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        // CA2100: as above, the query text is a constant of this assembly and every value is a
        // parameter.
#pragma warning disable CA2100
        command.CommandText = sql;
#pragma warning restore CA2100
        bind(command);

        List<TRow> rows = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(project(reader));
        }

        return rows;
    }
}
