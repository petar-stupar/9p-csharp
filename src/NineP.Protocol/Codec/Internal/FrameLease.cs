using System.Buffers;
using System.IO.Pipelines;

namespace NineP.Protocol.Codec.Internal;

/// <summary>
/// Owns one complete frame for exactly as long as the handler call that reads it. A single-segment
/// frame is decoded in place out of the pipe's own memory and the pipe is advanced past it on
/// dispose; a multi-segment frame is copied once into a pooled rental that dispose returns. Either
/// way the payload views a handler holds — <c>Twrite.Data</c>, <c>Rread.Data</c> — stop being
/// valid the moment the lease is disposed, and reading one afterwards is loud rather than subtle.
/// </summary>
internal sealed class FrameLease : IDisposable
{
    /// <summary>
    /// Whether a returned rental is cleared before it goes back to the pool. Debug builds poison
    /// it so that a use-after-return shows up as zeroes rather than as the next frame (RK-65);
    /// release builds do not pay for the clear on the hot path.
    /// </summary>
#if DEBUG
    public const bool PoisonByDefault = true;
#else
    public const bool PoisonByDefault = false;
#endif

    private readonly PipeReader? _reader;
    private readonly SequencePosition _consumed;
    private readonly ArrayPool<byte>? _pool;
    private readonly byte[]? _rented;
    private readonly bool _poison;
    private ReadOnlyMemory<byte> _frame;
    private bool _disposed;

    private FrameLease(
        PipeReader? reader,
        SequencePosition consumed,
        ArrayPool<byte>? pool,
        byte[]? rented,
        ReadOnlyMemory<byte> frame,
        bool poison)
    {
        _reader = reader;
        _consumed = consumed;
        _pool = pool;
        _rented = rented;
        _frame = frame;
        _poison = poison;
    }

    /// <summary>The complete frame, <c>size[4]</c> included.</summary>
    /// <exception cref="ObjectDisposedException">The lease has already been returned.</exception>
    public ReadOnlyMemory<byte> Frame =>
        _disposed ? throw new ObjectDisposedException(nameof(FrameLease)) : _frame;

    /// <summary>True when the frame was copied into a rental rather than read in place.</summary>
    public bool IsPooled => _rented is not null;

    /// <summary>Wraps a frame that lives in the pipe's own memory and is decoded where it lies.</summary>
    /// <param name="reader">The pipe, which is advanced past the frame on dispose.</param>
    /// <param name="frame">The frame's bytes inside the pipe's buffer.</param>
    /// <param name="consumed">The position just past the frame.</param>
    /// <returns>The lease.</returns>
    public static FrameLease InPlace(PipeReader reader, ReadOnlyMemory<byte> frame, SequencePosition consumed) =>
        new(reader, consumed, null, null, frame, false);

    /// <summary>Copies a frame that spans several segments into one pooled rental.</summary>
    /// <param name="pool">The pool the rental comes from and goes back to.</param>
    /// <param name="frame">The frame, however it is segmented.</param>
    /// <param name="poison">Whether to clear the rental before returning it.</param>
    /// <returns>The lease, which owns the rental.</returns>
    public static FrameLease Copied(ArrayPool<byte> pool, in ReadOnlySequence<byte> frame, bool poison = PoisonByDefault)
    {
        ArgumentNullException.ThrowIfNull(pool);

        int length = checked((int)frame.Length);
        byte[] rented = pool.Rent(length);
        frame.CopyTo(rented);
        return new FrameLease(null, default, pool, rented, rented.AsMemory(0, length), poison);
    }

    /// <summary>Wraps bytes the caller owns; nothing is pooled and nothing is advanced.</summary>
    /// <param name="frame">The frame's bytes.</param>
    /// <returns>The lease.</returns>
    public static FrameLease Borrowed(ReadOnlyMemory<byte> frame) => new(null, default, null, null, frame, false);

    /// <summary>Returns the rental, or advances the pipe past the frame that was read in place.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _frame = ReadOnlyMemory<byte>.Empty;

        if (_rented is not null && _pool is not null)
        {
            _pool.Return(_rented, _poison);
        }

        _reader?.AdvanceTo(_consumed);
    }
}
