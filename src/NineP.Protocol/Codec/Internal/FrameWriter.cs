using System.Globalization;
using System.IO.Pipelines;
using NineP.Protocol.Messages;

namespace NineP.Protocol.Codec.Internal;

/// <summary>
/// Writes whole 9P frames into a pipe, bounded by the same rule the reader enforces: a frame
/// larger than the active bound is never put on the wire, because the peer would be entitled to
/// close the connection on it. The size field is reserved and back-patched by
/// <see cref="MessageWriter"/>, so what goes out is the length that was produced.
/// </summary>
internal sealed class FrameWriter
{
    private readonly PipeWriter _writer;
    private readonly Limits _limits;
    private uint? _msize;

    /// <summary>Creates a writer over a pipe.</summary>
    /// <param name="writer">The pipe the transport drains.</param>
    /// <param name="limits">The bounds this side was configured with.</param>
    public FrameWriter(PipeWriter writer, Limits limits)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(limits);

        _writer = writer;
        _limits = limits;
    }

    /// <summary>The largest frame this writer will emit right now.</summary>
    public uint ActiveBound => _msize ?? _limits.PreNegotiationFrameCap;

    /// <summary>Takes the negotiated msize as the bound from the next frame onward.</summary>
    /// <param name="msize">The msize the two sides agreed on.</param>
    /// <exception cref="ArgumentOutOfRangeException">The msize is outside the configured range.</exception>
    public void SetNegotiatedMsize(uint msize)
    {
        if (msize < _limits.MinMsize || msize > _limits.MaxMsize)
        {
            throw new ArgumentOutOfRangeException(nameof(msize), msize, "outside the configured msize range");
        }

        _msize = msize;
    }

    /// <summary>Goes back to the pre-negotiation cap, as a mid-session Tversion requires.</summary>
    public void ResetToPreNegotiation() => _msize = null;

    /// <summary>Writes one frame into the pipe without flushing it.</summary>
    /// <typeparam name="TMessage">The record being written.</typeparam>
    /// <param name="message">The message.</param>
    /// <param name="dialect">The dialect the session negotiated.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="NinePProtocolException">The frame would exceed the active bound.</exception>
    public int Write<TMessage>(in TMessage message, Dialect dialect)
        where TMessage : struct, IMessage
    {
        int size = MessageWriter.GetEncodedSize(in message, dialect);
        if ((uint)size > ActiveBound)
        {
            throw new NinePProtocolException(
                ProtocolErrorKind.Size,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} is {1} bytes, over the {2}-byte bound",
                    MessageTypes.GetName(TMessage.Type),
                    size,
                    ActiveBound));
        }

        int written = MessageWriter.Write(_writer.GetSpan(size)[..size], in message, dialect);
        _writer.Advance(written);
        return written;
    }

    /// <summary>Flushes whatever has been written.</summary>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>The pipe's flush result.</returns>
    public ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) =>
        _writer.FlushAsync(cancellationToken);
}
