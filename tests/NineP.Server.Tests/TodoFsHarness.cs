#if NET10_0_OR_GREATER
using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Server;
using NineP.TodoFs;
using NineP.TodoFs.Storage;
using Xunit;
using NineP.TestSupport;

namespace NineP.Server.Tests;

/// <summary>
/// A todofs tree on a temp database, served in process. The realm roles that decide who may use
/// <c>/users/ctl</c> come from the authenticator in production; here they are supplied directly,
/// so the tests are about the tree and not about a token.
/// </summary>
internal sealed class TodoFsHarness : IAsyncDisposable
{
    private readonly ServerHarness _harness;
    private readonly TodoStore _store;
    private readonly string _directory;

    private TodoFsHarness(ServerHarness harness, TodoStore store, string directory)
    {
        _harness = harness;
        _store = store;
        _directory = directory;
    }

    /// <summary>The store behind the tree, for assertions about rows.</summary>
    public TodoStore Store => _store;

    /// <summary>Starts a server over a fresh database.</summary>
    /// <param name="roles">Realm roles per user name; a user not named here has none.</param>
    /// <returns>The running harness.</returns>
    public static async Task<TodoFsHarness> StartAsync(IReadOnlyDictionary<string, string[]>? roles = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "todofs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        TodoStore store = await TodoStore.OpenAsync(
            Path.Combine(directory, "todo.sqlite"), cancellationToken: TestDeadlines.Wrap(TestContext.Current.CancellationToken));

        TodoFilesystem tree = new(store);

        // CA2000: the harness returned here owns the server and disposes it with the store.
#pragma warning disable CA2000
        ServerHarness harness = await ServerHarness.StartAsync(
            filesystem: new RoleFilesystem(
                tree, roles ?? new Dictionary<string, string[]>(StringComparer.Ordinal)));
#pragma warning restore CA2000

        return new TodoFsHarness(harness, store, directory);
    }

    /// <summary>Connects and attaches as one user.</summary>
    /// <param name="user">The user name to claim.</param>
    /// <param name="dialect">The dialect to negotiate.</param>
    /// <returns>The attached session; the caller disposes it.</returns>
    public Task<NinePSession> ConnectAsync(string user, Dialect dialect = Dialect.P9_2000_L) =>
        _harness.ConnectAsync(dialect, options => options with { Uname = user });

    /// <summary>Stops the server and removes the database.</summary>
    /// <returns>A task that completes when everything is gone.</returns>
    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        await _store.DisposeAsync();

        // SqliteConnection.ClearAllPools() used to run here. It is process-wide: it closes the
        // pooled connections of every other todofs test running beside this one, which surfaces as
        // an ObjectDisposedException from SQLitePCL in whichever of them was mid-query. The store
        // this harness owns has already been disposed, and a temp file that cannot be deleted is
        // handled below.
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // The temp directory outlives the test rather than failing it.
        }
    }

    /// <summary>
    /// Stands in for the authenticator: it gives the attaching identity the realm roles the test
    /// configured for its name and hands the tree an identity an authenticator produced rather
    /// than the claim the core built from the uname.
    /// </summary>
    private sealed class RoleFilesystem(IFilesystem inner, IReadOnlyDictionary<string, string[]> roles)
        : IFilesystem
    {
        public ValueTask<IDirectoryHandler> AttachAsync(
            Identity identity, string aname, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identity);

            string[] groups = roles.TryGetValue(identity.User, out string[]? found) ? found : [];
            Identity attaching = new()
            {
                User = identity.User,
                Uid = identity.Uid,
                Groups = groups,
                Claims = identity.Claims,
            };

            return inner.AttachAsync(attaching, aname, cancellationToken);
        }
    }
}
#endif
