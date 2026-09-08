using System.Buffers;
using NineP.Protocol;
using NineP.Protocol.Messages;

namespace NineP.Client.Internal;

/// <summary>
/// Whole-file transfers, chunked at <c>min(iounit, msize - IOHDRSZ)</c> with several requests
/// outstanding (architecture §6). The window is where the throughput comes from: one request per
/// round trip would cost a round trip per chunk, and a 9P chunk is small.
/// </summary>
internal static class ChunkedTransfer
{
    /// <summary>Reads a whole file into memory.</summary>
    /// <param name="fid">The open fid to read.</param>
    /// <param name="window">How many reads to keep outstanding.</param>
    /// <param name="maximum">Maximum bytes to accumulate.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>Everything the file held from offset 0 to end of file.</returns>
    /// <exception cref="NinePProtocolException">A reply carried more bytes than were asked for.</exception>
    public static async ValueTask<byte[]> ReadAllAsync(
        NinePFid fid, int window, int maximum, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        cancellationToken.ThrowIfCancellationRequested();
        int chunk = fid.Iounit;
        ArrayBufferWriter<byte> sink = new();
        ulong offset = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // At the cap, one byte distinguishes EOF from a value that exceeds it. Outstanding
            // requests together never ask for more than the remaining allowance plus this probe.
            long remaining = (long)maximum - sink.WrittenCount + 1;
            int count = (int)Math.Min(chunk, remaining);
            int requests = (int)Math.Min(window, (remaining + count - 1) / count);
            Rread[] replies = await Task
                .WhenAll(Batch(fid, requests, offset, count, remaining, cancellationToken))
                .ConfigureAwait(false);

            (bool endOfFile, ulong next) = Absorb(sink, replies, offset, count, maximum);
            if (endOfFile)
            {
                return sink.WrittenSpan.ToArray();
            }

            offset = next;
        }
    }

    /// <summary>Writes a whole buffer to a file.</summary>
    /// <param name="fid">The open fid to write.</param>
    /// <param name="data">The bytes to write, starting at offset 0.</param>
    /// <param name="window">How many writes to keep outstanding.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>A task that completes when every byte has been acknowledged.</returns>
    /// <exception cref="NinePProtocolException">A reply claimed more bytes than were sent.</exception>
    public static async ValueTask WriteAllAsync(
        NinePFid fid, ReadOnlyMemory<byte> data, int window, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        cancellationToken.ThrowIfCancellationRequested();
        int chunk = fid.Iounit;
        int done = 0;

        while (done < data.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<Task<Rwrite>> batch = [];
            List<int> lengths = [];
            for (int i = 0; i < window && (long)done + ((long)i * chunk) < data.Length; i++)
            {
                int at = (int)((long)done + ((long)i * chunk));
                int length = Math.Min(chunk, data.Length - at);
                lengths.Add(length);
                batch.Add(fid.Session.Messages
                    .WriteAsync(new Twrite(0, fid.Fid, (ulong)at, data.Slice(at, length)), cancellationToken)
                    .AsTask());
            }

            Rwrite[] replies = await Task.WhenAll(batch).ConfigureAwait(false);
            done = Advance(replies, lengths, done, chunk);
        }
    }

    private static IEnumerable<Task<Rread>> Batch(
        NinePFid fid, int window, ulong offset, int chunk, long remaining, CancellationToken cancellationToken)
    {
        for (int i = 0; i < window; i++)
        {
            yield return fid.Session.Messages
                .ReadAsync(new Tread(0, fid.Fid, offset + (ulong)((long)i * chunk), (uint)Math.Min(chunk, remaining - ((long)i * chunk))), cancellationToken)
                .AsTask();
        }
    }

    private static (bool EndOfFile, ulong Next) Absorb(
        ArrayBufferWriter<byte> sink, Rread[] replies, ulong offset, int chunk, int maximum)
    {
        for (int i = 0; i < replies.Length; i++)
        {
            Rread reply = replies[i];
            long requested = Math.Min(chunk, (long)maximum - sink.WrittenCount + 1 - ((long)i * chunk));
            // Reference §8 rule 13: a reply may carry fewer bytes than were asked for, never more.
            if (reply.Data.Length > requested)
            {
                throw new NinePProtocolException(
                    ProtocolErrorKind.Bounds, "the server answered a Tread with more bytes than asked for");
            }
        }

        for (int i = 0; i < replies.Length; i++)
        {
            ReadOnlyMemory<byte> data = replies[i].Data;
            if (data.IsEmpty)
            {
                return (true, offset);
            }

            if (data.Length > maximum - sink.WrittenCount)
            {
                throw new NinePException(NinePError.FromErrno(Errno.EFBIG));
            }
            sink.Write(data.Span);

            // A short read is not end of file; it only means the next request starts here, and
            // whatever the rest of this batch read lies past a hole we have not filled.
            if (data.Length < chunk)
            {
                return (false, offset + (ulong)((long)i * chunk) + (ulong)data.Length);
            }
        }

        return (false, offset + (ulong)((long)replies.Length * chunk));
    }

    private static int Advance(Rwrite[] replies, List<int> lengths, int done, int chunk)
    {
        for (int i = 0; i < replies.Length; i++)
        {
            if (replies[i].Count > (uint)lengths[i])
            {
                throw new NinePProtocolException(
                    ProtocolErrorKind.Bounds, "the server claimed to have written more bytes than were sent");
            }

            // A server that accepts nothing at all would leave the transfer spinning on the same
            // offset forever, so no progress is a refusal rather than a short write.
            if (replies[i].Count == 0 && lengths[i] > 0)
            {
                throw new NinePException(NinePError.FromErrno(Errno.EIO));
            }
        }

        for (int i = 0; i < replies.Length; i++)
        {
            // A short write is not an error (read(5)); the transfer simply resumes where the
            // server stopped, and the requests already in flight past the gap are rewritten.
            if (replies[i].Count < (uint)lengths[i])
            {
                return done + (i * chunk) + (int)replies[i].Count;
            }
        }

        return done + (int)replies.Sum(reply => (long)reply.Count);
    }
}
