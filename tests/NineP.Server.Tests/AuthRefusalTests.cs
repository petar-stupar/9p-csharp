using System.Buffers;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests;

/// <summary>
/// S-23: a server with no authenticator refuses <c>Tauth</c> outright, and the refusal has one
/// shape per dialect — <c>Rerror "authentication not required"</c> in 9P2000 and 9P2000.u,
/// <c>Rlerror ECONNREFUSED</c> (errno 111) in 9P2000.L (reference §5.2). The frames are compared
/// byte for byte, because a client that mounts against an unauthenticated server keys on exactly
/// these bytes.
/// </summary>
public sealed class AuthRefusalTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>9P2000 answers the Plan 9 wording, with no errno field on the wire.</summary>
    [Fact]
    public Task Legacy() => RefusalIsByteExactAsync(Dialect.P9_2000);

    /// <summary>9P2000.u answers the same wording, followed by the .u errno.</summary>
    [Fact]
    public Task DotU() => RefusalIsByteExactAsync(Dialect.P9_2000_u);

    /// <summary>9P2000.L answers <c>Rlerror</c> carrying <c>ECONNREFUSED</c>.</summary>
    [Fact]
    public Task DotL() => RefusalIsByteExactAsync(Dialect.P9_2000_L);

    private static async Task RefusalIsByteExactAsync(Dialect dialect)
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using WireClient client = await WireClient.ConnectAsync(harness, dialect, cancellationToken: Ct);

        await client.SendAsync(new Tauth(7, 1, "glenda", string.Empty, Constants.NONUNAME), Ct);
        byte[] frame = await client.ReceiveFrameAsync(Ct);

        Assert.Equal(Expected(dialect, 7), frame);
    }

    /// <summary>The refusal frame this dialect must produce, encoded from the message records.</summary>
    /// <param name="dialect">The negotiated dialect.</param>
    /// <param name="tag">The tag the Tauth carried.</param>
    /// <returns>The bytes the server must answer with.</returns>
    private static byte[] Expected(Dialect dialect, ushort tag)
    {
        ArrayBufferWriter<byte> writer = new();

        if (dialect == Dialect.P9_2000_L)
        {
            Rlerror lerror = new(tag, Errno.ECONNREFUSED);
            MessageCodec.Encode(writer, in lerror, dialect);
            return writer.WrittenSpan.ToArray();
        }

        Rerror error = new(tag, "authentication not required", Errno.ECONNREFUSED);
        MessageCodec.Encode(writer, in error, dialect);
        return writer.WrittenSpan.ToArray();
    }
}
