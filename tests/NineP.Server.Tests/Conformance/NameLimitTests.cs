using System.Buffers;
using System.Buffers.Binary;
using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests.Conformance;

[Trait("Category", "Conformance")]
public sealed class NameLimitTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);
    public static TheoryData<Dialect> Dialects => BoundaryTests.Dialects;

    [Theory, MemberData(nameof(Dialects))]
    public async Task F7a_MalformedNamesAreAnsweredThenOnlyThatConnectionCloses(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession healthy = await h.ConnectAsync(dialect);
        foreach (byte[] legal in NameFrames(dialect, new string('z', 255)))
        {
            await using WireClient hostile = await WireClient.ConnectAsync(h, dialect, cancellationToken: Ct);
            await hostile.AttachAsync(1, Ct);
            await hostile.WalkAsync(2, 1, 2, ["hello.txt"], Ct);
            byte[] frame = ExtendName(legal);
            await hostile.SendRawAsync(frame, Ct);
            Assert.Equal(Errno.EPROTO, await BoundaryTests.WireError(hostile));
            Assert.Empty(await hostile.ReceiveFrameAsync(Ct));
            Assert.Equal(10ul, (await healthy.GetAttrAsync("hello.txt", Ct)).Size);
        }
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task F7c_ConfiguredLimitCoversEveryNameFieldAndKeepsSessionUsable(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync(o => o with { Limits = o.Limits with { MaxNameLength = 64 } });
        await using WireClient wire = await WireClient.ConnectAsync(h, dialect, cancellationToken: Ct);
        await wire.AttachAsync(1, Ct);
        await wire.WalkAsync(2, 1, 2, ["hello.txt"], Ct);
        foreach (string name in new[] { new string('x', 65), new string('é', 32) + "x" })
        {
            foreach (byte[] frame in NameFrames(dialect, name))
            {
                await wire.SendRawAsync(frame, Ct);
                Assert.Equal(Errno.ENAMETOOLONG, await BoundaryTests.WireError(wire));
                await wire.WalkAsync(3, 1, 3, [], Ct);
                await wire.SendAsync(new Tclunk(4, 3), Ct);
                await wire.ReceiveAsync<Rclunk>(Ct);
            }
        }
        await using NinePSession s = await h.ConnectAsync(dialect);
        foreach (string name in new[] { new string('x', 63), new string('x', 64), new string('é', 32) })
        {
            await s.MkdirAsync(name, Perms.P0755, Ct);
            Assert.Equal(FileKind.Directory, (await s.GetAttrAsync(name, Ct)).Kind);
            await s.RemoveAsync(name, Ct);
        }
        Assert.Equal(2, (await s.ReadDirAsync("/", Ct)).Count);
    }

    [Theory, MemberData(nameof(Dialects))]
    public async Task MalformedNamesRespectEachFieldsLegalExceptions(Dialect dialect)
    {
        await using ServerHarness h = await ServerHarness.StartAsync();
        await using NinePSession healthy = await h.ConnectAsync(dialect);
        foreach (string name in new[] { "", ".", "..", "a/b" })
        {
            foreach (byte[] frame in NameFrames(dialect, name))
            {
                MessageType type = (MessageType)frame[4];
                if ((name == ".." && type == MessageType.Twalk)
                    || (name.Length == 0 && type is MessageType.Twstat or MessageType.Txattrwalk))
                {
                    continue;
                }
                await using WireClient hostile = await WireClient.ConnectAsync(h, dialect, cancellationToken: Ct);
                await hostile.AttachAsync(1, Ct);
                await hostile.WalkAsync(2, 1, 2, ["hello.txt"], Ct);
                await hostile.SendRawAsync(frame, Ct);
                Assert.Equal(Errno.EPROTO, await BoundaryTests.WireError(hostile));
                Assert.Empty(await hostile.ReceiveFrameAsync(Ct));
                Assert.Equal(10ul, (await healthy.GetAttrAsync("hello.txt", Ct)).Size);
            }
        }
        await using WireClient legal = await WireClient.ConnectAsync(h, dialect, cancellationToken: Ct);
        await legal.AttachAsync(1, Ct);
        Assert.Single((await legal.WalkAsync(2, 1, 2, [".."], Ct)).Wqids);
        if (dialect == Dialect.P9_2000_L)
        {
            await legal.WalkAsync(3, 1, 3, ["hello.txt"], Ct);
            await legal.SendAsync(new Txattrwalk(4, 3, 4, ""), Ct);
            Assert.Equal(0ul, (await legal.ReceiveAsync<Rxattrwalk>(Ct)).Size);
        }
        else
        {
            await legal.SendAsync(new Twstat(4, 2, StatRecord.DontTouch with { Name = "" }), Ct);
            await legal.ReceiveAsync<Rwstat>(Ct);
        }
        Assert.Empty((await legal.WalkAsync(5, 1, 5, [], Ct)).Wqids);
    }

    internal static IEnumerable<byte[]> NameFrames(Dialect dialect, string name)
    {
        yield return Encode(new Twalk(10, 1, 3, [name]), dialect);
        if (dialect != Dialect.P9_2000_L)
        {
            yield return Encode(new Tcreate(10, 1, name, 0x1A4, 0, ""), dialect);
            yield return Encode(new Twstat(10, 2, new StatRecord { Name = name, Uid = "", Gid = "", Muid = "", Extension = "" }), dialect);
            yield break;
        }
        yield return Encode(new Tlcreate(10, 1, name, 0, 0x1A4, 1000), dialect);
        yield return Encode(new Tmkdir(10, 1, name, 0x1ED, 1000), dialect);
        yield return Encode(new Tsymlink(10, 1, name, "target", 1000), dialect);
        yield return Encode(new Tmknod(10, 1, name, 0x11A4, 0, 0, 1000), dialect);
        yield return Encode(new Tlink(10, 1, 2, name), dialect);
        yield return Encode(new Trename(10, 2, 1, name), dialect);
        yield return Encode(new Trenameat(10, 1, name, 1, "new"), dialect);
        yield return Encode(new Trenameat(10, 1, "hello.txt", 1, name), dialect);
        yield return Encode(new Tunlinkat(10, 1, name, 0), dialect);
        yield return Encode(new Txattrwalk(10, 2, 3, name), dialect);
        yield return Encode(new Txattrcreate(10, 2, name, 0, XattrFlags.None), dialect);
    }

    private static byte[] Encode<T>(T message, Dialect dialect) where T : struct, IMessage
    {
        ArrayBufferWriter<byte> buffer = new();
        MessageCodec.Encode(buffer, in message, dialect);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] ExtendName(byte[] frame)
    {
        int name = Array.IndexOf(frame, (byte)'z');
        Assert.True(name > 2);
        byte[] bad = new byte[frame.Length + 1];
        frame.AsSpan(0, name + 255).CopyTo(bad);
        bad[name + 255] = (byte)'z';
        frame.AsSpan(name + 255).CopyTo(bad.AsSpan(name + 256));
        BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(name - 2), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(bad, (uint)bad.Length);
        // Twstat carries both an outer and an inner stat record size.
        if ((MessageType)bad[4] == MessageType.Twstat)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(11), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(bad.AsSpan(11)) + 1));
            BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(13), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(bad.AsSpan(13)) + 1));
        }
        return bad;
    }
}
