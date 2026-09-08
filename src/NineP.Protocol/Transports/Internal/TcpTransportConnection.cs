using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NineP.Protocol.Internal;

namespace NineP.Protocol.Transports.Internal;

/// <summary>One accepted or dialled TCP connection.</summary>
internal sealed class TcpTransportConnection : INinePConnection
{
    private readonly Socket _socket;
    private readonly Action? _release;
    private readonly TimeSpan _readHeaderTimeout = Limits.Default.ReadHeaderTimeout;
    private int _closed;
    private bool _sawFirstByte;

    public TcpTransportConnection(Socket socket, NinePAddress remote, Action? release)
    {
        _socket = socket;
        _release = release;
        RemoteAddress = remote;
        PeerIdentity = new PeerIdentity { RemoteAddress = socket.RemoteEndPoint?.ToString() };
    }

    /// <summary>The address of the peer.</summary>
    public NinePAddress RemoteAddress { get; }

    /// <summary>What TCP alone knows about the peer: its network address.</summary>
    public PeerIdentity? PeerIdentity { get; }

    /// <summary>The accepted socket, so a test can assert on the socket rather than on the option.</summary>
    public Socket Socket => _socket;

    /// <summary>Why this end closed, once it has.</summary>
    public CloseReason? LocalCloseReason { get; private set; }

    /// <summary>
    /// Reads up to <c>buffer.Length</c> bytes. The first read of a connection is bounded by
    /// <see cref="Limits.ReadHeaderTimeout"/>: a peer that connects and then says nothing is
    /// dropped rather than left holding a slot against the connection cap.
    /// </summary>
    /// <param name="buffer">Where the bytes go.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes read; zero at the end of the stream.</returns>
    /// <exception cref="TimeoutException">The first byte did not arrive in time.</exception>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_sawFirstByte)
        {
            return await ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        using CancellationTokenSource budget =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_readHeaderTimeout);

        int read;
        try
        {
            read = await ReceiveAsync(buffer, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await CloseAsync(CloseReason.Timeout, CancellationToken.None).ConfigureAwait(false);
            throw new TimeoutException(string.Format(
                CultureInfo.InvariantCulture,
                "no header arrived from {0} within {1}", RemoteAddress, _readHeaderTimeout));
        }

        _sawFirstByte = true;
        return read;
    }

    /// <summary>Writes exactly one complete 9P message.</summary>
    /// <param name="message">The frame, <c>size[4]</c> included.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when every byte has been handed to the socket.</returns>
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        int sent = 0;
        while (sent < message.Length)
        {
            sent += await _socket.SendAsync(message[sent..], SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes the connection. TCP cannot carry a reason to the peer, so the reason is recorded for
    /// this side's log and the socket is shut down cleanly in both directions.
    /// </summary>
    /// <param name="reason">Why the connection is closing.</param>
    /// <param name="cancellationToken">Unused; a shutdown does not block.</param>
    /// <returns>A completed task.</returns>
    public ValueTask CloseAsync(CloseReason reason, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            LocalCloseReason = reason;
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // The peer is already gone; there is nothing left to shut down and nothing to log.
            }
            catch (ObjectDisposedException)
            {
                // Disposed underneath us by a concurrent close; the socket is closed either way.
            }

            _socket.Dispose();
            _release?.Invoke();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the connection normally if it is still open.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync() => CloseAsync(CloseReason.Normal);

    private async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException failure) when (failure.SocketErrorCode == SocketError.ConnectionReset)
        {
            return 0;
        }

        // A read that was in flight when this side closed the socket is reported by the platform
        // as an aborted operation rather than as an orderly end of stream — SocketException on
        // Unix, ObjectDisposedException on Windows. Both mean the same thing to a caller that is
        // shutting the connection down, and a reader loop must not have to know which platform it
        // is on to recognise its own close.
        catch (Exception failure)
            when (failure is SocketException or ObjectDisposedException
                && Volatile.Read(ref _closed) == 1)
        {
            return 0;
        }
    }
}
