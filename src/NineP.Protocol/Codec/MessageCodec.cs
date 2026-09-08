using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Messages;

namespace NineP.Protocol.Codec;

/// <summary>
/// Encodes and decodes single 9P frames. This is the whole public surface of the codec: the
/// streaming frame reader and writer, the pooled leases and the per-primitive readers are
/// internal, because no public member of this library exposes a pipe, a sequence or a pool.
/// </summary>
public static class MessageCodec
{
    /// <summary>Decodes one complete frame into its record.</summary>
    /// <typeparam name="TMessage">The record the caller expects; its type number must match.</typeparam>
    /// <param name="frame">One complete frame, <c>size[4]</c> included.</param>
    /// <param name="dialect">The dialect the session negotiated.</param>
    /// <returns>The decoded record.</returns>
    /// <exception cref="NinePProtocolException">The frame is malformed or illegal.</exception>
    public static TMessage Decode<TMessage>(ReadOnlyMemory<byte> frame, Dialect dialect)
        where TMessage : struct, IMessage =>
        TryDecode(frame, dialect, out TMessage message, out ProtocolErrorKind failure)
            ? message
            : throw new NinePProtocolException(
                failure,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "malformed {0}: {1}",
                    MessageTypes.GetName(TMessage.Type),
                    failure));

    /// <summary>Decodes one complete frame, reporting the failure kind instead of throwing.</summary>
    /// <typeparam name="TMessage">The record the caller expects; its type number must match.</typeparam>
    /// <param name="frame">One complete frame, <c>size[4]</c> included.</param>
    /// <param name="dialect">The dialect the session negotiated.</param>
    /// <param name="message">The decoded record, or the default on failure.</param>
    /// <param name="failure">What was wrong when the result is false.</param>
    /// <returns>True when the frame decoded cleanly.</returns>
    public static bool TryDecode<TMessage>(
        ReadOnlyMemory<byte> frame, Dialect dialect, out TMessage message, out ProtocolErrorKind failure)
        where TMessage : struct, IMessage =>
        MessageDecoder.TryDecode(frame, dialect, out message, out failure);

    /// <summary>Encodes one message, <c>size[4]</c> included, into a buffer writer.</summary>
    /// <typeparam name="TMessage">The record being written.</typeparam>
    /// <param name="writer">The destination; exactly one frame is written and advanced over.</param>
    /// <param name="message">The message.</param>
    /// <param name="dialect">The dialect the session negotiated.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentNullException">The writer is null.</exception>
    /// <exception cref="ArgumentException">The message cannot be encoded in this dialect.</exception>
    public static int Encode<TMessage>(IBufferWriter<byte> writer, in TMessage message, Dialect dialect)
        where TMessage : struct, IMessage
    {
        ArgumentNullException.ThrowIfNull(writer);

        int size = MessageWriter.GetEncodedSize(in message, dialect);
        int written = MessageWriter.Write(writer.GetSpan(size)[..size], in message, dialect);
        writer.Advance(written);
        return written;
    }

    /// <summary>The exact encoded length of a message, so a caller can size a buffer for it.</summary>
    /// <typeparam name="TMessage">The record being measured.</typeparam>
    /// <param name="message">The message.</param>
    /// <param name="dialect">The dialect the session negotiated.</param>
    /// <returns>The frame length, <c>size[4]</c> included.</returns>
    /// <exception cref="ArgumentException">The message cannot be encoded in this dialect.</exception>
    public static int GetEncodedSize<TMessage>(in TMessage message, Dialect dialect)
        where TMessage : struct, IMessage =>
        MessageWriter.GetEncodedSize(in message, dialect);

    /// <summary>Reads <c>size[4]</c> from a frame without decoding it.</summary>
    /// <param name="frame">At least the first four bytes of a frame.</param>
    /// <returns>The size the frame claims, which includes those four bytes.</returns>
    /// <exception cref="ArgumentException">Fewer than four bytes were supplied.</exception>
    public static uint PeekSize(ReadOnlySpan<byte> frame) =>
        frame.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(frame)
            : throw new ArgumentException("a frame's size needs four bytes", nameof(frame));

    /// <summary>Reads <c>type[1]</c> from a frame without decoding it, for the legality check.</summary>
    /// <param name="frame">At least the header of a frame.</param>
    /// <returns>The type number, which may be one the protocol does not define.</returns>
    /// <exception cref="ArgumentException">The header is not there.</exception>
    public static MessageType PeekType(ReadOnlySpan<byte> frame) =>
        frame.Length >= Constants.HDRSZ
            ? (MessageType)frame[4]
            : throw new ArgumentException("a frame's header is seven bytes", nameof(frame));

    /// <summary>Reads <c>tag[2]</c> from a frame without decoding it, for the flush reserve.</summary>
    /// <param name="frame">At least the header of a frame.</param>
    /// <returns>The tag the frame carries.</returns>
    /// <exception cref="ArgumentException">The header is not there.</exception>
    public static ushort PeekTag(ReadOnlySpan<byte> frame) =>
        frame.Length >= Constants.HDRSZ
            ? BinaryPrimitives.ReadUInt16LittleEndian(frame[5..])
            : throw new ArgumentException("a frame's header is seven bytes", nameof(frame));
}
