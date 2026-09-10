using NineP.Protocol;
using NineP.Protocol.Messages;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

/// <summary>
/// <c>Rgetattr.valid</c> (reference §4.6 and §8 rule 22): the mask says what the handler actually
/// supplied. It used to say <c>All</c> whatever the handler answered, so a client that trusted it
/// read a creation time of 1970 and a generation of zero as facts.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class GetattrTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 22: <c>btime</c>, <c>gen</c> and <c>data_version</c> are marked only when the handler
    /// supplied a non-zero value — <c>Attr</c> has no "unknown", so for those three zero is it.
    /// Everything else comes from fields every handler must answer with and stays valid.
    /// <b>Mutation:</b> answer <c>GetAttrMask.All</c> again in <c>Dispatcher.GetattrAsync</c> and
    /// this fails.
    /// </summary>
    [Fact]
    public async Task FieldsTheHandlerLeftZeroAreNotMarkedValid()
    {
        MemoryFilesystem tree = new();
        tree.Root.Add(tree.NewFile("hello.txt", 0x1B6));

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, cancellationToken: Ct);

        await client.AttachAsync(1, Ct);
        await client.WalkAsync(2, 1, 2, ["hello.txt"], Ct);
        await client.SendAsync(new Tgetattr(3, 2, GetAttrMask.All), Ct);

        byte[] frame = await client.ReceiveFrameAsync(Ct);

        // §4.6: the reply is the full 160 bytes however little of it is valid.
        Assert.Equal(160, frame.Length);

        Rgetattr reply = NineP.Protocol.Codec.MessageCodec.Decode<Rgetattr>(frame, Dialect.P9_2000_L);

        Assert.False(reply.Valid.HasFlag(GetAttrMask.BTime));
        Assert.False(reply.Valid.HasFlag(GetAttrMask.Gen));
        Assert.False(reply.Valid.HasFlag(GetAttrMask.DataVersion));

        // What the tree does answer with is still marked, and the qid is valid whatever the mask
        // says — it is not one of the fields the mask governs.
        Assert.True(reply.Valid.HasFlag(GetAttrMask.Mode));
        Assert.True(reply.Valid.HasFlag(GetAttrMask.Size));
        Assert.True(reply.Valid.HasFlag(GetAttrMask.MTime));
        Assert.Equal(QidType.QTFILE, reply.Qid.Type);
        Assert.NotEqual(0ul, reply.Qid.Path);
    }

    /// <summary>
    /// Rule 22: a handler that does supply them has them marked, so the mask reports the handler
    /// rather than a constant of either kind.
    /// </summary>
    [Fact]
    public async Task FieldsTheHandlerSuppliedAreMarkedValid()
    {
        MemoryFilesystem tree = new();
        MemoryFile file = tree.NewFile("hello.txt", 0x1B6);
        file.BTime = new TimeSpec(1_600_000_000, 7);
        file.Gen = 42;
        file.DataVersion = 99;
        tree.Root.Add(file);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, cancellationToken: Ct);

        await client.AttachAsync(1, Ct);
        await client.WalkAsync(2, 1, 2, ["hello.txt"], Ct);
        await client.SendAsync(new Tgetattr(3, 2, GetAttrMask.All), Ct);

        byte[] frame = await client.ReceiveFrameAsync(Ct);
        Assert.Equal(160, frame.Length);

        Rgetattr reply = NineP.Protocol.Codec.MessageCodec.Decode<Rgetattr>(frame, Dialect.P9_2000_L);

        Assert.True(reply.Valid.HasFlag(GetAttrMask.BTime));
        Assert.True(reply.Valid.HasFlag(GetAttrMask.Gen));
        Assert.True(reply.Valid.HasFlag(GetAttrMask.DataVersion));
        Assert.Equal(new TimeSpec(1_600_000_000, 7), reply.BTime);
        Assert.Equal(42ul, reply.Gen);
        Assert.Equal(99ul, reply.DataVersion);
    }

    /// <summary>
    /// §4.6: <c>valid</c> is what the client asked for and the handler supplied, so a narrower
    /// request mask still narrows the reply. Rule 22 tightens that intersection; it does not
    /// replace it.
    /// </summary>
    [Fact]
    public async Task ValidIsTheIntersectionWithTheRequestMask()
    {
        MemoryFilesystem tree = new();
        MemoryFile file = tree.NewFile("hello.txt", 0x1B6);
        file.Gen = 42;
        tree.Root.Add(file);

        await using ServerHarness harness = await ServerHarness.StartAsync(tree: tree);
        await using WireClient client = await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, cancellationToken: Ct);

        await client.AttachAsync(1, Ct);
        await client.WalkAsync(2, 1, 2, ["hello.txt"], Ct);
        await client.SendAsync(new Tgetattr(3, 2, GetAttrMask.Basic), Ct);

        Rgetattr reply = await client.ReceiveAsync<Rgetattr>(Ct);

        Assert.Equal(GetAttrMask.Basic, reply.Valid);
        Assert.False(reply.Valid.HasFlag(GetAttrMask.Gen));
        Assert.Equal(0ul, reply.Gen);
    }
}
