using System.Buffers;
using System.Text;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests;

/// <summary>
/// Reference §5.1 and §8 rule 9: what a connection may say before a dialect has been agreed, and
/// what a second <c>Tversion</c> does to everything that came before it.
/// </summary>
public sealed class ServerVersionTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// Rule 51: the first message must be a <c>Tversion</c>. Anything else is answered with the
    /// one error form every 9P peer can decode — an <c>Rerror</c> with an ename and nothing after
    /// it, never an <c>Rlerror</c> and never a <c>.u</c> errno — and the connection then closes.
    /// The frame is compared byte for byte, because "9P2000-shaped" is a statement about bytes.
    /// </summary>
    [Fact]
    public async Task PreNegotiationErrorIs9P2000Shaped()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using INinePConnection wire = await harness.DialAsync();

        await Send(wire, new Tclunk(7, 1), Dialect.P9_2000_L);

        byte[] reply = await ReadFrameAsync(wire);
        Assert.Equal(Expected(7, "version not negotiated"), reply);

        // The connection is closed after that reply; nothing more arrives on it.
        Assert.Empty(await ReadFrameAsync(wire));
    }

    /// <summary>
    /// Rule 52: a <c>Tversion</c> mid-session resets everything. The second one negotiates from
    /// scratch — a different dialect and a different msize are agreed on the same connection —
    /// which is the visible half of "a successful Tversion is a new session".
    /// </summary>
    [Fact]
    public async Task SecondTversionResetsSession()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync();
        await using INinePConnection wire = await harness.DialAsync();

        await Send(wire, new Tversion(Constants.NOTAG, 8192, Constants.Version9P2000L), Dialect.P9_2000);
        Rversion first = await ReadAsync<Rversion>(wire, Dialect.P9_2000);
        Assert.Equal(Constants.Version9P2000L, first.Version);
        Assert.Equal(8192u, first.Msize);

        await Send(wire, new Tversion(Constants.NOTAG, 4096, Constants.Version9P2000u), Dialect.P9_2000);
        Rversion second = await ReadAsync<Rversion>(wire, Dialect.P9_2000);

        // Nothing of the first session survives: neither its dialect nor its msize.
        Assert.Equal(Constants.Version9P2000u, second.Version);
        Assert.Equal(4096u, second.Msize);
    }

    /// <summary>
    /// version(5): a version string the server will not speak is refused with
    /// <c>Rversion "unknown"</c> echoing the client's own msize — never an <c>Rerror</c>, which
    /// the manual forbids here — and the connection stays open for another <c>Tversion</c> only.
    /// </summary>
    [Fact]
    public async Task UnknownVersionKeepsConnectionForTversionOnly()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync(
            options => options with { Dialects = new HashSet<Dialect> { Dialect.P9_2000_L } });
        await using INinePConnection wire = await harness.DialAsync();

        await Send(wire, new Tversion(Constants.NOTAG, 9000, "9P2000"), Dialect.P9_2000);
        Rversion refusal = await ReadAsync<Rversion>(wire, Dialect.P9_2000);

        Assert.Equal(Constants.VersionUnknown, refusal.Version);
        Assert.Equal(9000u, refusal.Msize);

        // Still pre-negotiation: a Tversion is answered, anything else is not.
        await Send(wire, new Tversion(Constants.NOTAG, 8192, Constants.Version9P2000L), Dialect.P9_2000);
        Rversion agreed = await ReadAsync<Rversion>(wire, Dialect.P9_2000);
        Assert.Equal(Constants.Version9P2000L, agreed.Version);
    }

    /// <summary>An unknown version answered after a good one leaves the session pre-negotiation again.</summary>
    [Fact]
    public async Task AnUnknownVersionAfterAGoodOneReturnsToPreNegotiation()
    {
        await using ServerHarness harness = await ServerHarness.StartAsync(
            options => options with { Dialects = new HashSet<Dialect> { Dialect.P9_2000_L } });
        await using INinePConnection wire = await harness.DialAsync();

        await Send(wire, new Tversion(Constants.NOTAG, 8192, Constants.Version9P2000L), Dialect.P9_2000);
        await ReadAsync<Rversion>(wire, Dialect.P9_2000);

        await Send(wire, new Tversion(Constants.NOTAG, 8192, "XYZ"), Dialect.P9_2000);
        Rversion refusal = await ReadAsync<Rversion>(wire, Dialect.P9_2000);
        Assert.Equal(Constants.VersionUnknown, refusal.Version);

        await Send(wire, new Tclunk(3, 1), Dialect.P9_2000_L);
        Assert.Equal(Expected(3, "version not negotiated"), await ReadFrameAsync(wire));
    }

    private static byte[] Expected(ushort tag, string ename)
    {
        byte[] text = Encoding.UTF8.GetBytes(ename);
        byte[] frame = new byte[4 + 1 + 2 + 2 + text.Length];

        BitConverter.TryWriteBytes(frame.AsSpan(0), (uint)frame.Length);
        frame[4] = (byte)MessageType.Rerror;
        BitConverter.TryWriteBytes(frame.AsSpan(5), tag);
        BitConverter.TryWriteBytes(frame.AsSpan(7), (ushort)text.Length);
        text.CopyTo(frame, 9);

        return frame;
    }

    private static async Task Send<TMessage>(INinePConnection wire, TMessage message, Dialect dialect)
        where TMessage : struct, IMessage
    {
        ArrayBufferWriter<byte> writer = new();
        MessageCodec.Encode(writer, in message, dialect);
        await wire.WriteAsync(writer.WrittenMemory, TestDeadlines.Wrap(TestContext.Current.CancellationToken));
    }

    private static async Task<TMessage> ReadAsync<TMessage>(INinePConnection wire, Dialect dialect)
        where TMessage : struct, IMessage =>
        MessageCodec.Decode<TMessage>(await ReadFrameAsync(wire), dialect);

    private static async Task<byte[]> ReadFrameAsync(INinePConnection wire)
    {
        byte[] header = await ReadExactlyAsync(wire, 4);
        if (header.Length < 4)
        {
            return [];
        }

        uint size = MessageCodec.PeekSize(header);
        byte[] rest = await ReadExactlyAsync(wire, (int)size - 4);
        byte[] frame = new byte[size];

        header.CopyTo(frame, 0);
        rest.CopyTo(frame, 4);
        return frame;
    }

    private static async Task<byte[]> ReadExactlyAsync(INinePConnection wire, int count)
    {
        byte[] buffer = new byte[count];
        int filled = 0;

        while (filled < count)
        {
            int read = await wire.ReadAsync(buffer.AsMemory(filled), Ct);
            if (read == 0)
            {
                return buffer.AsSpan(0, filled).ToArray();
            }

            filled += read;
        }

        return buffer;
    }
}
