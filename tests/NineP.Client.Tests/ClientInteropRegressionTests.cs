using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using Xunit;

namespace NineP.Client.Tests;

/// <summary>
/// Regressions found by running the client against other implementations (docs/interop.md),
/// each scripted here on the wire so the peer is not needed to keep it fixed.
/// </summary>
public sealed class ClientInteropRegressionTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// diod 1.0.24 implements <c>Trename</c> and not <c>Trenameat</c>, and answers the latter
    /// <c>EOPNOTSUPP</c>. Linux v9fs falls back to <c>Trename</c> on exactly that answer, and so
    /// does this client: the file gets a fid of its own, the destination directory's fid is the
    /// one already held, and the rename completes. <b>Mutation:</b> remove the fallback and the
    /// <c>EOPNOTSUPP</c> reaches the caller.
    /// </summary>
    [Fact]
    public async Task RenameFallsBackToTrenameWhenTheServerLacksTrenameat()
    {
        (INinePConnection connection, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using (server.ConfigureAwait(false))
        {
            Task<NinePSession> connecting = NinePClient.ConnectAsync(
                connection, new ClientOptions { Dialects = [Dialect.P9_2000_L] }, Ct).AsTask();
            await server.NegotiateAsync(Constants.Version9P2000L, cancellationToken: Ct);
            await using NinePSession session = await connecting;

            Task<NinePFid> attaching = session.AttachAsync(Ct).AsTask();
            Tattach attach = await server.ReadAsync<Tattach>(Ct);
            await server.WriteAsync(new Rattach(attach.Tag, new Qid(QidType.QTDIR, 0, 1)), Ct);
            await using NinePFid root = await attaching;

            Task renaming = session.RenameAsync("/old.txt", "/sub/new.txt", Ct).AsTask();

            Trenameat? renameat = null;
            Trename? rename = null;
            while (rename is null)
            {
                byte[] frame = await server.ReadFrameAsync(Ct);
                switch (MessageCodec.PeekType(frame))
                {
                    case MessageType.Twalk:
                        Twalk walk = MessageCodec.Decode<Twalk>(frame, Dialect.P9_2000_L);
                        Qid[] qids = [.. walk.Wnames.Select((_, i) => new Qid(QidType.QTFILE, 0, (ulong)(10 + i)))];
                        await server.WriteAsync(new Rwalk(walk.Tag, qids), Ct);
                        break;
                    case MessageType.Trenameat:
                        renameat = MessageCodec.Decode<Trenameat>(frame, Dialect.P9_2000_L);
                        await server.WriteAsync(new Rlerror(renameat.Value.Tag, Errno.EOPNOTSUPP), Ct);
                        break;
                    case MessageType.Trename:
                        rename = MessageCodec.Decode<Trename>(frame, Dialect.P9_2000_L);
                        await server.WriteAsync(new Rrename(rename.Value.Tag), Ct);
                        break;
                    default:
                        Assert.Fail("unexpected " + MessageCodec.PeekType(frame) + " before the rename completed");
                        break;
                }
            }

            Assert.NotNull(renameat);
            Assert.Equal("old.txt", renameat.Value.OldName);
            Assert.Equal("new.txt", renameat.Value.NewName);
            Assert.Equal(renameat.Value.NewDirFid, rename.Value.Dfid);
            Assert.Equal("new.txt", rename.Value.Name);
            Assert.NotEqual(renameat.Value.OldDirFid, rename.Value.Fid);

            // The three fids the rename held are clunked once it has completed.
            for (int i = 0; i < 3; i++)
            {
                Tclunk clunk = await server.ReadAsync<Tclunk>(Ct);
                await server.WriteAsync(new Rclunk(clunk.Tag), Ct);
            }

            await renaming;
        }
    }

    /// <summary>
    /// diod answers a <c>Tversion</c> for a dialect it does not speak with an <c>Rlerror</c> on
    /// <c>NOTAG</c>, where version(5) prescribes <c>Rversion "unknown"</c>. The error answers the
    /// version exchange, so connecting fails with a version error that names it, and not with a
    /// protocol violation about a tag the client never issued. <b>Mutation:</b> route the frame
    /// to the pending-tag lookup and the caller sees "the server answered unknown tag 65535".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnErrorAnsweringTheVersionRequestIsAVersionError(bool linuxShape)
    {
        (INinePConnection connection, FakeNinePServer server) = FakeNinePServer.CreatePair();
        await using (server.ConfigureAwait(false))
        {
            Task<NinePSession> connecting = NinePClient.ConnectAsync(
                connection, new ClientOptions { Dialects = [Dialect.P9_2000] }, Ct).AsTask();

            Tversion proposal = await server.ReadAsync<Tversion>(Ct);
            Assert.Equal(Constants.NOTAG, proposal.Tag);

            if (linuxShape)
            {
                await server.WriteAsync(new Rlerror(Constants.NOTAG, Errno.EOPNOTSUPP), Ct);
            }
            else
            {
                await server.WriteAsync(new Rerror(Constants.NOTAG, "unsupported version", 0), Ct);
            }

            NinePVersionException refused = await Assert.ThrowsAsync<NinePVersionException>(async () => await connecting);
            Assert.Contains("version request with an error", refused.Message, StringComparison.Ordinal);
            Assert.Contains(linuxShape ? "errno 95" : "unsupported version", refused.Message, StringComparison.Ordinal);
        }
    }
}
