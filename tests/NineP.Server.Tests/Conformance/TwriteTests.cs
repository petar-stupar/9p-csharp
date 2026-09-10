using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// Reference §8 rule 4 and S-26: <c>count == size − 23</c> is the validity rule, and
/// <c>msize − IOHDRSZ</c> is a <b>service</b> bound that shortens a write. Confusing the two turns
/// a legal maximal write into a protocol error.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class TwriteTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 44: a maximal legal <c>Twrite</c> has <c>size == msize</c> and therefore
    /// <c>count == msize − 23</c>, which is one byte more than <c>msize − IOHDRSZ</c>. It is
    /// accepted, not refused.
    /// </summary>
    [Fact]
    public async Task MaximalLegalWriteIsAccepted()
    {
        MemoryFilesystem tree = Populated();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid file = await session.OpenFileAsync("target", OpenMode.Write, OpenFlags.None, Ct);
        await using (file.ConfigureAwait(false))
        {
            int maximal = (int)session.Msize - Constants.TwriteHeaderSize;
            Assert.True(maximal > session.MaxPayload);

            Rwrite reply = await session.Messages
                .WriteAsync(new Twrite(0, file.Fid, 0, new byte[maximal]), Ct);

            // The frame is legal, so it is answered; the service bound only shortens the write.
            Assert.True(reply.Count > 0);
            Assert.True(reply.Count <= (uint)maximal);
        }
    }

    /// <summary>
    /// Rule 31: the service bound shortens a write and the server reports what it actually wrote —
    /// a short write, not an error (srv.c:522).
    /// </summary>
    [Fact]
    public async Task ServerBoundShortensWrite()
    {
        MemoryFilesystem tree = Populated();
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid file = await session.OpenFileAsync("target", OpenMode.Write, OpenFlags.None, Ct);
        await using (file.ConfigureAwait(false))
        {
            int maximal = (int)session.Msize - Constants.TwriteHeaderSize;
            Rwrite reply = await session.Messages
                .WriteAsync(new Twrite(0, file.Fid, 0, new byte[maximal]), Ct);

            Assert.Equal((uint)session.MaxPayload, reply.Count);
            Assert.Equal(session.MaxPayload, tree.Root.Children["target"] is MemoryFile written
                ? written.Data.Length
                : -1);
        }
    }

    /// <summary>read(5): a write to a fid opened for reading is refused.</summary>
    [Fact]
    public async Task WriteNeedsAWriteOpen()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync(tree: Populated());
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid file = await session.OpenFileAsync("target", OpenMode.Read, OpenFlags.None, Ct);
        await using (file.ConfigureAwait(false))
        {
            NinePException refusal = await Assert.ThrowsAsync<NinePException>(
                async () => await session.Messages.WriteAsync(new Twrite(0, file.Fid, 0, new byte[4]), Ct));

            Assert.Equal(Errno.EACCES, refusal.Error.Errno);
        }
    }

    /// <summary>§4.4: an append-only open ignores the offset and writes at the end.</summary>
    [Fact]
    public async Task AnAppendOpenIgnoresTheOffset()
    {
        MemoryFilesystem tree = Populated();
        MemoryFile target = (MemoryFile)tree.Root.Children["target"];
        target.Data = "abc"u8.ToArray();

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using NinePSession session = await harness.ConnectAsync(Dialect.P9_2000_L);

        NinePFid file = await session.OpenFileAsync("target", OpenMode.Write, OpenFlags.Append, Ct);
        await using (file.ConfigureAwait(false))
        {
            await session.Messages.WriteAsync(new Twrite(0, file.Fid, 0, "de"u8.ToArray()), Ct);
        }

        Assert.Equal("abcde"u8.ToArray(), target.Data);
    }

    private static MemoryFilesystem Populated()
    {
        MemoryFilesystem tree = new();
        tree.Root.Add(tree.NewFile("target", 0x1B6));
        return tree;
    }
}
