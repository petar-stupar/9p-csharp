using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NineP.TodoFs.Storage;

/// <summary>
/// The schema of the workspace architecture §7, and the one rule about versions: this build
/// creates the schema when the database is empty and refuses to open a database whose
/// <c>schema_version</c> is greater than its own. Backward compatibility is therefore "same
/// version or refuse", which <c>docs/examples.md</c> records; 0.1.0 needs no migration because
/// there is no earlier version to migrate from.
/// </summary>
internal static class TodoSchema
{
    /// <summary>The schema version this build writes and accepts.</summary>
    public const long Version = 1;

    /// <summary>The DDL, exactly as the architecture writes it.</summary>
    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS meta (
            schema_version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS users (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            name       TEXT    NOT NULL UNIQUE,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS lists (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            idx        INTEGER NOT NULL,
            name       TEXT    NOT NULL DEFAULT '',
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            UNIQUE (user_id, idx)
        );

        CREATE TABLE IF NOT EXISTS items (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            list_id     INTEGER NOT NULL REFERENCES lists(id) ON DELETE CASCADE,
            idx         INTEGER NOT NULL,
            label       TEXT    NOT NULL DEFAULT '',
            description TEXT    NOT NULL DEFAULT '',
            status      TEXT    NOT NULL DEFAULT 'open' CHECK (status IN ('open','done')),
            created_at  INTEGER NOT NULL,
            updated_at  INTEGER NOT NULL,
            UNIQUE (list_id, idx)
        );

        CREATE INDEX IF NOT EXISTS idx_lists_user ON lists(user_id);
        CREATE INDEX IF NOT EXISTS idx_items_list ON items(list_id);
        """;

    /// <summary>Creates the schema when it is absent and checks the version when it is not.</summary>
    /// <param name="connection">An open writer connection.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the database is ready to serve.</returns>
    /// <exception cref="TodoSchemaException">The database is newer than this build.</exception>
    public static async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using (SqliteCommand ddl = connection.CreateCommand())
        {
            ddl.CommandText = Ddl;
            await ddl.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long? found = await ReadVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (found is null)
        {
            await WriteVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (found > Version)
        {
            throw new TodoSchemaException(string.Format(
                CultureInfo.InvariantCulture,
                "the database is at schema version {0}; this build of todofs understands {1}",
                found,
                Version));
        }
    }

    private static async Task<long?> ReadVersionAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand read = connection.CreateCommand();
        read.CommandText = "SELECT schema_version FROM meta LIMIT 1";

        object? value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task WriteVersionAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand write = connection.CreateCommand();
        write.CommandText = "INSERT INTO meta (schema_version) VALUES (@version)";
        write.Parameters.AddWithValue("@version", Version);

        await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
