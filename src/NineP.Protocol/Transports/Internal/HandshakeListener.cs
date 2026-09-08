using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Channels;

namespace NineP.Protocol.Transports.Internal;

/// <summary>
/// Accepts raw connections independently of their handshakes. The underlying TCP listener counts
/// pending, queued and handed-off connections against one cap; the ready queue and worker history
/// are bounded by that same cap. A caller cancelling one accept does not cancel another handshake.
/// </summary>
internal sealed class HandshakeListener : INinePListener
{
    private readonly INinePListener _inner;
    private readonly Func<INinePConnection, CancellationToken, ValueTask<INinePConnection?>> _upgrade;
    private readonly Channel<INinePConnection> _ready;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _token;
    private readonly List<Task> _workers = [];
    private readonly object _sync = new();
    private Task? _pump;
    private int _disposed;

    /// <summary>Creates a listener over a TCP listener which enforces the connection cap.</summary>
    public HandshakeListener(INinePListener inner, NinePAddress address,
        Func<INinePConnection, CancellationToken, ValueTask<INinePConnection?>> upgrade, int capacity)
    {
        _inner = inner;
        _upgrade = upgrade;
        LocalAddress = address;
        _token = _stop.Token;
        _ready = Channel.CreateBounded<INinePConnection>(capacity);
    }

    /// <summary>The bound endpoint.</summary>
    public NinePAddress LocalAddress { get; }

    /// <summary>Returns the next completed handshake, without waiting for earlier slow peers.</summary>
    public async ValueTask<INinePConnection?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return null;
            }

            _pump ??= Task.Run(RunAsync, CancellationToken.None);
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
        try
        {
            return await _ready.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ChannelClosedException failure) when (failure.InnerException is null)
        {
            return null;
        }
    }

    /// <summary>Cancels pending handshakes and releases connections still owned by the listener.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? pump;
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            pump = _pump;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _inner.DisposeAsync().ConfigureAwait(false);
        if (pump is not null)
        {
            await pump.ConfigureAwait(false);
        }

        while (_ready.Reader.TryRead(out INinePConnection? connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        Exception? error = null;
        try
        {
            while (!_token.IsCancellationRequested)
            {
                INinePConnection? raw = await _inner.AcceptAsync(_token).ConfigureAwait(false);
                if (raw is null)
                {
                    break;
                }

                // Only this producer touches the list. Completion before insertion is harmless;
                // a completed task survives at most until the next accepted connection.
                _workers.RemoveAll(static task => task.IsCompleted);
                _workers.Add(UpgradeAsync(raw));
            }
        }
        catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException)
        {
            // Listener shutdown cancels both accepting and handshaking.
        }
        // The original failure is propagated through the ready channel to AcceptAsync.
#pragma warning disable CA1031
        catch (Exception failure)
        {
            error = failure;
            await _stop.CancelAsync().ConfigureAwait(false);
        }
#pragma warning restore CA1031
        finally
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
            _ready.Writer.TryComplete(error);
        }
    }

    private async Task UpgradeAsync(INinePConnection raw)
    {
        INinePConnection? ready = null;
        bool handedOff = false;
        try
        {
            ready = await _upgrade(raw, _token).ConfigureAwait(false);
            if (ready is not null)
            {
                await _ready.Writer.WriteAsync(ready, _token).ConfigureAwait(false);
                handedOff = true;
            }
        }
        catch (Exception failure) when (failure is AuthenticationException or IOException or SocketException
            or OperationCanceledException or ObjectDisposedException)
        {
            // A failed peer does not end the listener; its resources are returned below.
        }
        // The original failure is propagated through the ready channel to AcceptAsync.
#pragma warning disable CA1031
        catch (Exception failure)
        {
            _ready.Writer.TryComplete(failure);
            await _stop.CancelAsync().ConfigureAwait(false);
        }
#pragma warning restore CA1031
        finally
        {
            if (!handedOff)
            {
                if (ready is not null)
                {
                    await ready.DisposeAsync().ConfigureAwait(false);
                }

                await raw.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
