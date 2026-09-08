using NineP.Protocol;
using NineP.Server;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>The tree's root: <c>/users</c> (§8.3).</summary>
internal sealed class TodoRoot(TodoSession session) : TodoDirectory(session)
{
    private const int RootNode = 0;
    private const int UsersNode = 1;

    /// <summary>The root's qid.</summary>
    public override Qid Qid => TodoQid.Fixed(RootNode, QidType.QTDIR);

    /// <summary>Resolves <c>users</c>.</summary>
    /// <param name="name">The entry name.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The child, or null.</returns>
    public override ValueTask<IHandler?> LookupAsync(
        string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IHandler?>(name switch
        {
            "users" => new TodoUsers(Session),
            _ => null,
        });
    }

    /// <summary>The root's entries.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The entries.</returns>
    protected override ValueTask<IReadOnlyList<DirEntry>> EntriesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IReadOnlyList<DirEntry>>(
        [
            new DirEntry("users", TodoQid.Fixed(UsersNode, QidType.QTDIR), FileKind.Directory, 0),
        ]);
    }
}
