using System.Buffers;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Transports;

namespace NineP.Server.Tests;

/// <summary>
/// A client that writes and reads raw frames. The shipped client is the right tool for most
/// server tests, but the flush and backpressure rules are about what a client may do <em>while</em>
/// a request is outstanding, and that needs a hand on the wire.
/// </summary>
internal sealed class WireClient : IAsyncDisposable
{
    private readonly INinePConnection _connection;

    private WireClient(INinePConnection connection) => _connection = connection;

    /// <summary>The dialect frames are encoded and decoded in.</summary>
    public Dialect Dialect { get; private set; } = Dialect.P9_2000;

    /// <summary>The connection under this client, for tests that assert on the close reason.</summary>
    public INinePConnection Connection => _connection;

    /// <summary>Dials a harness and negotiates a dialect.</summary>
    /// <param name="harness">The running server.</param>
    /// <param name="dialect">The dialect to ask for.</param>
    /// <param name="msize">The msize to ask for.</param>
    /// <param name="cancellationToken">Cancels the dial.</param>
    /// <returns>The connected client.</returns>
    public static async Task<WireClient> ConnectAsync(
        ServerHarness harness,
        Dialect dialect,
        uint msize = 8192,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(harness);

        WireClient client = new(await harness.DialAsync());
        await client.SendAsync(
            new Tversion(Constants.NOTAG, msize, NineP.Protocol.Negotiation.Negotiator.VersionString(dialect)),
            Dialect.P9_2000,
            cancellationToken);

        Rversion agreed = await client.ReceiveAsync<Rversion>(Dialect.P9_2000, cancellationToken);
        if (!NineP.Protocol.Negotiation.Negotiator.TryParseVersion(agreed.Version, out Dialect negotiated))
        {
            throw new NinePVersionException("the server refused every dialect", agreed.Version, agreed.Msize);
        }

        client.Dialect = negotiated;

        return client;
    }

    /// <summary>Attaches with NOFID and returns the root fid's qid.</summary>
    /// <param name="fid">The fid to bind.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The root qid.</returns>
    public async Task<Qid> AttachAsync(uint fid, CancellationToken cancellationToken = default)
    {
        await SendAsync(new Tattach(1, fid, Constants.NOFID, "glenda", "", Constants.NONUNAME), cancellationToken);
        return (await ReceiveAsync<Rattach>(cancellationToken)).Qid;
    }

    /// <summary>Walks from one fid to another and returns the qids.</summary>
    /// <param name="tag">The tag to use.</param>
    /// <param name="fid">The fid to walk from.</param>
    /// <param name="newFid">The fid to bind.</param>
    /// <param name="names">The path elements.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The reply.</returns>
    public async Task<Rwalk> WalkAsync(
        ushort tag, uint fid, uint newFid, string[] names, CancellationToken cancellationToken = default)
    {
        await SendAsync(new Twalk(tag, fid, newFid, names), cancellationToken);
        return await ReceiveAsync<Rwalk>(cancellationToken);
    }

    /// <summary>Sends a message in the negotiated dialect.</summary>
    /// <typeparam name="TMessage">The record to encode.</typeparam>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the frame is on the wire.</returns>
    public Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : struct, IMessage =>
        SendAsync(message, Dialect, cancellationToken);

    /// <summary>Sends a message in a chosen dialect.</summary>
    /// <typeparam name="TMessage">The record to encode.</typeparam>
    /// <param name="message">The message.</param>
    /// <param name="dialect">The dialect to encode in.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the frame is on the wire.</returns>
    public async Task SendAsync<TMessage>(
        TMessage message, Dialect dialect, CancellationToken cancellationToken = default)
        where TMessage : struct, IMessage
    {
        ArrayBufferWriter<byte> writer = new();
        MessageCodec.Encode(writer, in message, dialect);
        await _connection.WriteAsync(writer.WrittenMemory, cancellationToken);
    }

    /// <summary>Reads one frame and decodes it in the negotiated dialect.</summary>
    /// <typeparam name="TMessage">The record to decode.</typeparam>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The decoded message.</returns>
    public Task<TMessage> ReceiveAsync<TMessage>(CancellationToken cancellationToken = default)
        where TMessage : struct, IMessage =>
        ReceiveAsync<TMessage>(Dialect, cancellationToken);

    /// <summary>Reads one frame and decodes it in a chosen dialect.</summary>
    /// <typeparam name="TMessage">The record to decode.</typeparam>
    /// <param name="dialect">The dialect to decode in.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The decoded message.</returns>
    public async Task<TMessage> ReceiveAsync<TMessage>(
        Dialect dialect, CancellationToken cancellationToken = default)
        where TMessage : struct, IMessage =>
        MessageCodec.Decode<TMessage>(await ReceiveFrameAsync(cancellationToken), dialect);

    /// <summary>Writes bytes straight onto the wire, however malformed they are.</summary>
    /// <param name="bytes">What to send; the codec never sees it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the bytes are on the wire.</returns>
    public ValueTask SendRawAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
        _connection.WriteAsync(bytes, cancellationToken);

    /// <summary>Reads one whole frame.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The frame's bytes; empty once the server has closed.</returns>
    public async Task<byte[]> ReceiveFrameAsync(CancellationToken cancellationToken = default)
    {
        byte[] header = await ReadExactlyAsync(4, cancellationToken);
        if (header.Length < 4)
        {
            return [];
        }

        uint size = MessageCodec.PeekSize(header);
        byte[] rest = await ReadExactlyAsync((int)size - 4, cancellationToken);
        byte[] frame = new byte[size];

        header.CopyTo(frame, 0);
        rest.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Closes the connection.</summary>
    /// <returns>A task that completes when it has closed.</returns>
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int filled = 0;

        while (filled < count)
        {
            int read = await _connection.ReadAsync(buffer.AsMemory(filled), cancellationToken);
            if (read == 0)
            {
                return buffer.AsSpan(0, filled).ToArray();
            }

            filled += read;
        }

        return buffer;
    }
}
