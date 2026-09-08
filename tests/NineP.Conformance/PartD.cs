using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Negotiation;
using NineP.Protocol.Transports;

namespace NineP.Conformance;

/// <summary>
/// Part D of <c>docs/9p/fixtures/conformance.md</c>: the hostile client, against a live
/// <c>jsonfs</c> over TCP with its shipped defaults. The mutation matrix half of Part D is the
/// codec's own suite; this half is the five attacks the fixture names — a size lie, a
/// <c>nwname = 17</c> walk, 70 000 distinct fids, 300 concurrent tags, and half a header followed
/// by 31 s of silence — and the one thing that makes them a test rather than a demonstration: the
/// cli keeps being answered on a <b>second</b> connection between every one of them.
/// </summary>
internal static class PartD
{
    private const int Attempts = 70_000;
    private const int Tags = 300;

    /// <summary>Runs the hostile client once, over TCP, in 9P2000.L.</summary>
    /// <param name="document">The document <c>jsonfs</c> serves.</param>
    /// <returns>The outcome.</returns>
    public static async Task<ConformanceOutcome> RunAsync(string document)
    {
        const string Name = "9P2000.L over tcp";

        try
        {
            await using ProcessTarget target =
                await ProcessTarget.StartAsync(Dialect.P9_2000_L, "tcp", document);

            await StillServingAsync(target, "before the hostile client").ConfigureAwait(false);

            await SizeLieAsync(target).ConfigureAwait(false);
            await StillServingAsync(target, "after the size lie").ConfigureAwait(false);

            await OverlongWalkAsync(target).ConfigureAwait(false);
            await StillServingAsync(target, "after the nwname=17 walk").ConfigureAwait(false);

            await FidFloodAsync(target).ConfigureAwait(false);
            await StillServingAsync(target, "after the fid flood").ConfigureAwait(false);

            await TagFloodAsync(target).ConfigureAwait(false);
            await StillServingAsync(target, "after the tag flood").ConfigureAwait(false);

            await HalfHeaderAsync(target).ConfigureAwait(false);
            await StillServingAsync(target, "after the slowloris").ConfigureAwait(false);

            return new ConformanceOutcome(Name, "D", true, string.Empty);
        }
        catch (ConformanceFailure failure)
        {
            return new ConformanceOutcome(Name, "D", false, failure.Message);
        }
    }

    /// <summary>The second connection: the ordinary cli, which must keep working throughout.</summary>
    private static async Task StillServingAsync(ProcessTarget target, string when)
    {
        CliResult listing = await target.RunAsync(["ls", "/"]).ConfigureAwait(false);

        if (listing.ExitCode != 0 || listing.Stdout.Length == 0)
        {
            throw new ConformanceFailure(string.Format(
                CultureInfo.InvariantCulture,
                "the server stopped answering a second connection {0}: exit {1} {2}",
                when,
                listing.ExitCode,
                listing.Stderr.Trim()));
        }
    }

    /// <summary>A frame claiming 0xFFFFFFFF bytes closes that connection and nothing else.</summary>
    private static async Task SizeLieAsync(ProcessTarget target)
    {
        await using HostileConnection hostile = await HostileConnection.OpenAsync(target).ConfigureAwait(false);

        await hostile.SendRawAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, (byte)MessageType.Tstat, 0x01, 0x00 })
            .ConfigureAwait(false);
        await hostile.ExpectClosedAsync("the size lie", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    /// <summary>A walk of seventeen elements is EPROTO and a close (reference §8 rule 2).</summary>
    private static async Task OverlongWalkAsync(ProcessTarget target)
    {
        await using HostileConnection hostile = await HostileConnection.OpenAsync(target).ConfigureAwait(false);
        await hostile.AttachAsync().ConfigureAwait(false);

        await hostile.SendRawAsync(OverlongWalk(tag: 9, fid: 1, newFid: 2, elements: 17)).ConfigureAwait(false);

        byte[] reply = await hostile.ReceiveFrameAsync().ConfigureAwait(false);
        if (MessageCodec.PeekType(reply) != MessageType.Rlerror)
        {
            throw new ConformanceFailure("a nwname=17 walk was answered " + MessageCodec.PeekType(reply));
        }

        int ecode = MessageCodec.Decode<Rlerror>(reply, Dialect.P9_2000_L).Ecode;
        if (ecode != Errno.EPROTO)
        {
            throw new ConformanceFailure(string.Format(
                CultureInfo.InvariantCulture, "a nwname=17 walk was answered errno {0}, not EPROTO", ecode));
        }

        await hostile.ExpectClosedAsync("the nwname=17 walk", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    /// <summary>70 000 fids meet the per-connection cap; the connection itself stays up.</summary>
    private static async Task FidFloodAsync(ProcessTarget target)
    {
        const int Batch = 128;

        await using HostileConnection hostile = await HostileConnection.OpenAsync(target).ConfigureAwait(false);
        await hostile.AttachAsync().ConfigureAwait(false);

        int bound = 0;
        int refused = 0;

        for (int start = 0; start < Attempts; start += Batch)
        {
            int count = Math.Min(Batch, Attempts - start);

            for (int i = 0; i < count; i++)
            {
                await hostile.SendAsync(new Twalk((ushort)(i + 1), 1, (uint)(start + i + 2), []))
                    .ConfigureAwait(false);
            }

            for (int i = 0; i < count; i++)
            {
                byte[] reply = await hostile.ReceiveFrameAsync().ConfigureAwait(false);
                if (MessageCodec.PeekType(reply) == MessageType.Rwalk)
                {
                    bound++;
                    continue;
                }

                int ecode = MessageCodec.Decode<Rlerror>(reply, Dialect.P9_2000_L).Ecode;
                if (ecode != Errno.ENFILE)
                {
                    throw new ConformanceFailure(string.Format(
                        CultureInfo.InvariantCulture, "the fid cap answered errno {0}, not ENFILE", ecode));
                }

                refused++;
            }
        }

        int cap = Limits.Default.MaxFidsPerConnection;
        if (bound != cap - 1 || refused != Attempts - bound)
        {
            throw new ConformanceFailure(string.Format(
                CultureInfo.InvariantCulture,
                "{0} of {1} walks bound a fid and {2} were refused; the cap is {3}",
                bound,
                Attempts,
                refused,
                cap));
        }

        // The connection is still a connection after the flood, which is the whole point.
        await hostile.SendAsync(new Tclunk(1, 2)).ConfigureAwait(false);
        if (MessageCodec.PeekType(await hostile.ReceiveFrameAsync().ConfigureAwait(false)) != MessageType.Rclunk)
        {
            throw new ConformanceFailure("the connection stopped answering after the fid flood");
        }
    }

    /// <summary>Every ordinary tag is answered once, with EAGAIN allowed at capacity; flush still progresses.</summary>
    private static async Task TagFloodAsync(ProcessTarget target)
    {
        await using HostileConnection hostile = await HostileConnection.OpenAsync(target).ConfigureAwait(false);
        await hostile.AttachAsync().ConfigureAwait(false);

        for (int i = 0; i < Tags; i++)
        {
            await hostile.SendAsync(new Tgetattr((ushort)(i + 1), 1, GetAttrMask.Basic)).ConfigureAwait(false);
        }

        // oldtag is unknown, so all ordinary tags still require a response. The control request
        // follows the flood on the same stream and must never be refused for worker exhaustion.
        const ushort FlushTag = Tags + 1;
        await hostile.SendAsync(new Tflush(FlushTag, 65000)).ConfigureAwait(false);
        HashSet<ushort> answered = [];
        for (int i = 0; i <= Tags; i++)
        {
            byte[] reply = await hostile.ReceiveFrameAsync().ConfigureAwait(false);
            ushort tag = MessageCodec.PeekTag(reply);
            MessageType type = MessageCodec.PeekType(reply);
            bool valid = tag == FlushTag
                ? type == MessageType.Rflush
                : tag is >= 1 and <= Tags && (type == MessageType.Rgetattr
                    || (type == MessageType.Rlerror
                        && MessageCodec.Decode<Rlerror>(reply, Dialect.P9_2000_L).Ecode == Errno.EAGAIN));
            if (!valid || !answered.Add(tag))
            {
                throw new ConformanceFailure(string.Format(CultureInfo.InvariantCulture,
                    "invalid or duplicate reply {0} of {1} under the tag flood: tag {2}, type {3}",
                    i + 1, Tags + 1, tag, type));
            }
        }

        await hostile.SendAsync(new Tgetattr(1, 1, GetAttrMask.Basic)).ConfigureAwait(false);
        if (MessageCodec.PeekType(await hostile.ReceiveFrameAsync().ConfigureAwait(false)) != MessageType.Rgetattr)
        {
            throw new ConformanceFailure("the tag flood did not release ordinary request capacity");
        }
    }

    /// <summary>
    /// Half a header and then silence. The fixture's 31 s is what the shipped
    /// <see cref="Limits.ReadHeaderTimeout"/> of 30 s is measured against, so this step waits for
    /// the close rather than for the clock, and fails if it has not come by 45 s.
    /// </summary>
    private static async Task HalfHeaderAsync(ProcessTarget target)
    {
        await using HostileConnection hostile = await HostileConnection.OpenAsync(target).ConfigureAwait(false);

        await hostile.SendRawAsync(new byte[] { 0x13, 0x00 }).ConfigureAwait(false);
        await hostile.ExpectClosedAsync("half a header and silence", TimeSpan.FromSeconds(45))
            .ConfigureAwait(false);
    }

    private static byte[] OverlongWalk(ushort tag, uint fid, uint newFid, int elements)
    {
        int size = Constants.HDRSZ + 4 + 4 + 2 + (elements * (2 + 1));
        byte[] frame = new byte[size];

        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)size);
        frame[4] = (byte)MessageType.Twalk;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), fid);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(11), newFid);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(15), (ushort)elements);

        for (int i = 0; i < elements; i++)
        {
            int at = 17 + (i * 3);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(at), 1);
            frame[at + 2] = (byte)'a';
        }

        return frame;
    }
}

/// <summary>
/// A 9P connection that says whatever it is told to, however illegal. The shipped client refuses
/// to build most of what Part D has to send, which is the reason this exists.
/// </summary>
internal sealed class HostileConnection : IAsyncDisposable
{
    private const uint Msize = 8192;

    private readonly INinePConnection _connection;

    private HostileConnection(INinePConnection connection) => _connection = connection;

    /// <summary>Dials the target over TCP and negotiates 9P2000.L.</summary>
    /// <param name="target">The running <c>jsonfs</c>.</param>
    /// <returns>The connected client.</returns>
    public static async Task<HostileConnection> OpenAsync(ProcessTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        TcpTransport transport = new();
        HostileConnection hostile = new(
            await transport.ConnectAsync(NinePAddress.Parse(target.Address)).ConfigureAwait(false));

        await hostile.SendAsync(
            new Tversion(Constants.NOTAG, Msize, Negotiator.VersionString(Dialect.P9_2000_L)),
            Dialect.P9_2000).ConfigureAwait(false);

        Rversion agreed = MessageCodec.Decode<Rversion>(
            await hostile.ReceiveFrameAsync().ConfigureAwait(false), Dialect.P9_2000);

        return Negotiator.TryParseVersion(agreed.Version, out Dialect negotiated)
            && negotiated == Dialect.P9_2000_L
            ? hostile
            : throw new ConformanceFailure("jsonfs refused 9P2000.L: " + agreed.Version);
    }

    /// <summary>Attaches fid 1 with no afid.</summary>
    /// <returns>A task that completes when the attach is answered.</returns>
    public async Task AttachAsync()
    {
        await SendAsync(new Tattach(1, 1, Constants.NOFID, "glenda", string.Empty, Constants.NONUNAME))
            .ConfigureAwait(false);

        byte[] reply = await ReceiveFrameAsync().ConfigureAwait(false);
        if (MessageCodec.PeekType(reply) != MessageType.Rattach)
        {
            throw new ConformanceFailure("the hostile connection could not attach");
        }
    }

    /// <summary>Encodes and sends one message in 9P2000.L.</summary>
    /// <typeparam name="TMessage">The record to encode.</typeparam>
    /// <param name="message">The message.</param>
    /// <returns>A task that completes when the frame is on the wire.</returns>
    public ValueTask SendAsync<TMessage>(TMessage message)
        where TMessage : struct, IMessage =>
        SendAsync(message, Dialect.P9_2000_L);

    /// <summary>Encodes and sends one message in a chosen dialect.</summary>
    /// <typeparam name="TMessage">The record to encode.</typeparam>
    /// <param name="message">The message.</param>
    /// <param name="dialect">The dialect to encode in.</param>
    /// <returns>A task that completes when the frame is on the wire.</returns>
    public ValueTask SendAsync<TMessage>(TMessage message, Dialect dialect)
        where TMessage : struct, IMessage
    {
        ArrayBufferWriter<byte> writer = new();
        MessageCodec.Encode(writer, in message, dialect);
        return _connection.WriteAsync(writer.WrittenMemory);
    }

    /// <summary>Sends bytes the codec never saw.</summary>
    /// <param name="bytes">Whatever is to go on the wire.</param>
    /// <returns>A task that completes when the bytes are on the wire.</returns>
    public ValueTask SendRawAsync(ReadOnlyMemory<byte> bytes) => _connection.WriteAsync(bytes);

    /// <summary>Reads one whole frame.</summary>
    /// <returns>The frame's bytes.</returns>
    public async Task<byte[]> ReceiveFrameAsync()
    {
        byte[] header = await ReadExactlyAsync(4).ConfigureAwait(false);
        if (header.Length < 4)
        {
            throw new ConformanceFailure("the connection closed before a reply arrived");
        }

        uint size = MessageCodec.PeekSize(header);
        byte[] frame = new byte[size];
        header.CopyTo(frame, 0);
        (await ReadExactlyAsync((int)size - 4).ConfigureAwait(false)).CopyTo(frame, 4);

        return frame;
    }

    /// <summary>Waits for the server to close this connection, and fails if it does not.</summary>
    /// <param name="what">What the connection did to deserve it, for the report.</param>
    /// <param name="within">How long to wait.</param>
    /// <returns>A task that completes when the connection has closed.</returns>
    public async Task ExpectClosedAsync(string what, TimeSpan within)
    {
        using CancellationTokenSource budget = new(within);
        byte[] buffer = new byte[512];

        try
        {
            while (await _connection.ReadAsync(buffer, budget.Token).ConfigureAwait(false) > 0)
            {
                // A reply may precede the close; only the end of the stream ends this loop.
            }
        }
        catch (OperationCanceledException)
        {
            throw new ConformanceFailure(string.Format(
                CultureInfo.InvariantCulture,
                "the server did not close the connection after {0} within {1:0.#} s",
                what,
                within.TotalSeconds));
        }
        catch (IOException)
        {
            // A reset is a close; TCP cannot carry a reason either way.
        }
    }

    /// <summary>Closes the connection.</summary>
    /// <returns>A task that completes when it has closed.</returns>
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private async Task<byte[]> ReadExactlyAsync(int count)
    {
        byte[] buffer = new byte[count];
        int filled = 0;

        while (filled < count)
        {
            int read = await _connection.ReadAsync(buffer.AsMemory(filled)).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.AsSpan(0, filled).ToArray();
            }

            filled += read;
        }

        return buffer;
    }
}
