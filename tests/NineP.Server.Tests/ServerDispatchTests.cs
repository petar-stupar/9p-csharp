using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Server;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests;

/// <summary>
/// Dialect-shaped dispatch rules, and the observability the core owes an operator: counters per
/// message type and an audit hook whose strings are already escaped (architecture §4).
/// </summary>
public sealed class ServerDispatchTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>Rule 35: in a .L session a <c>Tread</c> on a directory is an error.</summary>
    [Fact]
    public async Task DotLReadOnDirectoryIsAnError()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid directory = await session.OpenFileAsync("/", OpenMode.Read, OpenFlags.None, Ct);
        await using (directory.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.ReadAsync(new Tread(0, directory.Fid, 0, 64), Ct));

            Assert.Equal(Errno.EISDIR, refusal.Error.Errno);
        }

        // The same read is the ordinary listing in the dialects that have no Treaddir.
        await using NinePSession legacy = await harness.ConnectAsync(Dialect.P9_2000);
        Assert.NotEmpty(await legacy.ReadDirAsync("/", Ct));
    }

    /// <summary>
    /// Reference §2: a message the negotiated dialect does not carry draws
    /// <c>Rerror "unknown message"</c> / <c>Rlerror EOPNOTSUPP</c>, and the connection <b>stays
    /// up</b>. The frame parsed; nothing about the stream behind it is in doubt, so there is
    /// nothing to resync from, and u9fs and plan9 both keep serving. Closing here answered a
    /// client that asked for one unsupported feature by killing its session.
    /// </summary>
    [Fact]
    public async Task AMessageTheDialectDoesNotCarryIsAnsweredAndTheSessionStaysUp()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();

        // A .L message in a .u session: Tlopen is encoded by hand, because the shipped client
        // refuses to put it on the wire at all.
        await using (WireClient legacy = await WireClient.ConnectAsync(harness, Dialect.P9_2000_u, cancellationToken: Ct))
        {
            await legacy.AttachAsync(1, Ct);
            await legacy.SendAsync(new Tlopen(7, 1, 0), Dialect.P9_2000_L, Ct);

            Rerror refusal = await legacy.ReceiveAsync<Rerror>(Ct);

            Assert.Equal((ushort)7, refusal.Tag);
            Assert.Equal("unknown message", refusal.Ename);
            Assert.Equal(Errno.EOPNOTSUPP, refusal.Errno);

            // Still a session: the next request is answered normally.
            Rwalk walked = await legacy.WalkAsync(8, 1, 2, [], Ct);
            Assert.Empty(walked.Wqids);
        }

        // And the other direction: a 9P2000-only message in a .L session.
        await using WireClient linux = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, cancellationToken: Ct);
        await linux.AttachAsync(1, Ct);
        await linux.SendAsync(new Topen(9, 1, 0), Dialect.P9_2000, Ct);

        Rlerror refused = await linux.ReceiveAsync<Rlerror>(Ct);

        Assert.Equal((ushort)9, refused.Tag);
        Assert.Equal(Errno.EOPNOTSUPP, refused.Ecode);

        Rwalk again = await linux.WalkAsync(10, 1, 2, [], Ct);
        Assert.Empty(again.Wqids);
    }

    /// <summary>
    /// A <c>Tread</c> at the end of the address space is an empty read, not a framing violation.
    /// The decoder guarded <c>offset + count</c> against wrapping and closed the connection when
    /// it did, but reference §8 rule 5 names <c>Twrite</c>, <c>Tsetattr</c> and <c>Tlock</c> and
    /// only those - a client probing near EOF lost its session where Plan 9 answers count = 0.
    /// </summary>
    [Fact]
    public async Task AReadAtTheEndOfTheAddressSpaceIsAnEmptyRead()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using WireClient client = await WireClient.ConnectAsync(
            harness, Dialect.P9_2000_L, cancellationToken: Ct);

        await client.AttachAsync(1, Ct);
        await client.WalkAsync(2, 1, 2, ["hello.txt"], Ct);
        await client.SendAsync(new Tlopen(3, 2, 0), Ct);
        await client.ReceiveAsync<Rlopen>(Ct);

        await client.SendAsync(new Tread(4, 2, ulong.MaxValue, 1), Ct);
        Rread reply = await client.ReceiveAsync<Rread>(Ct);

        Assert.Equal((ushort)4, reply.Tag);
        Assert.Equal(0, reply.Data.Length);

        // Still a session: the next request is answered normally.
        Rwalk again = await client.WalkAsync(5, 1, 3, [], Ct);
        Assert.Empty(again.Wqids);
    }

    /// <summary>Messages by type are counted, which is what a metrics scrape reads.</summary>
    [Fact]
    public async Task MessagesAreCountedByType()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        await session.Messages.ClunkAsync(new Tclunk(0, session.Root.Fid), Ct);

        ServerCounters counters = harness.Server.Counters;
        Assert.True(counters.MessagesByType[MessageType.Tattach] >= 1);
        Assert.True(counters.MessagesByType[MessageType.Tclunk] >= 1);
        Assert.True(counters.BytesRead > 0);
        Assert.True(counters.BytesWritten > 0);
    }

    /// <summary>
    /// The audit hook receives one entry per completed request, carrying the identity the request
    /// ran as and a summary whose untrusted parts are already escaped (reference §8 rule 11).
    /// </summary>
    [Fact]
    public async Task TheRequestLogSeesEscapedSummaries()
    {
        RecordingSink sink = new();
        await using ServerHarness harness = await ServerHarness.StartAsync(
            options => options with { RequestLog = sink });
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        await Assert.ThrowsAsync<NinePException>(async () => await session.WalkAsync("no\tsuch", Ct));

        // The reply reaches the client before the dispatcher records the entry, so the assertion
        // waits for the record rather than racing it.
        RequestLogEntry walk = await sink.AwaitAsync(
            entry => entry.Request == MessageType.Twalk
                && entry.Summary.Contains("such", StringComparison.Ordinal),
            Ct);

        // The tab in the name reached the log escaped, so a peer cannot forge a log line.
        Assert.Contains("\\t", walk.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(walk.Summary, character => character == '\t');
        Assert.Equal("glenda", walk.Identity?.User);
        Assert.Equal(MessageType.Rlerror, walk.Reply);
        Assert.Equal(Errno.ENOENT, walk.Error?.Errno);
    }

    /// <summary>A sink that throws loses its entry, not the connection.</summary>
    [Fact]
    public async Task AThrowingSinkDoesNotBreakTheSession()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync(
            options => options with { RequestLog = new ThrowingSink() });
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        Assert.NotEmpty(await session.ReadDirAsync("/", Ct));
    }

    private sealed class RecordingSink : IRequestLogSink
    {
        private readonly List<RequestLogEntry> _entries = [];

        public IReadOnlyList<RequestLogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public void Record(in RequestLogEntry entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        public async Task<RequestLogEntry> AwaitAsync(
            Func<RequestLogEntry, bool> match, CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                foreach (RequestLogEntry entry in Entries)
                {
                    if (match(entry))
                    {
                        return entry;
                    }
                }

                await Task.Delay(10, cancellationToken);
            }

            throw new InvalidOperationException("no matching request-log entry was recorded");
        }
    }

    private sealed class ThrowingSink : IRequestLogSink
    {
        public void Record(in RequestLogEntry entry) =>
            throw new InvalidOperationException("a sink that breaks its contract");
    }

    /// <summary>
    /// A reply that cannot be encoded must still be a reply. A stat record's <c>size[2]</c> is
    /// sixteen bits, so a file whose owner is 70 000 bytes long has no encodable <c>Rstat</c> —
    /// and a client provokes exactly that by attaching with a long <c>uname</c> to a tree that
    /// reports the owner it was told, which is what the shipped <c>jsonfs</c> does. The encode
    /// therefore happens before the request's tag is claimed, so the overflow is an ordinary
    /// <c>NinePException(EOVERFLOW)</c> that the dispatcher turns into an error reply.
    /// <b>Mutation:</b> move the <c>Encode</c> in <c>ServerSession.CompleteAsync</c> back below
    /// <c>pending.TryComplete()</c> and the <c>Tstat</c> below is never answered at all.
    /// </summary>
    [Fact]
    public async Task AReplyTooLongToEncodeIsAnErrorReplyRatherThanSilence()
    {
        MemoryFilesystem tree = new();
        MemoryFile unstatable = tree.NewFile("unstatable", 0x1A4);
        unstatable.Owner = new string('u', 70_000);
        tree.Root.Add(unstatable);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000, 8192, Ct);

        await client.AttachAsync(1, Ct);
        await client.WalkAsync(2, 1, 2, ["unstatable"], Ct);
        await client.SendAsync(new Tstat(3, 2), Ct);

        // The bound is what makes the mutation's symptom -- silence -- a fast, named failure
        // rather than the test deadline three minutes later.
        Task<Rerror> answering = client.ReceiveAsync<Rerror>(Ct);
        Task settled = await Task.WhenAny(answering, Task.Delay(TimeSpan.FromSeconds(10), Ct));

        Assert.Same(answering, settled);

        Rerror refused = await answering;

        Assert.Equal((ushort)3, refused.Tag);
        Assert.Equal(ErrorTable.EnameFor(Errno.EOVERFLOW), refused.Ename);

        // The session survives it, and the tag the refusal answered is the client's again.
        await client.SendAsync(new Tclunk(3, 2), Ct);
        Rclunk clunked = await client.ReceiveAsync<Rclunk>(Ct);

        Assert.Equal((ushort)3, clunked.Tag);
    }
}
