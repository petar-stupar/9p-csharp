using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using NineP.Protocol.Transports;

namespace NineP.Protocol.Codec.Internal;

/// <summary>
/// Reads whole 9P frames off a pipe, bounded (reference §8 rule 1). The size field is peeked and
/// validated against the active bound before a single byte is waited for, so a peer cannot make
/// the connection reserve memory by lying: until <c>Rversion</c> the bound is the constant
/// <see cref="Limits.PreNegotiationFrameCap"/>, never the configured maximum, and after it the
/// negotiated msize. A size violation closes the connection; it cannot be resynced.
/// A peer that begins a frame and then stops sending is closed with
/// <see cref="CloseReason.Timeout"/> once <see cref="Limits.ReadHeaderTimeout"/> has elapsed
/// (§6.8, RK-62): the deadline runs from the first byte of a frame to its last, so a connection
/// that is idle <em>between</em> frames is untouched and a slowloris cannot hold a slot for ever.
/// </summary>
internal sealed class FrameReader
{
    private const int SizeFieldLength = 4;

    private readonly PipeReader _reader;
    private readonly Limits _limits;
    private readonly ArrayPool<byte> _pool;
    private uint? _msize;
    private CancellationTokenSource? _deadline;

    /// <summary>Creates a reader over a pipe.</summary>
    /// <param name="reader">The pipe the transport writes into.</param>
    /// <param name="limits">The bounds this side was configured with.</param>
    /// <param name="pool">The pool multi-segment frames are copied into.</param>
    public FrameReader(PipeReader reader, Limits limits, ArrayPool<byte> pool)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(pool);

        _reader = reader;
        _limits = limits;
        _pool = pool;
    }

    /// <summary>The largest frame this reader will accept right now.</summary>
    public uint ActiveBound => _msize ?? _limits.PreNegotiationFrameCap;

    /// <summary>True once a dialect and an msize have been agreed.</summary>
    public bool IsNegotiated => _msize.HasValue;

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

    /// <summary>Reads one whole frame, or reports why there will not be one.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The frame, the end of the stream, or a framing violation.</returns>
    public async ValueTask<FrameReadResult> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadOneAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deadline?.Dispose();
            _deadline = null;
        }
    }

    private async ValueTask<FrameReadResult> ReadOneAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult read;
            try
            {
                read = await _reader.ReadAsync(_deadline?.Token ?? cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Expired(cancellationToken))
            {
                return FrameReadResult.HeaderTimeout();
            }

            ReadOnlySequence<byte> buffer = read.Buffer;

            if (buffer.Length < SizeFieldLength)
            {
                _reader.AdvanceTo(buffer.Start, buffer.End);
                if (!read.IsCompleted)
                {
                    StartDeadline(buffer.IsEmpty, cancellationToken);
                    continue;
                }

                return buffer.IsEmpty ? FrameReadResult.Closed() : FrameReadResult.Truncated();
            }

            uint size = PeekSize(buffer);
            if (size < Constants.HDRSZ)
            {
                _reader.AdvanceTo(buffer.Start, buffer.Start);
                return FrameReadResult.SizeViolation(CloseReason.ProtocolViolation);
            }

            if (size > ActiveBound)
            {
                _reader.AdvanceTo(buffer.Start, buffer.Start);
                return FrameReadResult.SizeViolation(CloseReason.MessageTooLarge);
            }

            if (buffer.Length < size)
            {
                _reader.AdvanceTo(buffer.Start, buffer.End);
                if (!read.IsCompleted)
                {
                    StartDeadline(empty: false, cancellationToken);
                    continue;
                }

                return FrameReadResult.Truncated();
            }

            // CA2000: the lease is the caller's to dispose — that is the whole contract of §6.1,
            // where the frame stays valid for exactly as long as the handler call that reads it.
#pragma warning disable CA2000
            return FrameReadResult.Frame(Take(buffer, (int)size));
#pragma warning restore CA2000
        }
    }

    /// <summary>
    /// Arms the partial-frame deadline the first time this frame is found incomplete. It is armed
    /// once per frame and never re-armed, so a peer that drips a byte every 29 s is closed on the
    /// same schedule as one that sends nothing at all.
    /// </summary>
    /// <param name="empty">True when nothing of a frame has arrived yet.</param>
    /// <param name="cancellationToken">The caller's token, which the deadline is linked to.</param>
    private void StartDeadline(bool empty, CancellationToken cancellationToken)
    {
        if (empty || _deadline is not null || _limits.ReadHeaderTimeout <= TimeSpan.Zero)
        {
            return;
        }

        CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_limits.ReadHeaderTimeout);
        _deadline = deadline;
    }

    /// <summary>True when the wait ended because this reader's own deadline fired.</summary>
    /// <param name="cancellationToken">The caller's token, which takes precedence.</param>
    /// <returns>True to report a timeout rather than to propagate the cancellation.</returns>
    private bool Expired(CancellationToken cancellationToken) =>
        _deadline is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested;

    private static uint PeekSize(in ReadOnlySequence<byte> buffer)
    {
        Span<byte> header = stackalloc byte[SizeFieldLength];
        SequenceReader<byte> reader = new(buffer);
        reader.TryCopyTo(header);
        return BinaryPrimitives.ReadUInt32LittleEndian(header);
    }

    private FrameLease Take(in ReadOnlySequence<byte> buffer, int size)
    {
        ReadOnlySequence<byte> frame = buffer.Slice(0, size);

        // A frame that already lies in one piece is decoded where it is; the pipe is advanced past
        // it when the lease is disposed, which is exactly one AdvanceTo for this read (§6.1).
        if (frame.IsSingleSegment)
        {
            return FrameLease.InPlace(_reader, frame.First, frame.End);
        }

        FrameLease lease = FrameLease.Copied(_pool, frame);
        _reader.AdvanceTo(frame.End);
        return lease;
    }
}
