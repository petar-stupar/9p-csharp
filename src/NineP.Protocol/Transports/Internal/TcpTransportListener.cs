using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NineP.Protocol.Internal;

namespace NineP.Protocol.Transports.Internal;

/// <summary>A TCP listener with a connection cap that stops accepting rather than resetting.</summary>
internal sealed class TcpTransportListener : INinePListener
{
    private readonly Socket _socket;
    private readonly TcpTransportOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _slots;
    private readonly object _lifetimeGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private readonly TaskCompletionSource _acceptsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _accepts;
    private int _disposed;

    public TcpTransportListener(Socket socket, NinePAddress requested, TcpTransportOptions options)
    {
        _socket = socket;
        _stopToken = _stop.Token;
        _options = options;
        _logger = options.Logger ?? NullLogger.Instance;
        _slots = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);

        IPEndPoint bound = (IPEndPoint)socket.LocalEndPoint!;
        LocalAddress = requested with { Port = bound.Port };
    }

    /// <summary>The address actually bound, carrying the real port when 0 was requested.</summary>
    public NinePAddress LocalAddress { get; }

    /// <summary>Connections currently held against the cap.</summary>
    public int HeldConnections => _options.MaxConnections - _slots.CurrentCount;

    /// <summary>
    /// Accepts the next connection, but only once a slot is free: the wait happens <em>before</em>
    /// the accept, so a connection over the cap is left in the kernel's backlog untouched. A
    /// transient accept failure is retried behind a backoff rather than ending the listener; see
    /// <see cref="AcceptPolicy"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The connection, or null once the listener has been disposed.</returns>
    public async ValueTask<INinePConnection?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifetimeGate)
        {
            if (_disposed != 0)
            {
                return null;
            }

            _accepts++;
        }

        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_stopToken, cancellationToken);
            return await AcceptCoreAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (_lifetimeGate)
            {
                _accepts--;
                if (_disposed != 0 && _accepts == 0)
                {
                    _acceptsDrained.TrySetResult();
                }
            }
        }
    }

    private async ValueTask<INinePConnection?> AcceptCoreAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        Socket? accepted;
        try
        {
            accepted = await AcceptPolicy.AcceptAsync(
                token => _socket.AcceptAsync(token),
                _logger,
                LocalAddress,
                () => Volatile.Read(ref _disposed) != 0,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release();
            throw;
        }

        if (accepted is null)
        {
            Release();
            return null;
        }

        lock (_lifetimeGate)
        {
            if (_disposed != 0)
            {
                accepted.Dispose();
                return null;
            }

            TcpTransport.Configure(accepted, _options);
            return new TcpTransportConnection(accepted, TcpTransport.RemoteAddressOf(accepted, NinePScheme.Tcp), Release);
        }
    }

    /// <summary>Stops accepting, cancels cap waiters and drains accepts before releasing their gate.</summary>
    /// <returns>A task that completes after pending accepts have stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        bool ownsClose;
        lock (_lifetimeGate)
        {
            ownsClose = _disposed == 0;
            _disposed = 1;
            if (_accepts == 0)
            {
                _acceptsDrained.TrySetResult();
            }
        }

        if (!ownsClose)
        {
            await _closed.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            _socket.Dispose();
            await _acceptsDrained.Task.ConfigureAwait(false);
            lock (_lifetimeGate)
            {
                _slots.Dispose();
                _stop.Dispose();
            }
        }
        finally
        {
            _closed.TrySetResult();
        }
    }

    private void Release()
    {
        // A handed-off connection can close concurrently with its listener. Serialize the
        // disposed check and semaphore access so a returned slot never touches a disposed gate.
        lock (_lifetimeGate)
        {
            if (_disposed == 0)
            {
                _slots.Release();
            }
        }
    }
}
