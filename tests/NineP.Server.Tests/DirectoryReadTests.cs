using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests;

/// <summary>
/// Directory reads in both record formats (§6.7): the 9P2000 offset rule, the .L cookie rule, the
/// count clamp, and the promise that a record is never split across replies.
/// </summary>
public sealed class DirectoryReadTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// read(5): the offset of a directory read must be 0 or the previous offset plus the previous
    /// count. A byte offset into a directory has no meaning a server can seek to, so anything else
    /// is <c>"bad offset"</c> rather than a silently wrong listing.
    /// </summary>
    [Fact]
    public async Task BadOffsetRejected()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid directory = await session.OpenFileAsync("/", OpenMode.Read, OpenFlags.None, Ct);
        await using (directory.ConfigureAwait(false))
        {
            Rread first = await session.Messages.ReadAsync(new Tread(0, directory.Fid, 0, 8168), Ct);
            Assert.NotEmpty(first.Data.ToArray());

            NinePException bad = await Assert.ThrowsAsync<NinePException>(async () =>
                await session.Messages.ReadAsync(new Tread(0, directory.Fid, 3, 8168), Ct));

            Assert.Equal("bad offset", bad.Error.Ename);

            // The resumable offset is still accepted afterwards: a refusal changes nothing.
            Rread resumed = await session.Messages
                .ReadAsync(new Tread(0, directory.Fid, (ulong)first.Data.Length, 8168), Ct);
            Assert.Empty(resumed.Data.ToArray());
        }
    }

    /// <summary>
    /// §6.7 and RK-30: a stat record is never split. With a budget just under one record the
    /// server answers <c>ERANGE</c> rather than an empty reply the client would read as the end of
    /// the directory; with a budget of exactly one record it sends exactly one.
    /// </summary>
    [Fact]
    public async Task RecordNeverSplit()
    {
        MemoryFilesystem tree = new();
        tree.Root.Add(tree.NewFile("one", 0x1A4));
        tree.Root.Add(tree.NewFile("two", 0x1A4));

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid directory = await session.OpenFileAsync("/", OpenMode.Read, OpenFlags.None, Ct);
        await using (directory.ConfigureAwait(false))
        {
            Rread whole = await session.Messages.ReadAsync(new Tread(0, directory.Fid, 0, 8168), Ct);
            int both = whole.Data.Length;
            int one = OneRecord(whole.Data.Span);

            // Exactly one record fits: one record comes back, never one and a fragment.
            Rread single = await session.Messages
                .ReadAsync(new Tread(0, directory.Fid, 0, (uint)one), Ct);
            Assert.Equal(one, single.Data.Length);
            Assert.True(both > one);

            // One byte less than a record: the reply would have to be empty, which would mean
            // "end of directory". The server says ERANGE instead.
            NinePException tooSmall = await Assert.ThrowsAsync<NinePException>(async () =>
                await session.Messages.ReadAsync(new Tread(0, directory.Fid, 0, (uint)(one - 1)), Ct));

            Assert.Equal(Errno.ERANGE, tooSmall.Error.Errno);
        }
    }

    /// <summary>
    /// S-26: <c>msize - IOHDRSZ</c> is a service bound. A count above it is clamped silently and
    /// never rejected — the client asked for more than one message can carry, not for something
    /// illegal.
    /// </summary>
    [Fact]
    public async Task CountClampedNotRejected()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid directory = await session.OpenFileAsync("/", OpenMode.Read, OpenFlags.None, Ct);
        await using (directory.ConfigureAwait(false))
        {
            Rread reply = await session.Messages
                .ReadAsync(new Tread(0, directory.Fid, 0, uint.MaxValue), Ct);

            Assert.NotEmpty(reply.Data.ToArray());
            Assert.True(reply.Data.Length <= (int)session.Msize - Constants.IOHDRSZ);
        }
    }

    /// <summary>Reference §5.9: in a .L session a directory is read with <c>Treaddir</c> alone.</summary>
    [Fact]
    public async Task TreadOnDirIsErrorInDotL()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid directory = await session.OpenFileAsync("/", OpenMode.Read, OpenFlags.None, Ct);
        await using (directory.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(async () =>
                await session.Messages.ReadAsync(new Tread(0, directory.Fid, 0, 4096), Ct));

            Assert.Equal(Errno.EISDIR, refusal.Error.Errno);
        }
    }

    /// <summary>S-27: neither record format ever carries "." or "..".</summary>
    /// <param name="dialect">The dialect the listing comes back in.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(Dialect.P9_2000)]
    [InlineData(Dialect.P9_2000_u)]
    [InlineData(Dialect.P9_2000_L)]
    public async Task NoDotEntries(Dialect dialect)
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(dialect);

        IReadOnlyList<DirEntry> entries = await session.ReadDirAsync("/", Ct);

        Assert.Equal(["hello.txt", "sub"], entries.Select(entry => entry.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Name is "." or "..");
    }

    /// <summary>
    /// Reference §4.3: an <c>Rreaddir</c> offset is the cookie of the last entry returned, so a
    /// second read starting from it resumes <b>after</b> that entry and never repeats it.
    /// </summary>
    [Fact]
    public async Task ReaddirCookieResumesAfterEntry()
    {
        MemoryFilesystem tree = new();
        for (int i = 0; i < 6; i++)
        {
            tree.Root.Add(tree.NewFile("f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), 0x1A4));
        }

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid directory = await session.OpenFileAsync("/", OpenMode.Read, OpenFlags.None, Ct);
        await using (directory.ConfigureAwait(false))
        {
            // A budget that holds two entries and not three.
            Rreaddir first = await session.Messages.ReaddirAsync(new Treaddir(0, directory.Fid, 0, 64), Ct);
            IReadOnlyList<DirEntry> head = Decode(first.Data);
            Assert.NotEmpty(head);

            Rreaddir second = await session.Messages
                .ReaddirAsync(new Treaddir(0, directory.Fid, head[^1].Cursor, 64), Ct);
            IReadOnlyList<DirEntry> tail = Decode(second.Data);

            Assert.DoesNotContain(tail, entry => head.Any(seen => seen.Name == entry.Name));

            // The listing ends with a count of zero, not with a repeat of the last page.
            Rreaddir end = await session.Messages.ReaddirAsync(new Treaddir(0, directory.Fid, 6, 64), Ct);
            Assert.Empty(end.Data.ToArray());
        }
    }

    private static IReadOnlyList<DirEntry> Decode(ReadOnlyMemory<byte> payload)
    {
        Assert.True(NineP.Protocol.Codec.Internal.DirEntryCodec.TryReadAll(
            payload, out IReadOnlyList<DirEntry> entries, out _));

        return entries;
    }

    private static int OneRecord(ReadOnlySpan<byte> payload) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload) + 2;
}
