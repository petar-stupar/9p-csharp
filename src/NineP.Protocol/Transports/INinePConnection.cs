namespace NineP.Protocol.Transports;

/// <summary>
/// One bidirectional 9P byte stream. <see cref="WriteAsync"/> is called once per complete 9P
/// message, so a message-oriented transport can put each message in one frame of its own.
/// </summary>
public interface INinePConnection : IAsyncDisposable
{
    /// <summary>The address of the peer.</summary>
    NinePAddress RemoteAddress { get; }

    /// <summary>What the transport learned about the peer during the handshake; null when nothing.</summary>
    PeerIdentity? PeerIdentity { get; }

    /// <summary>Reads up to <c>buffer.Length</c> bytes.</summary>
    /// <param name="buffer">Where the bytes go.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes read; zero at the end of the stream.</returns>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Writes exactly one complete 9P message.</summary>
    /// <param name="message">The frame, <c>size[4]</c> included.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the message has been handed to the transport.</returns>
    ValueTask WriteAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);

    /// <summary>Closes with a reason; the reason reaches the peer where the transport can express it.</summary>
    /// <param name="reason">Why the connection is closing.</param>
    /// <param name="cancellationToken">Cancels the close handshake.</param>
    /// <returns>A task that completes when the close has been sent.</returns>
    ValueTask CloseAsync(CloseReason reason, CancellationToken cancellationToken = default);
}
