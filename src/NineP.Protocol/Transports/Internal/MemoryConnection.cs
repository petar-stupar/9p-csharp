using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Threading.Channels;

namespace NineP.Protocol.Transports.Internal;

/// <summary>One end of an in-process connection: a pipe in, a pipe out, and a close reason.</summary>
internal sealed class MemoryConnection(
    NinePAddress address,
    PipeReader inbound,
    PipeWriter outbound,
    MemoryEndpoint self,
    MemoryEndpoint peer)
    : INinePConnection
{
    /// <summary>The address of the peer.</summary>
    public NinePAddress RemoteAddress => address;

    /// <summary>Nothing is learned about a peer in this process.</summary>
    public PeerIdentity? PeerIdentity => null;

    /// <summary>Why the peer closed, once it has; null while the peer is still open.</summary>
    public CloseReason? PeerCloseReason => peer.Reason;

    /// <summary>Why this end closed, once it has.</summary>
    public CloseReason? LocalCloseReason => self.Reason;

    /// <summary>Reads up to <c>buffer.Length</c> bytes from the peer.</summary>
    /// <param name="buffer">Where the bytes go.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes read; zero once the peer has closed and the pipe has drained.</returns>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (self.Reason is not null)
            {
                return 0;
            }

            ReadResult read = await inbound.ReadAsync(cancellationToken).ConfigureAwait(false);
            long available = read.Buffer.Length;

            if (available == 0)
            {
                if (read.IsCompleted)
                {
                    inbound.AdvanceTo(read.Buffer.Start);
                    return 0;
                }

                inbound.AdvanceTo(read.Buffer.Start, read.Buffer.End);
                continue;
            }

            int taken = (int)Math.Min(available, buffer.Length);
            read.Buffer.Slice(0, taken).CopyTo(buffer.Span[..taken]);
            inbound.AdvanceTo(read.Buffer.GetPosition(taken));
            return taken;
        }
    }

    /// <summary>Writes one complete 9P message to the peer.</summary>
    /// <param name="message">The frame, <c>size[4]</c> included.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the bytes are in the pipe.</returns>
    /// <exception cref="ObjectDisposedException">This end has already closed.</exception>
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(self.Reason is not null, this);

        await outbound.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes this end, recording the reason where the peer can read it.</summary>
    /// <param name="reason">Why the connection is closing.</param>
    /// <param name="cancellationToken">Unused; an in-process close cannot block.</param>
    /// <returns>A task that completes once the writing half has been completed.</returns>
    public async ValueTask CloseAsync(CloseReason reason, CancellationToken cancellationToken = default)
    {
        // Only the writing half is completed: that is what the peer sees as end of stream, and it
        // leaves anything already in flight readable rather than turning it into an exception.
        if (self.TryClose(reason))
        {
            await outbound.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Closes this end normally if it is still open.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync() => CloseAsync(CloseReason.Normal);
}
