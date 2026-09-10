using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// Walk semantics (reference §5.4, §6.5) against the shipped server over
/// <see cref="NineP.Protocol.Transports.MemoryTransport"/>: the partial-walk rule, the clone
/// rules, <c>Edupfid</c>, ".." at the root, and the name rules the codec enforces.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class WalkTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 28 (§5.4): a walk that stops part way returns the qids it managed and binds nothing.
    /// <b>Mutation:</b> binding <c>newfid</c> unconditionally in <c>WalkHandler.WalkAsync</c>
    /// makes the second half of this test — the unknown fid — fail.
    /// </summary>
    [Fact]
    public async Task PartialWalkDoesNotBindNewfid()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        Rwalk partial = await session.Messages
            .WalkAsync(new Twalk(0, session.Root.Fid, 40, ["sub", "nowhere"]), Ct);

        // One qid for "sub", none for "nowhere": nwqid < nwname.
        Assert.Single(partial.Wqids);

        // Nothing was bound, so fid 40 is a fid this connection does not hold.
        NinePException unknown = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages.ClunkAsync(new Tclunk(0, 40), Ct));
        Assert.Equal(Errno.EBADF, unknown.Error.Errno);
    }

    /// <summary>Rule 28 restated as the rule index words it: the prefix comes back.</summary>
    [Fact]
    public async Task PartialWalkReturnsPrefix()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        Rwalk partial = await session.Messages
            .WalkAsync(new Twalk(0, session.Root.Fid, 41, ["sub", "nowhere", "either"]), Ct);

        Assert.Single(partial.Wqids);
        Assert.Equal(QidType.QTDIR, partial.Wqids[0].Type);
    }

    /// <summary>§5.4: a first-element failure is an error, and it changes nothing.</summary>
    [Fact]
    public async Task FirstElementFailureIsAnError()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePException failure = await Assert.ThrowsAsync<NinePException>(async () =>
            await session.Messages.WalkAsync(new Twalk(0, session.Root.Fid, 42, ["nowhere", "sub"]), Ct));

        Assert.Equal(Errno.ENOENT, failure.Error.Errno);

        NinePException unknown = await Assert.ThrowsAsync<NinePException>(
            async () => await session.Messages.ClunkAsync(new Tclunk(0, 42), Ct));
        Assert.Equal(Errno.EBADF, unknown.Error.Errno);
    }

    /// <summary>
    /// Rule 29 (§5.4, srv.c:302-309): a fid opened for I/O cannot be walked or cloned. The
    /// session is 9P2000 so that the ename itself is on the wire; in .L the same error value
    /// travels as its errno.
    /// </summary>
    [Fact]
    public async Task CannotCloneOpenFid()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        NinePFid file = await session.OpenFileAsync("hello.txt", OpenMode.Read, OpenFlags.None, Ct);
        await using (file.ConfigureAwait(false))
        {
            NinePException refused = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.WalkAsync(new Twalk(0, file.Fid, 43, []), Ct));

            Assert.Equal("cannot clone open fid", refused.Error.Ename);
        }
    }

    /// <summary>§5.4: a <c>newfid</c> that is already in use draws <c>Edupfid</c>.</summary>
    [Fact]
    public async Task DuplicateNewfidRejected()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000);

        // A second fid on the root, so the walk below comes from a fid that is not its newfid.
        await session.Messages.WalkAsync(new Twalk(0, session.Root.Fid, 50, []), Ct);

        NinePException duplicate = await Assert.ThrowsAsync<NinePException>(async () =>
            await session.Messages.WalkAsync(new Twalk(0, 50, session.Root.Fid, ["sub"]), Ct));

        Assert.Equal("duplicate fid", duplicate.Error.Ename);

        // walk(5) allows newfid == fid, which is the one case that is not a duplicate.
        await session.Messages.WalkAsync(new Twalk(0, 50, 50, ["sub"]), Ct);
        await session.Messages.ClunkAsync(new Tclunk(0, 50), Ct);
    }

    /// <summary>§5.4: ".." at the root walks to the root itself rather than out of the tree.</summary>
    [Fact]
    public async Task DotDotAtRootIsRoot()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        Rwalk up = await session.Messages
            .WalkAsync(new Twalk(0, session.Root.Fid, 44, ["..", "..", ".."]), Ct);

        Assert.Equal(3, up.Wqids.Count);
        Assert.All(up.Wqids, qid => Assert.Equal(session.Root.Qid, qid));

        await session.Messages.ClunkAsync(new Tclunk(0, 44), Ct);
    }

    /// <summary>"..": from a subdirectory it walks back to the directory it was reached through.</summary>
    [Fact]
    public async Task DotDotReturnsToTheParent()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        Rwalk there = await session.Messages
            .WalkAsync(new Twalk(0, session.Root.Fid, 45, ["sub", ".."]), Ct);

        Assert.Equal(2, there.Wqids.Count);
        Assert.Equal(session.Root.Qid, there.Wqids[1]);

        await session.Messages.ClunkAsync(new Tclunk(0, 45), Ct);
    }

    /// <summary>
    /// Reference §8 rule 3: a name with a '/' in it, a NUL, or "." never reaches a handler — the
    /// codec refuses the message, so the name rules cannot be forgotten by a server author.
    /// </summary>
    [Fact]
    public void BadNameRejected()
    {
        Assert.Equal(ProtocolErrorKind.Name, WalkNameFailure("a/b"));
        Assert.Equal(ProtocolErrorKind.Name, WalkNameFailure("."));
        Assert.Equal(ProtocolErrorKind.Nul, WalkNameFailure("a\0b"));
        Assert.Equal(ProtocolErrorKind.Name, WalkNameFailure(new string('x', 256)));

        // ".." is the one name that is legal in a walk and nowhere else.
        Assert.Null(WalkNameFailure(".."));
    }

    private static ProtocolErrorKind? WalkNameFailure(string name)
    {
        byte[] frame = TwalkFrame(name);

        return MessageCodec.TryDecode(frame, Dialect.P9_2000_L, out Twalk _, out ProtocolErrorKind failure)
            ? null
            : failure;
    }

    private static byte[] TwalkFrame(string name)
    {
        byte[] text = System.Text.Encoding.UTF8.GetBytes(name);
        byte[] frame = new byte[4 + 1 + 2 + 4 + 4 + 2 + 2 + text.Length];

        BitConverter.TryWriteBytes(frame.AsSpan(0), (uint)frame.Length);
        frame[4] = (byte)MessageType.Twalk;
        BitConverter.TryWriteBytes(frame.AsSpan(5), (ushort)1);
        BitConverter.TryWriteBytes(frame.AsSpan(7), 1u);
        BitConverter.TryWriteBytes(frame.AsSpan(11), 2u);
        BitConverter.TryWriteBytes(frame.AsSpan(15), (ushort)1);
        BitConverter.TryWriteBytes(frame.AsSpan(17), (ushort)text.Length);
        text.CopyTo(frame, 19);

        return frame;
    }
}
