using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
#if NET10_0_OR_GREATER
using System.Text;
using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.TodoFs.Storage;
using Xunit;
using NineP.TestSupport;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// The list and item semantics of §8.3: lists and items are created at the next number and nowhere
/// else, <c>status</c> has a vocabulary of two, <c>rmdir</c> removes an item or an empty list, and
/// every field is capped at 64 KiB.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class TodoFsItemTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>A list is created at the next number; any other number is EINVAL.</summary>
    [Fact]
    public async Task MkdirTakesTheNextNumberOnly()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync("glenda");

        await session.MkdirAsync("users/glenda/0", cancellationToken: Ct);
        await session.MkdirAsync("users/glenda/1", cancellationToken: Ct);

        await Einval(async () => await session.MkdirAsync("users/glenda/7", cancellationToken: Ct));
        await Einval(async () => await session.MkdirAsync("users/glenda/01", cancellationToken: Ct));
        await Einval(async () => await session.MkdirAsync("users/glenda/x", cancellationToken: Ct));

        IReadOnlyList<DirEntry> lists = await session.ReadDirAsync("users/glenda", Ct);
        Assert.Equal(["0", "1"], lists.Select(entry => entry.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Rule 19: an item's flags are derived from the row, so a <c>Tcreate</c> asking for an
    /// append-only, exclusive or temporary item is refused whole and no item is made.
    /// </summary>
    [Fact]
    public async Task ACreateAskingForAFlagIsRefused()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync("glenda", Dialect.P9_2000);

        await session.MkdirAsync("users/glenda/0", cancellationToken: Ct);

        NinePFid list = await session.WalkAsync("users/glenda/0", Ct);
        await using (list.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.CreateAsync(
                    new Tcreate(0, list.Fid, "0", ModeBits.DMDIR | ModeBits.DMEXCL | 0x1ED, 0, null), Ct));

            Assert.Equal(Errno.EOPNOTSUPP, refusal.Error.Errno);
        }

        Assert.Equal(
            ["name"],
            (await session.ReadDirAsync("users/glenda/0", Ct)).Select(entry => entry.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>A new list has an empty name file; an item has three files and status "open".</summary>
    [Fact]
    public async Task NewListAndItemStartEmpty()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync("glenda");

        await session.MkdirAsync("users/glenda/0", cancellationToken: Ct);
        Assert.Empty(await session.ReadFileAsync("users/glenda/0/name", Ct));

        await session.MkdirAsync("users/glenda/0/0", cancellationToken: Ct);
        IReadOnlyList<DirEntry> item = await session.ReadDirAsync("users/glenda/0/0", Ct);

        Assert.Equal(
            ["description", "label", "status"],
            item.Select(entry => entry.Name).Order(StringComparer.Ordinal));
        Assert.Equal("open", await TextAsync(session, "users/glenda/0/0/status"));
        Assert.Empty(await session.ReadFileAsync("users/glenda/0/0/label", Ct));
    }

    /// <summary>The list's name and the item's label and description are free text.</summary>
    [Fact]
    public async Task FieldsRoundTrip()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync("glenda");

        await session.MkdirAsync("users/glenda/0", cancellationToken: Ct);
        await session.MkdirAsync("users/glenda/0/0", cancellationToken: Ct);

        await session.WriteFileAsync("users/glenda/0/name", "shopping"u8.ToArray(), Ct);
        await session.WriteFileAsync("users/glenda/0/0/label", "milk"u8.ToArray(), Ct);
        await session.WriteFileAsync("users/glenda/0/0/description", "semi-skimmed"u8.ToArray(), Ct);

        Assert.Equal("shopping", await TextAsync(session, "users/glenda/0/name"));
        Assert.Equal("milk", await TextAsync(session, "users/glenda/0/0/label"));
        Assert.Equal("semi-skimmed", await TextAsync(session, "users/glenda/0/0/description"));
    }

    /// <summary><c>status</c> accepts "open" and "done", with an optional newline, and nothing else.</summary>
    [Fact]
    public async Task StatusRejectsOtherValues()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync("glenda");

        await session.MkdirAsync("users/glenda/0", cancellationToken: Ct);
        await session.MkdirAsync("users/glenda/0/0", cancellationToken: Ct);

        await session.WriteFileAsync("users/glenda/0/0/status", "done"u8.ToArray(), Ct);
        Assert.Equal("done", await TextAsync(session, "users/glenda/0/0/status"));

        await session.WriteFileAsync("users/glenda/0/0/status", "open\n"u8.ToArray(), Ct);
        Assert.Equal("open", await TextAsync(session, "users/glenda/0/0/status"));

        foreach (string refused in new[] { "DONE", "closed", "open done", "opendone", "done x" })
        {
            await Einval(async () => await session.WriteFileAsync(
                "users/glenda/0/0/status", Encoding.UTF8.GetBytes(refused), Ct));
        }

        // The refusals changed nothing.
        Assert.Equal("open", await TextAsync(session, "users/glenda/0/0/status"));
    }

    /// <summary>A field write that would cross the 64 KiB cap is refused rather than truncated.</summary>
    [Fact]
    public async Task FieldWriteCappedAt64KiB()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync("glenda");

        await session.MkdirAsync("users/glenda/0", cancellationToken: Ct);
        await session.MkdirAsync("users/glenda/0/0", cancellationToken: Ct);

        byte[] full = new byte[TodoStore.MaxFieldBytes];
        Array.Fill(full, (byte)'x');
        await session.WriteFileAsync("users/glenda/0/0/label", full, Ct);
        Assert.Equal(TodoStore.MaxFieldBytes, (await session.ReadFileAsync("users/glenda/0/0/label", Ct)).Length);

        await using NinePFid past = await session.OpenFileAsync(
            "users/glenda/0/0/label", OpenMode.Write, OpenFlags.None, Ct);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await past.WriteAsync((ulong)TodoStore.MaxFieldBytes, "y"u8.ToArray(), Ct));

        Assert.Equal(Errno.EFBIG, refusal.Error.Errno);
    }

    /// <summary><c>rmdir</c> removes an item, and an empty list, and nothing else.</summary>
    [Fact]
    public async Task RmdirRemovesItemsAndEmptyLists()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync("glenda");

        await session.MkdirAsync("users/glenda/0", cancellationToken: Ct);
        await session.MkdirAsync("users/glenda/0/0", cancellationToken: Ct);

        NinePException notEmpty = await Assert.ThrowsAsync<NinePException>(
            async () => await session.RemoveAsync("users/glenda/0", Ct));
        Assert.Equal(Errno.ENOTEMPTY, notEmpty.Error.Errno);

        await session.RemoveAsync("users/glenda/0/0", Ct);
        await session.RemoveAsync("users/glenda/0", Ct);

        Assert.Empty(await session.ReadDirAsync("users/glenda", Ct));
    }

    /// <summary>
    /// Two opens of one field are two views of one file, not two files. The reviewer's case, on the
    /// wire: A writes <c>hello</c>, B writes <c>J</c> over its first byte, and A's next read has to
    /// see <c>Jello</c> and its next partial write has to splice into it. While each open kept its
    /// own copy of the field, A read <c>hello</c> after B's write and then wrote <c>!</c> at 5 into
    /// that stale copy, storing <c>hello!</c> — B's change lost, and acknowledged as written.
    /// <b>Mutation:</b> give <c>OpenField</c> its own <c>byte[]? _value</c> again instead of the
    /// file's shared state and the splice assertion below fails. (The read assertion is now pinned
    /// by the row rather than by the sharing: reads answer from the store.)
    /// </summary>
    [Fact]
    public async Task TwoOpensOfOneFieldSeeOneFile()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync("glenda");

        await session.MkdirAsync("users/glenda/0", cancellationToken: Ct);
        await session.MkdirAsync("users/glenda/0/0", cancellationToken: Ct);

        const string Label = "users/glenda/0/0/label";
        await using NinePFid a = await session.OpenFileAsync(Label, OpenMode.ReadWrite, OpenFlags.None, Ct);
        await using NinePFid b = await session.OpenFileAsync(Label, OpenMode.ReadWrite, OpenFlags.None, Ct);

        Assert.Equal(5, await a.WriteAsync(0, "hello"u8.ToArray(), Ct));
        Assert.Equal(1, await b.WriteAsync(0, "J"u8.ToArray(), Ct));

        // The row is what a third reader sees, and it is what A must see as well.
        Assert.Equal("Jello", await TextAsync(session, Label));

        byte[] seen = new byte[16];
        int read = await a.ReadAsync(0, seen, Ct);
        Assert.Equal("Jello", Encoding.UTF8.GetString(seen, 0, read));

        // And A's next partial write splices into B's value rather than into a copy of its own.
        Assert.Equal(1, await a.WriteAsync(5, "!"u8.ToArray(), Ct));
        Assert.Equal("Jello!", await TextAsync(session, Label));
    }

    /// <summary>
    /// A read answers from the row, including on the fid that wrote. <c>status</c> stores
    /// <c>done</c> for a written <c>done\n</c>, and the splice buffer used to answer reads too, so
    /// that fid read the trailing newline back — bytes the file does not hold — until the last
    /// open of the field closed.
    /// <b>Mutation:</b> answer <c>OpenField.ReadAsync</c> from <c>_state.Value</c> again and both
    /// assertions below come back with the newline.
    /// </summary>
    [Fact]
    public async Task AReadAfterAWriteAnswersTheStoredValue()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync("glenda");

        await session.MkdirAsync("users/glenda/0", cancellationToken: Ct);
        await session.MkdirAsync("users/glenda/0/0", cancellationToken: Ct);

        await using NinePFid status = await session.OpenFileAsync(
            "users/glenda/0/0/status", OpenMode.ReadWrite, OpenFlags.Truncate, Ct);

        Assert.Equal(5, await status.WriteAsync(0, "done\n"u8.ToArray(), Ct));

        byte[] seen = new byte[16];
        int read = await status.ReadAsync(0, seen, Ct);

        Assert.Equal("done", Encoding.UTF8.GetString(seen, 0, read));
        Assert.Equal(4UL, await status.GetAttrAsync(Ct) is { } attr ? attr.Size : 0UL);
    }

    private static async Task Einval(Func<Task> write)
    {
        NinePException refusal = await Assert.ThrowsAsync<NinePException>(write);
        Assert.Equal(Errno.EINVAL, refusal.Error.Errno);
    }

    private static async Task<string> TextAsync(NinePSession session, string path) =>
        Encoding.UTF8.GetString(await session.ReadFileAsync(path, Ct));
}
#endif
