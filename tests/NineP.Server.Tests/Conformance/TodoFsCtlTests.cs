using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
#if NET10_0_OR_GREATER
using System.Text;
using NineP.Client;
using NineP.Protocol;
using NineP.TodoFs.Storage;
using Xunit;
using NineP.TestSupport;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// <c>/users/ctl</c> (§8.3): reading lists the users, writing takes exactly one command, and both
/// need the realm role <c>--admin-role</c>. Everything else is <c>EINVAL</c>, and everyone else is
/// <c>EACCES</c> — an ordinary user may not even learn who exists.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class TodoFsCtlTests
{
    private static readonly Dictionary<string, string[]> Roles =
        new(StringComparer.Ordinal) { ["root"] = ["todofs-admin"] };

    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>A user without the role is refused on the read and on the write.</summary>
    [Fact]
    public async Task NonAdminGetsEacces()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync(Roles);
        await using NinePSession ordinary = await harness.ConnectAsync("glenda");

        NinePException read = await Assert.ThrowsAsync<NinePException>(
            async () => await ordinary.ReadFileAsync("users/ctl", Ct));
        Assert.Equal(Errno.EACCES, read.Error.Errno);

        NinePException write = await Assert.ThrowsAsync<NinePException>(
            async () => await ordinary.WriteFileAsync("users/ctl", "add bootes\n"u8.ToArray(), Ct));
        Assert.Equal(Errno.EACCES, write.Error.Errno);
    }

    /// <summary>An administrator adds a user, sees it in the listing, and removes it again.</summary>
    [Fact]
    public async Task AddAndRemoveRoundTrip()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync(Roles);
        await using NinePSession admin = await harness.ConnectAsync("root");

        await admin.WriteFileAsync("users/ctl", "add bootes\n"u8.ToArray(), Ct);
        Assert.Contains("bootes", await ReadCtlAsync(admin), StringComparison.Ordinal);
        Assert.NotNull(await harness.Store.FindUserAsync("bootes", Ct));

        await admin.WriteFileAsync("users/ctl", "remove bootes\n"u8.ToArray(), Ct);
        Assert.DoesNotContain("bootes", await ReadCtlAsync(admin), StringComparison.Ordinal);
        Assert.Null(await harness.Store.FindUserAsync("bootes", Ct));
    }

    /// <summary>Removing a user takes their lists and items with them, in one statement.</summary>
    [Fact]
    public async Task RemoveTakesTheUsersListsAndItems()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync(Roles);

        await using (NinePSession glenda = await harness.ConnectAsync("glenda"))
        {
            await glenda.MkdirAsync("users/glenda/0", cancellationToken: Ct);
            await glenda.MkdirAsync("users/glenda/0/0", cancellationToken: Ct);
        }

        UserRow before = await harness.Store.FindUserAsync("glenda", Ct)
            ?? throw new InvalidOperationException();

        await using NinePSession admin = await harness.ConnectAsync("root");
        await admin.WriteFileAsync("users/ctl", "remove glenda\n"u8.ToArray(), Ct);

        Assert.Empty(await harness.Store.ListListsAsync(before.Id, Ct));
    }

    /// <summary>
    /// Anything that is not exactly one legal command is EINVAL. An empty write is not in the
    /// list: a <c>Twrite</c> of no bytes writes nothing, so there is no command to refuse.
    /// </summary>
    /// <param name="command">The bytes written to the control file.</param>
    /// <returns>A task that completes when the refusal has been checked.</returns>
    [Theory]
    [InlineData("add\n")]
    [InlineData("add \n")]
    [InlineData("frobnicate bootes\n")]
    [InlineData("add bootes extra\n")]
    [InlineData("add bootes\nadd glenda\n")]
    [InlineData("ADD bootes\n")]
    public async Task MalformedCommandIsEinval(string command)
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync(Roles);
        await using NinePSession admin = await harness.ConnectAsync("root");

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await admin.WriteFileAsync("users/ctl", Encoding.UTF8.GetBytes(command), Ct));

        Assert.Equal(Errno.EINVAL, refusal.Error.Errno);
    }

    /// <summary>The trailing newline is optional, as it is for <c>status</c>.</summary>
    [Fact]
    public async Task TrailingNewlineIsOptional()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync(Roles);
        await using NinePSession admin = await harness.ConnectAsync("root");

        await admin.WriteFileAsync("users/ctl", "add bootes"u8.ToArray(), Ct);
        Assert.NotNull(await harness.Store.FindUserAsync("bootes", Ct));
    }

    /// <summary>
    /// The listing is sorted bytewise by name, like every other listing in this workspace. It used
    /// to come back in insertion order, which is an order nothing documents and which changes as
    /// users are added and removed.
    /// <b>Mutation:</b> drop the sort in <c>TodoCtl.ReadAsync</c> and the users below come back in
    /// the order they were created.
    /// </summary>
    [Fact]
    public async Task ListingIsSortedByName()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync(Roles);
        await using NinePSession admin = await harness.ConnectAsync("root");

        // Created out of order, and "root" is already there from the attach above.
        foreach (string user in new[] { "zoe", "bootes", "alice" })
        {
            await admin.WriteFileAsync("users/ctl", Encoding.UTF8.GetBytes("add " + user), Ct);
        }

        Assert.Equal("alice\nbootes\nroot\nzoe\n", await ReadCtlAsync(admin));
    }

    /// <summary>
    /// A read answers from the user table, including on the very fid that wrote a command. The
    /// splice buffer a partial write merges into used to answer reads as well, so a fid that
    /// wrote <c>add alice</c> read <c>add alice</c> back — its own command, presented as the
    /// contents of the control file — until the last open of the file closed.
    /// <b>Mutation:</b> answer <c>OpenField.ReadAsync</c> from <c>_state.Value</c> again and the
    /// listing assertion below reads the command back instead.
    /// </summary>
    [Fact]
    public async Task AReadAfterAWriteAnswersTheUserList()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync(Roles);
        await using NinePSession admin = await harness.ConnectAsync("root");

        await using NinePFid ctl = await admin.OpenFileAsync(
            "users/ctl", OpenMode.ReadWrite, OpenFlags.Truncate, Ct);

        byte[] command = "add alice\n"u8.ToArray();
        Assert.Equal(command.Length, await ctl.WriteAsync(0, command, Ct));

        byte[] seen = new byte[64];
        int read = await ctl.ReadAsync(0, seen, Ct);

        Assert.Equal("alice\nroot\n", Encoding.UTF8.GetString(seen, 0, read));

        // And a fid opened afterwards, while this one is still open, sees the same thing.
        Assert.Equal("alice\nroot\n", await ReadCtlAsync(admin));
    }

    private static async Task<string> ReadCtlAsync(NinePSession session) =>
        Encoding.UTF8.GetString(await session.ReadFileAsync("users/ctl", Ct));
}
#endif
