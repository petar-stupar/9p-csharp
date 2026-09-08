using System.Globalization;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Messages;

namespace NineP.Client.Internal;

/// <summary>
/// The client's read/write view of an afid (reference §5.2): <c>Twrite</c> carries the credential
/// out and <c>Tread</c> brings the server's answer back. What flows over it is not 9P's business,
/// so this type knows nothing about the exchange — only how to move bytes and how to stop when the
/// configured budget of <see cref="Limits.MaxAuthBytes"/> is spent.
/// </summary>
internal sealed class AuthChannel(NinePSession session, uint afid, Limits limits) : IAuthChannel
{
    private ulong _writeOffset;
    private ulong _readOffset;
    private int _spent;

    /// <summary>Writes credential bytes to the afid, chunked at the session's payload maximum.</summary>
    /// <param name="data">The bytes to write.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when every byte has been acknowledged.</returns>
    /// <exception cref="NinePException">The budget is spent, or the server short-wrote forever.</exception>
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Spend(data.Length);

        while (!data.IsEmpty)
        {
            int chunk = Math.Min(data.Length, session.MaxPayload);
            Rwrite reply = await session.Messages
                .WriteAsync(new Twrite(0, afid, _writeOffset, data[..chunk]), cancellationToken)
                .ConfigureAwait(false);

            // A server that accepts nothing at all would spin here forever; that is a refusal.
            if (reply.Count == 0 || reply.Count > (uint)chunk)
            {
                throw new NinePException(NinePError.FromErrno(Errno.EACCES));
            }

            _writeOffset += reply.Count;
            data = data[(int)reply.Count..];
        }
    }

    /// <summary>Reads the server's answer from the afid.</summary>
    /// <param name="maxBytes">The most to ask for.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The bytes the server produced; empty at the end of the exchange.</returns>
    /// <exception cref="NinePException">The budget is spent.</exception>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(
        int maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        Spend(maxBytes);

        uint count = (uint)Math.Min(maxBytes, session.MaxPayload);
        Rread reply = await session.Messages
            .ReadAsync(new Tread(0, afid, _readOffset, count), cancellationToken)
            .ConfigureAwait(false);

        if (reply.Data.Length > count)
        {
            throw new NinePProtocolException(
                ProtocolErrorKind.Bounds, "the server answered the afid with more bytes than asked for");
        }

        _readOffset += (ulong)reply.Data.Length;
        return reply.Data.ToArray();
    }

    private void Spend(int bytes)
    {
        int spent = Interlocked.Add(ref _spent, bytes);
        if (spent > limits.MaxAuthBytes)
        {
            throw new NinePException(string.Format(
                CultureInfo.InvariantCulture,
                "the authentication exchange exceeded its {0}-byte budget",
                limits.MaxAuthBytes));
        }
    }
}
