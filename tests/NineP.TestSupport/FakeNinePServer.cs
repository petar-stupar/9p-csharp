using System.Buffers;
using System.Buffers.Binary;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Negotiation;
using NineP.Protocol.Transports;

namespace NineP.TestSupport;

/// <summary>
/// The other end of the wire for the client tests: a server that speaks 9P frames and does exactly
/// what the test scripts, and nothing more. There is no server package yet, so this is what proves
/// the client against the protocol rather than against itself — it runs over
/// <see cref="MemoryTransport"/> (S-31), and every byte it reads and writes is a real frame.
/// </summary>
public sealed class FakeNinePServer : IAsyncDisposable
{
    private readonly INinePConnection _connection;

    private FakeNinePServer(INinePConnection connection) => _connection = connection;

    /// <summary>The dialect frames are encoded and decoded in; the test sets it after Rversion.</summary>
    public Dialect Dialect { get; set; } = Dialect.P9_2000;

    /// <summary>Wraps an already-accepted connection as a fake server.</summary>
    /// <param name="connection">The accepted server end.</param>
    /// <returns>The fake server over it.</returns>
    public static FakeNinePServer Wrap(INinePConnection connection) => new(connection);

    /// <summary>Creates a connected client end and a fake server end.</summary>
    /// <returns>The client connection and the server that answers it.</returns>
    public static (INinePConnection Client, FakeNinePServer Server) CreatePair()
    {
        (INinePConnection client, INinePConnection server) = MemoryTransport.CreatePair();
        return (client, new FakeNinePServer(server));
    }

    /// <summary>Reads one whole frame, <c>size[4]</c> included.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The frame's bytes; empty once the client has closed.</returns>
    public async Task<byte[]> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        byte[] header = await ReadExactlyAsync(4, cancellationToken);
        if (header.Length < 4)
        {
            return [];
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        byte[] rest = await ReadExactlyAsync((int)size - 4, cancellationToken);
        byte[] frame = new byte[size];

        header.CopyTo(frame, 0);
        rest.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Reads one whole frame and decodes it as the record the test expects.</summary>
    /// <typeparam name="TMessage">The record to decode.</typeparam>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The decoded message.</returns>
    public async Task<TMessage> ReadAsync<TMessage>(CancellationToken cancellationToken = default)
        where TMessage : struct, IMessage =>
        MessageCodec.Decode<TMessage>(await ReadFrameAsync(cancellationToken), Dialect);

    /// <summary>Writes one message as a frame.</summary>
    /// <typeparam name="TMessage">The record to encode.</typeparam>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the frame is on the wire.</returns>
    public async Task WriteAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : struct, IMessage
    {
        ArrayBufferWriter<byte> writer = new();
        MessageCodec.Encode(writer, in message, Dialect);
        await _connection.WriteAsync(writer.WrittenMemory, cancellationToken);
    }

    /// <summary>Writes bytes the test built itself, so it can put an illegal frame on the wire.</summary>
    /// <param name="frame">The bytes to send, exactly as they are.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the bytes are on the wire.</returns>
    public Task WriteRawAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default) =>
        _connection.WriteAsync(frame, cancellationToken).AsTask();

    /// <summary>Answers the opening <c>Tversion</c>, agreeing on a dialect and an msize.</summary>
    /// <param name="version">The version string to answer with, for example "9P2000.L".</param>
    /// <param name="msize">The msize to answer with; never larger than the client's.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The client's proposal, so the test can assert on it.</returns>
    public async Task<Tversion> NegotiateAsync(
        string version, uint? msize = null, CancellationToken cancellationToken = default)
    {
        Tversion proposal = await ReadAsync<Tversion>(cancellationToken);
        uint agreed = Math.Min(msize ?? proposal.Msize, proposal.Msize);

        await WriteAsync(new Rversion(Constants.NOTAG, agreed, version), cancellationToken);

        if (Negotiator.TryParseVersion(version, out Dialect dialect))
        {
            Dialect = dialect;
        }

        return proposal;
    }

    /// <summary>Closes the server end of the wire.</summary>
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
