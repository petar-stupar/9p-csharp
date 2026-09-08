using NineP.Protocol.Auth;
using NineP.Server;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>
/// The todofs tree (§8.3). One attach, one user: the root handler this returns can reach only rows
/// scoped by the attaching user's id, and there is no path through the tree to anyone else's.
/// </summary>
internal sealed class TodoFilesystem : IFilesystem
{
    private readonly TodoStore _store;
    private readonly TodoFsSettings _settings;
    private readonly TimeProvider _clock;

    // One registry for the whole tree: an open field is one file however many attaches have it
    // open, so a write through one open is what the next read of another open sees.
    private readonly TodoFieldStates _fields = new();

    /// <summary>Serves a database.</summary>
    /// <param name="store">The store behind the tree.</param>
    /// <param name="settings">The settings the tree needs.</param>
    /// <param name="clock">The clock reported times come from.</param>
    public TodoFilesystem(TodoStore store, TodoFsSettings? settings = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _settings = settings ?? new TodoFsSettings();
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Returns the root for this identity, creating the user's row if it has none.</summary>
    /// <param name="identity">Who the session runs as; the authenticator's answer, never a claim.</param>
    /// <param name="aname">Ignored: todofs serves one tree.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The root directory handler.</returns>
    public async ValueTask<IDirectoryHandler> AttachAsync(
        Identity identity, string aname, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // §8.3: a user who authenticates but has no row is created. An identity no authenticator
        // produced is given no row, so it owns nothing and sees nothing.
        UserRow? user = IsUnproven(identity)
            ? null
            : await _store.EnsureUserAsync(identity.User, cancellationToken).ConfigureAwait(false);

        TodoSession session = new(_store, identity, user, _settings.AdminRole, _clock, _fields);

        return new TodoRoot(session);
    }

    /// <summary>
    /// Whether an attach has proved nothing. A <c>Tattach</c> with <c>afid = NOFID</c> carries the
    /// <b>claimed</b> uname, and the identity must come from the token and from nothing else: an
    /// identity no authenticator produced is unproven whatever it calls itself.
    /// </summary>
    /// <param name="identity">The identity the attach ran as.</param>
    /// <returns>True when the session owns no row and sees nothing under <c>/users</c>.</returns>
    private static bool IsUnproven(Identity identity) =>
        !identity.IsAuthenticated || identity.User.Length == 0;
}
