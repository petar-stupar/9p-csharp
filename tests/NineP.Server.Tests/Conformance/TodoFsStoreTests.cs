using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
#if NET10_0_OR_GREATER
using Microsoft.Data.Sqlite;
using NineP.Protocol;
using NineP.TodoFs.Storage;
using Xunit;
using NineP.TestSupport;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// The store of the workspace architecture §7: one writer behind a gate, a read path beside it,
/// the schema's cascades, and the version rule that decides whether a database may be opened at
/// all.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class TodoFsStoreTests : IAsyncLifetime
{
    /// <summary>The sorted outcome of a race at the cap: one refusal (false), one row (true).</summary>
    private static readonly bool[] OneRefusalOneRow = [false, true];

    private string _directory = string.Empty;

    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>Creates the temp directory each test's database lives in.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "todofs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        return ValueTask.CompletedTask;
    }

    /// <summary>Removes the temp directory.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        // The pool holds the file open, so it is released before the directory goes.
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // The temp directory outlives the test rather than failing it.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Fifty writers at once all succeed. One writer connection behind a gate is what makes that
    /// true: two connections writing under WAL meet <c>SQLITE_BUSY</c>, and a busy timeout would
    /// turn the contention into a stall rather than into correctness.
    /// </summary>
    [Fact]
    public async Task ConcurrentWritersSerialise()
    {
        await using TodoStore store = await OpenAsync();
        UserRow user = await store.EnsureUserAsync("glenda", Ct);

        List<Task> writers = [];
        for (int i = 0; i < 50; i++)
        {
            int index = i;
            writers.Add(Task.Run(async () => await store.CreateListAsync(user.Id, index, Ct), Ct));
        }

        await Task.WhenAll(writers);

        IReadOnlyList<ListRow> lists = await store.ListListsAsync(user.Id, Ct);
        Assert.Equal(50, lists.Count);
        Assert.Equal([.. Enumerable.Range(0, 50).Select(i => (long)i)], lists.Select(row => row.Index));
    }

    /// <summary>
    /// Ticket 015 E2, the pattern of <c>BoundaryTests.F26_ConcurrentCreatesHaveExactlyOneWinner</c>
    /// at the store: with one slot left under the quota, two creates released together yield
    /// exactly one row and one <see cref="TodoQuotaException"/>, for lists and for items. The
    /// count runs inside the insert's own transaction under the writer gate; a count taken before
    /// the gate, or on the read path, would let both pass.
    /// <b>Mutation:</b> take the count of <c>TodoStore.InsertWithinQuotaAsync</c> before the gate,
    /// or drop the refusal, and the row counts below read three.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentCreatesAtTheQuotaHaveExactlyOneWinner()
    {
        await using TodoStore store = await TodoStore.OpenAsync(
            Path.Combine(_directory, "todo.sqlite"),
            quotas: new TodoQuotas { MaxLists = 2, MaxItems = 2 },
            cancellationToken: Ct);
        UserRow user = await store.EnsureUserAsync("glenda", Ct);
        ListRow list = await store.CreateListAsync(user.Id, 0, Ct);

        Assert.Equal(
            OneRefusalOneRow,
            await RaceAsync(
                () => store.CreateListAsync(user.Id, 1, Ct),
                () => store.CreateListAsync(user.Id, 2, Ct)));
        Assert.Equal(2, (await store.ListListsAsync(user.Id, Ct)).Count);

        await store.CreateItemAsync(user.Id, list.Id, 0, Ct);

        Assert.Equal(
            OneRefusalOneRow,
            await RaceAsync(
                () => store.CreateItemAsync(user.Id, list.Id, 1, Ct),
                () => store.CreateItemAsync(user.Id, list.Id, 2, Ct)));
        Assert.Equal(2, (await store.ListItemsAsync(user.Id, list.Id, Ct)).Count);
    }

    /// <summary>A database written by a newer build is refused, with a message naming both versions.</summary>
    [Fact]
    public async Task SchemaVersionRefusesNewer()
    {
        string path = Path.Combine(_directory, "newer.sqlite");

        await using (TodoStore store = await TodoStore.OpenAsync(path, cancellationToken: Ct))
        {
            // The store is created at this build's version, then aged forward by hand.
        }

        SqliteConnection.ClearAllPools();
        await using (SqliteConnection connection = new("Data Source=" + path))
        {
            await connection.OpenAsync(Ct);
            await using SqliteCommand bump = connection.CreateCommand();
            bump.CommandText = "UPDATE meta SET schema_version = 99";
            await bump.ExecuteNonQueryAsync(Ct);
        }

        SqliteConnection.ClearAllPools();
        TodoSchemaException refusal = await Assert.ThrowsAsync<TodoSchemaException>(
            async () => await TodoStore.OpenAsync(path, cancellationToken: Ct));

        Assert.Contains("99", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Removing a user removes their lists and every item in them, in one statement.</summary>
    [Fact]
    public async Task CascadeDeleteRemovesListsAndItems()
    {
        await using TodoStore store = await OpenAsync();

        UserRow user = await store.EnsureUserAsync("glenda", Ct);
        ListRow list = await store.CreateListAsync(user.Id, 0, Ct);
        await store.CreateItemAsync(user.Id, list.Id, 0, Ct);
        await store.CreateItemAsync(user.Id, list.Id, 1, Ct);

        Assert.Equal(2, (await store.ListItemsAsync(user.Id, list.Id, Ct)).Count);
        Assert.True(await store.RemoveUserAsync("glenda", Ct));

        UserRow recreated = await store.EnsureUserAsync("glenda", Ct);
        Assert.NotEqual(user.Id, recreated.Id);
        Assert.Empty(await store.ListListsAsync(user.Id, Ct));
        Assert.Empty(await store.ListItemsAsync(user.Id, list.Id, Ct));
    }

    /// <summary>A user who authenticates but has no row is created, and a second call is idempotent.</summary>
    [Fact]
    public async Task EnsureUserIsIdempotent()
    {
        await using TodoStore store = await OpenAsync();

        UserRow first = await store.EnsureUserAsync("glenda", Ct);
        UserRow again = await store.EnsureUserAsync("glenda", Ct);

        Assert.Equal(first.Id, again.Id);
        Assert.Single(await store.ListUsersAsync(Ct));
    }

    /// <summary>
    /// Reference §4.1: a qid is stable across restarts and changes when the row does. The path
    /// carries the table tag in its top byte, so a list and an item with the same row id are still
    /// different files.
    /// </summary>
    [Fact]
    public void QidPathCarriesTheTableTag()
    {
        Qid list = TodoQid.For(TodoQid.ListsTag, 7, 1_700_000_000, QidType.QTDIR);
        Qid item = TodoQid.For(TodoQid.ItemsTag, 7, 1_700_000_000, QidType.QTDIR);

        Assert.NotEqual(list.Path, item.Path);
        Assert.Equal((TodoQid.ListsTag << 56) | 7, (long)list.Path);
        Assert.Equal(1_700_000_000u, list.Version);
        Assert.NotEqual(list.Version, TodoQid.For(TodoQid.ListsTag, 7, 1_700_000_001, QidType.QTDIR).Version);
    }

    /// <summary>Every query is scoped by the owning user, so one user cannot name another's rows.</summary>
    [Fact]
    public async Task QueriesAreScopedByUser()
    {
        await using TodoStore store = await OpenAsync();

        UserRow a = await store.EnsureUserAsync("a", Ct);
        UserRow b = await store.EnsureUserAsync("b", Ct);
        ListRow theirs = await store.CreateListAsync(b.Id, 0, Ct);

        Assert.Null(await store.FindListAsync(a.Id, 0, Ct));
        Assert.Empty(await store.ListListsAsync(a.Id, Ct));
        Assert.Empty(await store.ListItemsAsync(a.Id, theirs.Id, Ct));
        Assert.False(await store.RemoveListAsync(a.Id, theirs.Id, Ct));
        Assert.False(await store.SetListNameAsync(a.Id, theirs.Id, "mine", Ct));
    }

    /// <summary>
    /// Releases two creates together and reports which won, sorted: a refusal is false, a row is
    /// true. Anything but a quota refusal is a failure of the test, not an outcome.
    /// </summary>
    private static async Task<bool[]> RaceAsync(Func<Task> first, Func<Task> second)
    {
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> Create(Func<Task> create)
        {
            await start.Task;
            try
            {
                await create();
                return true;
            }
            catch (TodoQuotaException)
            {
                return false;
            }
        }

        Task<bool> a = Task.Run(() => Create(first), Ct);
        Task<bool> b = Task.Run(() => Create(second), Ct);
        start.SetResult();

        return [.. (await Task.WhenAll(a, b)).Order()];
    }

    private Task<TodoStore> OpenAsync() =>
        TodoStore.OpenAsync(Path.Combine(_directory, "todo.sqlite"), cancellationToken: Ct);
}
#endif
