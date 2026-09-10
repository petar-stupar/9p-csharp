using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// stat(5) and reference §5.8: a wstat is atomic, the owner may never change through one, and an
/// all-don't-touch record is a request to commit the file rather than a no-op.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class WstatTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 33: a wstat is all of it or none of it. A record that changes a permitted field and a
    /// forbidden one leaves both alone, so a client can never half-apply a change.
    /// </summary>
    [Fact]
    public async Task AllOrNothing()
    {
        MemoryFilesystem tree = new();
        MemoryFile theirs = tree.NewFile("theirs", 0x1B6);
        theirs.Owner = "root";
        theirs.Uid = 0;
        theirs.Group = "wheel";
        theirs.Data = "12345"u8.ToArray();
        tree.Root.Add(theirs);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid file = await session.WalkAsync("theirs", Ct);
        await using (file.ConfigureAwait(false))
        {
            // The length change alone would be refused too, but the mode change is the one only
            // an owner may make; either way nothing is applied.
            StatRecord both = StatRecord.DontTouch with { Mode = 0x1FF, Length = 2 };

            await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.WstatAsync(new Twstat(0, file.Fid, both), Ct));

            Assert.Equal(0x1B6u, theirs.Perm);
            Assert.Equal(5, theirs.Data.Length);
        }
    }

    /// <summary>§4.2: a record whose every field is "don't touch" commits the file.</summary>
    [Fact]
    public async Task AllDontTouchIsAnFsync()
    {
        MemoryFilesystem tree = new();
        MemoryFile file = tree.NewFile("mine", 0x1B6);
        tree.Root.Add(file);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid fid = await session.WalkAsync("mine", Ct);
        await using (fid.ConfigureAwait(false))
        {
            int before = file.Fsyncs;
            await session.Messages.WstatAsync(new Twstat(0, fid.Fid, StatRecord.DontTouch), Ct);

            Assert.Equal(before + 1, file.Fsyncs);
        }
    }

    /// <summary>stat(5): the owner may never change through a wstat, in either dialect.</summary>
    [Fact]
    public void OwnerCannotChange()
    {
        SetAttr chown = new() { Uid = 7 };

        NinePException refusal = Assert.Throws<NinePException>(
            () => NineP.Protocol.Codec.Internal.AttrProjector.ToWstat(chown, Dialect.P9_2000_u));

        Assert.Contains("owner", refusal.Error.Ename, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reference §5.8: <c>uid</c>, <c>atime</c>, <c>muid</c>, <c>qid</c>, <c>type</c> and
    /// <c>dev</c> cannot be set. A record that asks to <em>change</em> one of them used to be
    /// answered <c>Rwstat</c> — success — with the field silently dropped and nothing changed.
    /// Every value below differs from the one the file already carries; the sibling test covers
    /// the case where it does not.
    /// </summary>
    /// <param name="field">The unsettable field the record asks for.</param>
    /// <returns>A task that completes when the refusal has been observed.</returns>
    [Theory]
    [InlineData("uid")]
    [InlineData("n_uid")]
    [InlineData("muid")]
    [InlineData("n_muid")]
    [InlineData("atime")]
    [InlineData("type")]
    [InlineData("dev")]
    [InlineData("qid")]
    public async Task AnUnsettableFieldIsRefused(string field)
    {
        MemoryFilesystem tree = new();
        MemoryFile mine = tree.NewFile("mine", 0x1B6);
        tree.Root.Add(mine);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_u);

        NinePFid fid = await session.WalkAsync("mine", Ct);
        await using (fid.ConfigureAwait(false))
        {
            StatRecord baseline = StatRecord.DontTouch with
            {
                Extension = string.Empty,
                NUid = uint.MaxValue,
                NGid = uint.MaxValue,
                NMuid = uint.MaxValue,
            };

            StatRecord asking = field switch
            {
                "uid" => baseline with { Uid = "root" },
                "n_uid" => baseline with { NUid = 0 },
                "muid" => baseline with { Muid = "root" },
                "n_muid" => baseline with { NMuid = 0 },
                "atime" => baseline with { ATime = 0 },
                "type" => baseline with { Type = 1 },
                "dev" => baseline with { Dev = 1 },
                _ => baseline with { Qid = new Qid(QidType.QTFILE, 0, 9) },
            };

            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.WstatAsync(new Twstat(0, fid.Fid, asking), Ct));

            Assert.Equal((int)Errno.EPERM, refusal.Error.Errno);
        }
    }

    /// <summary>
    /// A field is "don't touch" in two ways: the sentinel, and the value the file already has. A
    /// client that fills a <c>Twstat</c> from the record it just read — which is what Linux v9fs
    /// does for an ordinary <c>chmod</c> or <c>truncate</c> on a <c>.u</c> mount — carries the
    /// file's own <c>uid</c>, <c>muid</c>, <c>type</c> and <c>dev</c> back to the server. Those
    /// fields ask for no change, so they are a no-op and not an <c>EPERM</c>.
    /// <para>
    /// (<c>n_muid</c> is the degenerate case: this tree reports <c>NONUNAME</c> for it, so the
    /// record a client echoes back already carries the sentinel. It is kept in the theory because
    /// that is exactly what such a client would send.)
    /// </para>
    /// <b>Mutation:</b> drop the <c>!= now.X</c> half of any refusal in
    /// <c>AttrProjector.FromWstat</c> and that field's case here fails with <c>EPERM</c>.
    /// </summary>
    /// <param name="field">The field the record echoes back unchanged.</param>
    /// <returns>A task that completes when the wstat has been accepted.</returns>
    [Theory]
    [InlineData("uid")]
    [InlineData("n_uid")]
    [InlineData("muid")]
    [InlineData("n_muid")]
    [InlineData("atime")]
    [InlineData("type")]
    [InlineData("dev")]
    [InlineData("qid")]
    public async Task AFieldEchoedBackUnchangedIsANoOp(string field)
    {
        MemoryFilesystem tree = new();
        MemoryFile mine = tree.NewFile("mine", 0x1B6);
        tree.Root.Add(mine);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_u);

        NinePFid fid = await session.WalkAsync("mine", Ct);
        await using (fid.ConfigureAwait(false))
        {
            StatRecord now = (await session.Messages.StatAsync(new Tstat(0, fid.Fid), Ct)).Stat;

            StatRecord baseline = StatRecord.DontTouch with
            {
                Extension = string.Empty,
                NUid = uint.MaxValue,
                NGid = uint.MaxValue,
                NMuid = uint.MaxValue,
            };

            StatRecord echoed = field switch
            {
                "uid" => baseline with { Uid = now.Uid },
                "n_uid" => baseline with { NUid = now.NUid },
                "muid" => baseline with { Muid = now.Muid },
                "n_muid" => baseline with { NMuid = now.NMuid },
                "atime" => baseline with { ATime = now.ATime },
                "type" => baseline with { Type = now.Type },
                "dev" => baseline with { Dev = now.Dev },
                _ => baseline with { Qid = now.Qid },
            };

            await session.Messages.WstatAsync(new Twstat(0, fid.Fid, echoed), Ct);

            StatRecord after = (await session.Messages.StatAsync(new Tstat(0, fid.Fid), Ct)).Stat;

            // The qid version is the file's modification counter, which a wstat is allowed to
            // move; nothing the record named may have changed.
            Assert.Equal(now with { Qid = now.Qid with { Version = after.Qid.Version } }, after);
        }
    }

    /// <summary>§5.8: the DMDIR bit cannot change, so a wstat that turns a file into a directory
    /// is refused rather than reported as done.</summary>
    [Fact]
    public async Task DmdirCannotChange()
    {
        MemoryFilesystem tree = new();
        MemoryFile mine = tree.NewFile("mine", 0x1B6);
        tree.Root.Add(mine);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid fid = await session.WalkAsync("mine", Ct);
        await using (fid.ConfigureAwait(false))
        {
            StatRecord promote = StatRecord.DontTouch with { Mode = ModeBits.DMDIR | 0x1A4 };

            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.WstatAsync(new Twstat(0, fid.Fid, promote), Ct));

            // A 9P2000 session carries the ename alone, so that is what is asserted here; the
            // .u and .L projections of the same value carry errno EPERM.
            Assert.Contains("DMDIR", refusal.Error.Ename, StringComparison.Ordinal);
            Assert.Equal(0x1B6u, mine.Perm);
        }
    }

    /// <summary>
    /// Rule 19: a <c>Twstat</c> that sets <c>DMAPPEND</c>, <c>DMEXCL</c> or <c>DMTMP</c> reaches
    /// the handler as <see cref="SetAttr.Flags"/> — stat(5) makes the directory bit the one mode
    /// bit a wstat cannot change — and the file the client then stats carries it. The bits used
    /// to be refused, and before that dropped by a <c>0xFFF</c> mask and answered <c>Rwstat</c>.
    /// The permission bits in the same word are applied with them: the two halves of the mode
    /// word are one change.
    /// <b>Mutation:</b> stop setting <c>Flags</c> on the update in <c>Dispatcher.WstatAsync</c>
    /// and the handler assertion fails.
    /// </summary>
    /// <param name="bit">The high mode bit the record asks for.</param>
    /// <param name="expected">The flag it names.</param>
    /// <param name="dialect">The dialect the wstat goes out in.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(ModeBits.DMAPPEND, FileFlags.Append, Dialect.P9_2000)]
    [InlineData(ModeBits.DMEXCL, FileFlags.Exclusive, Dialect.P9_2000)]
    [InlineData(ModeBits.DMTMP, FileFlags.Temporary, Dialect.P9_2000)]
    [InlineData(ModeBits.DMAPPEND, FileFlags.Append, Dialect.P9_2000_u)]
    [InlineData(ModeBits.DMEXCL, FileFlags.Exclusive, Dialect.P9_2000_u)]
    [InlineData(ModeBits.DMTMP, FileFlags.Temporary, Dialect.P9_2000_u)]
    public async Task ChangingAFileFlagReachesTheHandler(uint bit, FileFlags expected, Dialect dialect)
    {
        MemoryFilesystem tree = new();
        MemoryFile mine = tree.NewFile("mine", 0x1B6);
        tree.Root.Add(mine);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(dialect);

        NinePFid fid = await session.WalkAsync("mine", Ct);
        await using (fid.ConfigureAwait(false))
        {
            StatRecord asking = StatRecord.DontTouch with { Mode = bit | 0x1A4 };
            await session.Messages.WstatAsync(new Twstat(0, fid.Fid, asking), Ct);

            Assert.Equal(expected, Assert.IsType<SetAttr>(mine.LastUpdate).Flags);
            Assert.Equal(expected, mine.Flags);
            Assert.Equal(0x1A4u, mine.Perm);

            StatRecord now = (await session.Messages.StatAsync(new Tstat(0, fid.Fid), Ct)).Stat;
            Assert.Equal(bit, now.Mode & bit);
        }
    }

    /// <summary>
    /// Rule 19: the bits are judged like every other field — against what the file already is.
    /// A client that echoes back the <c>DMEXCL</c> it just read while changing the permission
    /// bits asks for no change in it, so the handler is not asked about the flags at all; only a
    /// real change, here dropping the bit, reaches it.
    /// </summary>
    [Fact]
    public async Task AFlagEchoedBackUnchangedIsANoOp()
    {
        MemoryFilesystem tree = new();
        MemoryFile mine = tree.NewFile("mine", 0x1B6);
        mine.Exclusive = true;
        tree.Root.Add(mine);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid fid = await session.WalkAsync("mine", Ct);
        await using (fid.ConfigureAwait(false))
        {
            StatRecord now = (await session.Messages.StatAsync(new Tstat(0, fid.Fid), Ct)).Stat;
            Assert.Equal(ModeBits.DMEXCL, now.Mode & ModeBits.DMEXCL);

            StatRecord chmod = StatRecord.DontTouch with { Mode = (now.Mode & ~0x1FFu) | 0x1A4 };
            await session.Messages.WstatAsync(new Twstat(0, fid.Fid, chmod), Ct);

            Assert.Equal(0x1A4u, mine.Perm);
            Assert.True(mine.Exclusive);
            Assert.Null(Assert.IsType<SetAttr>(mine.LastUpdate).Flags);

            // Dropping the bit is a change, and the handler is told so.
            StatRecord dropping = StatRecord.DontTouch with { Mode = 0x1A4 };
            await session.Messages.WstatAsync(new Twstat(0, fid.Fid, dropping), Ct);

            Assert.False(mine.Exclusive);
            Assert.Equal(FileFlags.None, Assert.IsType<SetAttr>(mine.LastUpdate).Flags);
        }
    }

    /// <summary>
    /// Rule 19: the reply says the file now has the flags, so the core reads the file back. A
    /// handler that answered the update without applying them — one written before
    /// <see cref="SetAttr.Flags"/> existed — is not answered <c>Rwstat</c> for it.
    /// <b>Mutation:</b> delete the read-back in <c>Dispatcher.ApplyAsync</c> and this fails.
    /// </summary>
    [Fact]
    public async Task AFlagUpdateTheHandlerDroppedIsRefused()
    {
        MemoryFilesystem tree = new();
        MemoryFile mine = tree.NewFile("mine", 0x1B6);
        mine.DropsFlagUpdates = true;
        tree.Root.Add(mine);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid fid = await session.WalkAsync("mine", Ct);
        await using (fid.ConfigureAwait(false))
        {
            StatRecord asking = StatRecord.DontTouch with { Mode = ModeBits.DMAPPEND | 0x1B6 };

            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.WstatAsync(new Twstat(0, fid.Fid, asking), Ct));

            Assert.Equal(Errno.EOPNOTSUPP, refusal.Error.Errno);
            Assert.Equal(FileFlags.Append, Assert.IsType<SetAttr>(mine.LastUpdate).Flags);
            Assert.Equal(FileFlags.None, mine.Flags);
        }
    }

    /// <summary>
    /// stat(5) and rule 19: the flags are part of the mode, and only the owner may change the
    /// mode. A record whose permission bits echo the file's own and whose only change is a flag
    /// is refused for a non-owner before the handler is asked.
    /// <b>Mutation:</b> drop <c>update.Flags</c> from the owner check in
    /// <c>Dispatcher.ApplyAsync</c> and this fails.
    /// </summary>
    [Fact]
    public async Task OnlyTheOwnerMaySetAFlag()
    {
        MemoryFilesystem tree = new();
        MemoryFile theirs = tree.NewFile("theirs", 0x1B6);
        theirs.Owner = "root";
        theirs.Uid = 0;
        tree.Root.Add(theirs);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid fid = await session.WalkAsync("theirs", Ct);
        await using (fid.ConfigureAwait(false))
        {
            StatRecord asking = StatRecord.DontTouch with { Mode = ModeBits.DMAPPEND | 0x1B6 };

            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.WstatAsync(new Twstat(0, fid.Fid, asking), Ct));

            Assert.Equal(Errno.EPERM, refusal.Error.Errno);
            Assert.Equal(FileFlags.None, theirs.Flags);
            Assert.Null(theirs.LastUpdate);
        }
    }

    /// <summary>
    /// Rule 19: <c>DMAUTH</c> and <c>DMMOUNT</c> are the server's own bits. A record that would
    /// set one is refused like the other unsettable fields, and the permission change beside it
    /// is not applied either.
    /// </summary>
    /// <param name="bit">The server-owned bit the record asks for.</param>
    /// <returns>The running test.</returns>
    [Theory]
    [InlineData(ModeBits.DMAUTH)]
    [InlineData(ModeBits.DMMOUNT)]
    public async Task AServerOwnedBitCannotBeSetByAWstat(uint bit)
    {
        MemoryFilesystem tree = new();
        MemoryFile mine = tree.NewFile("mine", 0x1B6);
        tree.Root.Add(mine);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid fid = await session.WalkAsync("mine", Ct);
        await using (fid.ConfigureAwait(false))
        {
            StatRecord asking = StatRecord.DontTouch with { Mode = bit | 0x1A4 };

            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.WstatAsync(new Twstat(0, fid.Fid, asking), Ct));

            Assert.Equal("wstat cannot set DMAUTH or DMMOUNT", refusal.Error.Ename);
            Assert.Equal(Errno.EPERM, refusal.Error.Errno);
            Assert.Equal(0x1B6u, mine.Perm);
            Assert.Null(mine.LastUpdate);
        }
    }

    /// <summary>
    /// Reference §8 rule 19, second sentence: the <c>.u</c> <c>DMSETUID</c> / <c>DMSETGID</c> /
    /// <c>DMSETVTX</c> bits map onto the <c>07777</c> permission bits and are <b>honoured</b>, not
    /// masked away and answered <c>Rwstat</c>. This is the whole round trip a Linux v9fs
    /// <c>chmod u+s</c> makes on a <c>.u</c> mount: the bit goes out in the mode word's high bits,
    /// the handler stores it as <c>04000</c>, and the <c>Tstat</c> that follows reports it again.
    /// </summary>
    /// <param name="dmBit">The DM* bit the client sets.</param>
    /// <param name="expected">The 07777 permission the handler must end up holding.</param>
    /// <returns>A task that completes when the round trip has been observed.</returns>
    [Theory]
    [InlineData(ModeBits.DMSETUID, 0x9B6u)]
    [InlineData(ModeBits.DMSETGID, 0x5B6u)]
    [InlineData(ModeBits.DMSETVTX, 0x3B6u)]
    public async Task TheDotUPermissionBitsRoundTripThroughWstat(uint dmBit, uint expected)
    {
        MemoryFilesystem tree = new();
        MemoryFile mine = tree.NewFile("mine", 0x1B6);
        tree.Root.Add(mine);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_u);

        NinePFid fid = await session.WalkAsync("mine", Ct);
        await using (fid.ConfigureAwait(false))
        {
            StatRecord chmod = StatRecord.DontTouch with
            {
                Extension = string.Empty,
                Mode = dmBit | 0x1B6,
            };
            await session.Messages.WstatAsync(new Twstat(0, fid.Fid, chmod), Ct);

            Assert.Equal(expected, mine.Perm);

            // And it comes back: the stat record carries the DM* bit again, and the client's own
            // projection reads it as the 07777 value the caller would have named.
            StatRecord back = (await session.Messages.StatAsync(new Tstat(0, fid.Fid), Ct)).Stat;
            Assert.Equal(dmBit, back.Mode & dmBit);
            Assert.Equal(expected, (await fid.GetAttrAsync(Ct)).Perm);
        }
    }

    /// <summary>
    /// Reference §8 rule 15: plain 9P2000's mode word has only the rwx bits, so the client refuses
    /// a <c>setuid</c> chmod there before it is sent rather than masking the bit off and reporting
    /// the chmod as done.
    /// </summary>
    /// <returns>A task that completes when the refusal has been observed.</returns>
    [Fact]
    public async Task ASetuidChmodIsRefusedOnPlain9P2000()
    {
        MemoryFilesystem tree = new();
        MemoryFile mine = tree.NewFile("mine", 0x1B6);
        tree.Root.Add(mine);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid fid = await session.WalkAsync("mine", Ct);
        await using (fid.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await fid.SetAttrAsync(new SetAttr { Perm = 0x9B6 }, Ct));

            Assert.Equal(Errno.EINVAL, refusal.Error.Errno);
            Assert.Equal(0x1B6u, mine.Perm);
        }
    }

    /// <summary>The owner may change what stat(5) allows an owner to change.</summary>
    [Fact]
    public async Task TheOwnerMayChangeModeAndLength()
    {
        MemoryFilesystem tree = new();
        MemoryFile mine = tree.NewFile("mine", 0x1B6);
        mine.Data = "12345"u8.ToArray();
        tree.Root.Add(mine);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid fid = await session.WalkAsync("mine", Ct);
        await using (fid.ConfigureAwait(false))
        {
            StatRecord change = StatRecord.DontTouch with { Mode = 0x1A4, Length = 2 };
            await session.Messages.WstatAsync(new Twstat(0, fid.Fid, change), Ct);

            Assert.Equal(0x1A4u, mine.Perm);
            Assert.Equal(2, mine.Data.Length);
        }
    }
}
