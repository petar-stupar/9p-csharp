using NineP.Protocol.Auth;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>
/// One attach's state: who it runs as, which row that is, and whether it may use <c>/users/ctl</c>.
/// The identity is the authenticator's answer and it is fixed for the life of the attach.
/// </summary>
internal sealed class TodoSession
{
    /// <summary>Starts a session for one attach.</summary>
    /// <param name="store">The database behind the tree.</param>
    /// <param name="identity">The identity the attach ran as.</param>
    /// <param name="user">That identity's row, or null when it has none.</param>
    /// <param name="adminRole">The realm role that may use <c>/users/ctl</c>.</param>
    /// <param name="clock">The clock reported times come from.</param>
    /// <param name="fields">
    /// The open fields of the whole tree, which every attach shares: two opens of one field are two
    /// views of one file and not two files. A session given none keeps its own, which is a session
    /// alone in a process and not what the server composes.
    /// </param>
    public TodoSession(
        TodoStore store,
        Identity identity,
        UserRow? user,
        string adminRole,
        TimeProvider clock,
        TodoFieldStates? fields = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(adminRole);
        ArgumentNullException.ThrowIfNull(clock);

        Store = store;
        Clock = clock;
        Fields = fields ?? new TodoFieldStates();

        // Keycloak's realm roles arrive as the identity's groups (§8.3), so the admin verdict is
        // one membership test taken once at the attach and not a claim parse per request.
        View = new TodoView(identity, user, identity.Groups.Contains(adminRole, StringComparer.Ordinal));
    }

    /// <summary>The database behind the tree.</summary>
    public TodoStore Store { get; }

    /// <summary>The clock reported times come from.</summary>
    public TimeProvider Clock { get; }

    /// <summary>The open fields of the tree, shared by every attach this server serves.</summary>
    public TodoFieldStates Fields { get; }

    /// <summary>What every fid of this attach may see.</summary>
    public TodoView View { get; }
}
