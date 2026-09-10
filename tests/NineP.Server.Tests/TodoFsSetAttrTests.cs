#if NET10_0_OR_GREATER
using System.Text;
using NineP.Client;
using NineP.Protocol;
using Xunit;
using NineP.TestSupport;

namespace NineP.Server.Tests;

/// <summary>
/// What a <c>Twstat</c>, a <c>Tsetattr</c> and an <c>OTRUNC</c> open do to the todofs tree, under
/// reference §8 rule 27: an update is applied whole or refused whole, and a truncation that is not
/// performed is refused rather than answered with success. Every one of these used to be answered
/// <c>Rwstat</c> / <c>Rsetattr</c> with the row untouched, because the handler treated
/// <c>Size = 0</c> as "nothing to do" and dropped whatever field rode along with it.
/// </summary>
public sealed class TodoFsSetAttrTests
{
    private static readonly Dictionary<string, string[]> Roles =
        new(StringComparer.Ordinal) { ["root"] = ["todofs-admin"] };

    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 27: a length of zero on a free-text field truly empties it, so the success the client
    /// is told about is one the row agrees with.
    /// </summary>
    /// <param name="path">The field to truncate, under the item created by the test.</param>
    /// <returns>A task that completes when the field has been checked.</returns>
    [Theory]
    [InlineData("users/glenda/0/name")]
    [InlineData("users/glenda/0/0/label")]
    [InlineData("users/glenda/0/0/description")]
    public async Task SetAttrLengthZeroEmptiesAFreeTextField(string path)
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync("glenda");

        await MakeItemAsync(session);
        await session.WriteFileAsync(path, "something"u8.ToArray(), Ct);
        Assert.Equal("something", await TextAsync(session, path));

        await session.SetAttrAsync(path, new SetAttr { Size = 0 }, Ct);

        Assert.Empty(await session.ReadFileAsync(path, Ct));
    }

    /// <summary>
    /// Rule 27: <c>status</c> has a vocabulary of two and the schema's <c>CHECK</c> forbids
    /// anything else, so it has no zero-length value and the truncation is refused rather than
    /// answered with success. The row is what it was.
    /// </summary>
    [Fact]
    public async Task SetAttrLengthZeroOnStatusIsRefused()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync("glenda");

        await MakeItemAsync(session);
        await session.WriteFileAsync("users/glenda/0/0/status", "done"u8.ToArray(), Ct);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await session.SetAttrAsync(
                "users/glenda/0/0/status", new SetAttr { Size = 0 }, Ct));

        Assert.Equal(Errno.EINVAL, refusal.Error.Errno);
        Assert.Equal("done", await TextAsync(session, "users/glenda/0/0/status"));
    }

    /// <summary>
    /// Rule 27: <c>/users/ctl</c>'s length is the rendering of the user table and not a value a
    /// client may set, so a length of zero is refused. Emptying it by deleting every user is not
    /// what a truncation means, and answering success while the listing stands is the lie the rule
    /// forbids.
    /// </summary>
    [Fact]
    public async Task SetAttrLengthZeroOnTheControlFileIsRefused()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync(Roles);
        await using NinePSession admin = await harness.ConnectAsync("root");

        await admin.WriteFileAsync("users/ctl", "add bootes\n"u8.ToArray(), Ct);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await admin.SetAttrAsync("users/ctl", new SetAttr { Size = 0 }, Ct));

        Assert.Equal(Errno.EINVAL, refusal.Error.Errno);
        Assert.NotNull(await harness.Store.FindUserAsync("bootes", Ct));
    }

    /// <summary>
    /// Rule 27: a field todofs cannot honour refuses the whole update, so the truncation beside it
    /// changes nothing. Every attribute but the length is derived from the row and the attaching
    /// identity, and an update naming one used to be answered with success and dropped.
    /// <b>Mutation:</b> drop the <c>NamesADerivedField</c> check from <c>TodoFile.SetAttrAsync</c>
    /// and the "still there" assertion below fails for every row.
    /// </summary>
    /// <param name="update">An update naming a truncation and one field todofs does not have.</param>
    /// <returns>A task that completes when the refusal has been checked.</returns>
    [Theory]
    [MemberData(nameof(UnsupportedUpdates))]
    public async Task SetAttrWithAnUnsupportedFieldChangesNothing(SetAttr update)
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();

        // A name reaches a handler only through a Twstat, so these run in 9P2000.
        await using NinePSession session = await harness.ConnectAsync("glenda", Dialect.P9_2000);

        await MakeItemAsync(session);
        await session.WriteFileAsync("users/glenda/0/0/label", "milk"u8.ToArray(), Ct);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await session.SetAttrAsync("users/glenda/0/0/label", update, Ct));

        Assert.Equal(Errno.EOPNOTSUPP, refusal.Error.Errno);
        Assert.Equal("milk", await TextAsync(session, "users/glenda/0/0/label"));
    }

    /// <summary>Updates that name a truncation and one field todofs cannot hold.</summary>
    /// <returns>One update per row.</returns>
    public static TheoryData<SetAttr> UnsupportedUpdates() =>
    [
        new SetAttr { Size = 0, Perm = 0x1FF, Flags = FileFlags.None },
        new SetAttr { Size = 0, Perm = 0x1B6, Flags = FileFlags.Append },
        new SetAttr { Size = 0, GroupName = "wheel" },
        new SetAttr { Size = 0, MTime = new TimeSpec(1, 0) },
        new SetAttr { Size = 0, Name = "renamed" },
    ];

    /// <summary>
    /// Rule 27: a length a todofs column cannot be given is refused. A column is text, not a
    /// buffer padded out to a byte count, so only zero is representable at all; a directory's
    /// length is not a client's to set either (stat(5)).
    /// </summary>
    [Fact]
    public async Task SetAttrRefusesALengthItCannotProduce()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync("glenda");

        await MakeItemAsync(session);
        await session.WriteFileAsync("users/glenda/0/0/label", "milk"u8.ToArray(), Ct);

        NinePException grown = await Assert.ThrowsAsync<NinePException>(
            async () => await session.SetAttrAsync(
                "users/glenda/0/0/label", new SetAttr { Size = 64 }, Ct));
        Assert.Equal(Errno.EOPNOTSUPP, grown.Error.Errno);

        NinePException directory = await Assert.ThrowsAsync<NinePException>(
            async () => await session.SetAttrAsync(
                "users/glenda/0/0", new SetAttr { Size = 8 }, Ct));
        Assert.Equal(Errno.EISDIR, directory.Error.Errno);

        Assert.Equal("milk", await TextAsync(session, "users/glenda/0/0/label"));
    }

    /// <summary>
    /// Rule 27: a directory has no attribute a client may set, so an update naming one is refused
    /// whole rather than answered with success. It used to be answered with success whenever a
    /// length of zero was set beside it.
    /// </summary>
    [Fact]
    public async Task SetAttrOnADirectoryIsRefused()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync("glenda");

        await MakeItemAsync(session);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await session.SetAttrAsync(
                "users/glenda/0", new SetAttr { Size = 0, Perm = 0x1FF }, Ct));

        Assert.Equal(Errno.EOPNOTSUPP, refusal.Error.Errno);
    }

    /// <summary>
    /// Rule 27, the <c>OTRUNC</c> half: an open that asks for a truncation performs it when the
    /// open is answered, so a fid clunked without a write leaves the field empty. It used to leave
    /// the row exactly as it was and answer the open with success anyway.
    /// <b>Mutation:</b> stop calling <c>TruncateAsync</c> from <c>TodoFile.OpenAsync</c> and the
    /// assertion below reads <c>milk</c> back.
    /// </summary>
    [Fact]
    public async Task ATruncatingOpenClunkedWithoutAWriteEmptiesTheField()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync("glenda");

        await MakeItemAsync(session);
        await session.WriteFileAsync("users/glenda/0/0/label", "milk"u8.ToArray(), Ct);

        await using (NinePFid truncating = await session.OpenFileAsync(
            "users/glenda/0/0/label", OpenMode.Write, OpenFlags.Truncate, Ct))
        {
            // Nothing is written: the truncation is the whole of what this open asked for.
        }

        Assert.Empty(await session.ReadFileAsync("users/glenda/0/0/label", Ct));
    }

    /// <summary>
    /// Rule 27, the <c>OTRUNC</c> half for a file with a vocabulary. <c>status</c> has no
    /// zero-length value, so its empty state is the value a new item carries, <c>open</c>: the
    /// truncation is performed at the open and a fid clunked without a write leaves a defined
    /// value rather than the old one. The open is not refused, because <c>echo done &gt; status</c>
    /// and <c>ninep write .../status</c> both open with <c>OTRUNC</c> — which the two writes below
    /// stand for.
    /// </summary>
    [Fact]
    public async Task ATruncatingOpenOfStatusResetsItToOpen()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync("glenda");

        await MakeItemAsync(session);
        await session.WriteFileAsync("users/glenda/0/0/status", "done"u8.ToArray(), Ct);
        Assert.Equal("done", await TextAsync(session, "users/glenda/0/0/status"));

        await using (NinePFid truncating = await session.OpenFileAsync(
            "users/glenda/0/0/status", OpenMode.Write, OpenFlags.Truncate, Ct))
        {
            // Nothing is written.
        }

        Assert.Equal("open", await TextAsync(session, "users/glenda/0/0/status"));

        // And the ordinary truncate-then-write still stores what was written.
        await session.WriteFileAsync("users/glenda/0/0/status", "done\n"u8.ToArray(), Ct);
        Assert.Equal("done", await TextAsync(session, "users/glenda/0/0/status"));
    }

    /// <summary>
    /// The control file keeps no bytes of its own — its read side renders the user table and its
    /// write side takes one command — so a truncating open removes nothing and, in particular,
    /// does not remove the users. This is the one place where an <c>OTRUNC</c> open of this tree
    /// changes no row, and it is documented as such in <c>docs/examples.md</c>.
    /// </summary>
    [Fact]
    public async Task ATruncatingOpenOfTheControlFileKeepsTheUsers()
    {
        await using TodoFsHarness harness = await TodoFsHarness.StartAsync(Roles);
        await using NinePSession admin = await harness.ConnectAsync("root");

        await admin.WriteFileAsync("users/ctl", "add bootes\n"u8.ToArray(), Ct);

        await using (NinePFid truncating = await admin.OpenFileAsync(
            "users/ctl", OpenMode.Write, OpenFlags.Truncate, Ct))
        {
            // Nothing is written.
        }

        Assert.Equal("bootes\nroot\n", await TextAsync(admin, "users/ctl"));
    }

    private static async Task MakeItemAsync(NinePSession session)
    {
        await session.MkdirAsync("users/glenda/0", cancellationToken: Ct);
        await session.MkdirAsync("users/glenda/0/0", cancellationToken: Ct);
    }

    private static async Task<string> TextAsync(NinePSession session, string path) =>
        Encoding.UTF8.GetString(await session.ReadFileAsync(path, Ct));
}
#endif
