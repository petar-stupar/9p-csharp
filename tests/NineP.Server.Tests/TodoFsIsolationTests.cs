#if NET10_0_OR_GREATER
using NineP.Client;
using NineP.Protocol;
using NineP.TodoFs.Storage;
using Xunit;
using NineP.TestSupport;

namespace NineP.Server.Tests;

/// <summary>
/// AC-c: one user's attach cannot reach anything under another user's directory. The isolation is
/// enforced at the handler boundary by a query scoped to the attaching user's id, not by a name
/// comparison, and the mutation below is what proves the query is load-bearing.
/// </summary>
public sealed class TodoFsIsolationTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Walk, read, stat and list are all refused across the user boundary.
    /// <b>Mutation:</b> deleting <c>AND id = @uid</c> from <c>TodoStore.FindVisibleUserAsync</c>
    /// lets A walk into B's directory and this test fails on the very first assertion.
    /// </summary>
    [Fact]
    public async Task UserACannotWalkReadStatOrListUserB()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();

        await using (NinePSession b = await harness.ConnectAsync("b"))
        {
            await b.MkdirAsync("users/b/0", cancellationToken: Ct);
            await b.WriteFileAsync("users/b/0/name", "b's list"u8.ToArray(), Ct);
            await b.MkdirAsync("users/b/0/0", cancellationToken: Ct);
        }

        await using NinePSession a = await harness.ConnectAsync("a");

        await Refused(async () => await a.WalkAsync("users/b", Ct));
        await Refused(async () => await a.GetAttrAsync("users/b", Ct));
        await Refused(async () => await a.GetAttrAsync("users/b/0/name", Ct));
        await Refused(async () => await a.ReadFileAsync("users/b/0/name", Ct));
        await Refused(async () => await a.ReadDirAsync("users/b", Ct));
        await Refused(async () => await a.ReadDirAsync("users/b/0", Ct));

        // A's own /users lists exactly one user directory beside the control file.
        IReadOnlyList<DirEntry> visible = await a.ReadDirAsync("users", Ct);
        Assert.Equal(["a", "ctl"], visible.Select(entry => entry.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>The same rule under the name the rule index gives it.</summary>
    [Fact]
    public async Task UserACannotReachUserB()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();

        await using (NinePSession b = await harness.ConnectAsync("b"))
        {
            await b.MkdirAsync("users/b/0", cancellationToken: Ct);
        }

        await using NinePSession a = await harness.ConnectAsync("a");
        await Refused(async () => await a.WalkAsync("users/b/0", Ct));
    }

    /// <summary>
    /// The store refuses the same reaches directly, so the isolation does not depend on the tree
    /// having refused the walk first.
    /// </summary>
    [Fact]
    public async Task StoreQueriesAreScopedEvenWhenTheRowIdIsKnown()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();

        await using (NinePSession b = await harness.ConnectAsync("b"))
        {
            await b.MkdirAsync("users/b/0", cancellationToken: Ct);
            await b.MkdirAsync("users/b/0/0", cancellationToken: Ct);
        }

        UserRow a = await harness.Store.EnsureUserAsync("a", Ct);
        UserRow b2 = await harness.Store.FindUserAsync("b", Ct) ?? throw new InvalidOperationException();
        ListRow theirs = (await harness.Store.ListListsAsync(b2.Id, Ct))[0];

        Assert.Null(await harness.Store.FindListAsync(a.Id, theirs.Index, Ct));
        Assert.Empty(await harness.Store.ListItemsAsync(a.Id, theirs.Id, Ct));
        Assert.Null(await harness.Store.FindItemAsync(a.Id, theirs.Id, 0, Ct));
        Assert.False(await harness.Store.RemoveListAsync(a.Id, theirs.Id, Ct));
    }

    private static async Task Refused(Func<Task> reach)
    {
        NinePException refusal = await Assert.ThrowsAsync<NinePException>(reach);
        Assert.Contains(refusal.Error.Errno, new[] { Errno.ENOENT, Errno.EACCES });
    }
}
#endif
