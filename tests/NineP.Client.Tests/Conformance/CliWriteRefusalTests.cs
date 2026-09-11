using NineP.Client.Tests;
using NineP.Client.Tests.Conformance;
#if NET10_0_OR_GREATER
using System.Text;
using NineP.Cli;
using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Negotiation;
using NineP.Protocol.Transports;
using NineP.Server;
using NineP.TestSupport;
using Xunit;

namespace NineP.Client.Tests.Conformance;

/// <summary>
/// <c>ninep write</c> creates what is not there yet (conformance Part B step 2 writes straight
/// after a <c>mkdir</c>), and that recovery must not swallow a refusal the server meant. A handler
/// may answer a write <c>ENOENT</c> for its own reasons — a control file rejecting a name it does
/// not hold — and the file is then present, so the create that follows fails for a wholly
/// unrelated reason and it is <b>that</b> error the user would otherwise be shown.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class CliWriteRefusalTests
{
    private const string Refusal = "nothing ingested under the name 'nothing'";

    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// The refusal the handler wrote is the one reported, in each dialect as far as that dialect
    /// can carry it: 9P2000 has no errno field, so the sentence survives and the unknown ename
    /// maps to <c>EIO</c>; .u carries both; .L carries an errno alone, so the sentence becomes the
    /// table's wording for that errno. What none of them may report is the create's
    /// <c>EACCES</c> / "permission denied", which is about the parent's mode and not about this
    /// refusal at all.
    /// <b>Mutation:</b> removing the inner <c>catch</c> from <c>CliCommands.WriteAsync</c> makes
    /// the .u and .L cases report "permission denied" / 13 and this test fails on both. Plain
    /// 9P2000 passes either way, which is the whole trap: the bug is invisible in the dialect a
    /// developer is most likely to try first.
    /// </summary>
    [Theory]
    [InlineData(Dialect.P9_2000, Refusal, Errno.EIO)]
    [InlineData(Dialect.P9_2000_u, Refusal, Errno.ENOENT)]
    [InlineData(Dialect.P9_2000_L, "file not found", Errno.ENOENT)]
    public async Task AServerRefusalIsNotReplacedByTheCreateItProvokes(
        Dialect dialect, string ename, int errno)
    {
        await using Harness h = await Harness.StartAsync(dialect, Errno.ENOENT);

        NinePException refused = await Assert.ThrowsAsync<NinePException>(async () =>
            await CliCommands.RunAsync(
                h.Session,
                new CliOptions { Command = "write", Arguments = ["/ctl"] },
                Stream.Null,
                new MemoryStream(Encoding.UTF8.GetBytes("frobnicate\n")),
                Ct));

        Assert.Equal(ename, refused.Error.Ename);
        Assert.Equal(errno, refused.Error.Errno);
        Assert.NotEqual(Errno.EACCES, refused.Error.Errno);
    }

    /// <summary>
    /// The recovery itself still works: a write to a name that is genuinely absent creates it.
    /// The guard narrows what the fallback reports, never when it runs.
    /// </summary>
    [Fact]
    public async Task WriteStillCreatesAFileThatIsGenuinelyAbsent()
    {
        await using Harness h = await Harness.StartAsync(Dialect.P9_2000_L, refusal: null);

        await CliCommands.RunAsync(
            h.Session,
            new CliOptions { Command = "write", Arguments = ["/fresh"] },
            Stream.Null,
            new MemoryStream("made"u8.ToArray()),
            Ct);

        Assert.Equal("made"u8.ToArray(), await h.Session.ReadFileAsync("/fresh", Ct));
    }

    /// <summary>
    /// A tree whose root refuses a create — the shape a synthetic server has — holding one control
    /// file that refuses every write with a sentence of its own.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly NinePServer _server;
        private readonly Task _serving;

        private Harness(NinePServer server, NinePSession session, Task serving)
        {
            _server = server;
            Session = session;
            _serving = serving;
        }

        public NinePSession Session { get; }

        public static async Task<Harness> StartAsync(Dialect dialect, int? refusal)
        {
            MemoryTransport transport = new();
            NinePAddress address = new(NinePScheme.Memory, "w" + Guid.NewGuid().ToString("N"), 0, string.Empty);

            MemoryFilesystem tree = new();
            MemoryFile control = tree.NewFile("ctl", Perms.P0666);
            if (refusal is int errno)
            {
                control.WriteFailure = new NinePError(Refusal, errno);

                // The root of a synthetic tree is not writable, which is what makes the create
                // the fallback attempts fail — with an error about permissions, nothing to do
                // with the name the handler was complaining about.
                tree.Root.Perm = Perms.P0555;
            }
            tree.Root.Add(control);

            NinePServer server = new(new ServerOptions { Listen = [address], Transports = [transport] });
            Task serving = server.ServeAsync(tree, CancellationToken.None);
            await server.Listening;

            ClientOptions options = new() { Dialects = [dialect], Msize = 8192, Uname = "glenda" };
            NinePSession session = await NinePClient
                .ConnectAsync(transport, address, options, CancellationToken.None);
            await session.AttachAsync(CancellationToken.None);

            return new Harness(server, session, serving);
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await _server.DisposeAsync();

            try
            {
                await _serving;
            }
            catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException)
            {
                // The accept loop was stopped on purpose.
            }
        }
    }
}
#endif
