using NineP.Protocol;
using NineP.Protocol.Transports;

namespace NineP.Server.Internal;

/// <summary>
/// One bound listener and everything scoped to it (architecture §4): its accept loop, the
/// connection cap, and the listener-wide in-flight budget that keeps 1024 connections from
/// admitting 262 144 concurrent handler calls between them (reference §8 rule 8).
/// </summary>
internal sealed class ListenerContext : IAsyncDisposable
{
    private readonly INinePListener _listener;
    private readonly ServerOptions _options;
    private readonly ServerMetrics _metrics;
    private readonly OpenState _openState;
    private readonly SemaphoreSlim _connections;
    private readonly HashSet<Task> _sessions = [];
    private readonly HashSet<ServerSession> _live = [];
    private bool _connectionsRetired;

    /// <summary>Creates a context over a bound listener.</summary>
    /// <param name="listener">The listener; the context owns it.</param>
    /// <param name="options">The server configuration.</param>
    /// <param name="metrics">The counters this listener contributes to.</param>
    /// <param name="openState">The server-wide open state, which owns the DMEXCL registry.</param>
    public ListenerContext(
        INinePListener listener, ServerOptions options, ServerMetrics metrics, OpenState openState)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(openState);

        _listener = listener;
        _options = options;
        _metrics = metrics;
        _openState = openState;
        _connections = new SemaphoreSlim(options.Limits.MaxConnectionsPerListener);
        InFlight = new SemaphoreSlim(options.Limits.MaxInFlightPerListener);
    }

    /// <summary>The address actually bound, carrying the real port when 0 was requested.</summary>
    public NinePAddress LocalAddress => _listener.LocalAddress;

    /// <summary>The requests in flight across every connection of this listener.</summary>
    public SemaphoreSlim InFlight { get; }

    /// <summary>Accepts connections until the token fires or the listener is disposed.</summary>
    /// <param name="filesystem">The tree every session serves.</param>
    /// <param name="cancellationToken">Stops accepting.</param>
    /// <returns>A task that completes when the accept loop has stopped.</returns>
    public async Task AcceptAsync(IFilesystem filesystem, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // The cap is applied before the accept, so a connection over the limit waits in
                // the kernel's backlog rather than being accepted and reset (reference §6.8).
                await _connections.WaitAsync(cancellationToken).ConfigureAwait(false);

                INinePConnection? connection;
                try
                {
                    connection = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    ReleaseConnection();
                    throw;
                }

                if (connection is null)
                {
                    ReleaseConnection();
                    return;
                }

                _metrics.ConnectionAccepted();
                Task serving = Task.Run(
                    () => ServeAsync(connection, filesystem, cancellationToken), CancellationToken.None);

                lock (_sessions)
                {
                    _sessions.Add(serving);
                }

                _ = RemoveCompletedAsync(serving);
            }
        }
        catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException)
        {
            // The server is stopping; the sessions are drained by StopAsync.
        }
    }

    /// <summary>The active session tasks retained by this listener.</summary>
    public int ActiveSessionCount
    {
        get
        {
            lock (_sessions)
            {
                return _sessions.Count;
            }
        }
    }

    private async Task RemoveCompletedAsync(Task serving)
    {
        try
        {
            await serving.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Observe task failures without retaining completed sessions forever.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            _options.Logger.CleanupFailedSafely(failure);
        }
        finally
        {
            lock (_sessions)
            {
                _sessions.Remove(serving);
            }
        }
    }

    /// <summary>Waits for the sessions this listener started, up to a deadline.</summary>
    /// <param name="graceful">How long in-flight work may take to finish.</param>
    /// <returns>True when every session finished inside the deadline.</returns>
    public async ValueTask<bool> DrainAsync(TimeSpan graceful)
    {
        Task[] running;
        lock (_sessions)
        {
            running = [.. _sessions];
        }

        Task all = Task.WhenAll(running);
        Task finished = await Task.WhenAny(all, Task.Delay(graceful)).ConfigureAwait(false);
        return ReferenceEquals(finished, all);
    }

    /// <summary>Closes the listener and every session it started.</summary>
    /// <returns>A task that completes when everything has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync().ConfigureAwait(false);

        ServerSession[] sessions;
        lock (_live)
        {
            sessions = [.. _live];
        }

        foreach (ServerSession session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        Task[] running;
        lock (_sessions)
        {
            running = [.. _sessions];
        }

        await Task.WhenAll(running).ConfigureAwait(false);
        lock (_sessions)
        {
            _connectionsRetired = true;
            _connections.Dispose();
        }

        // Session cleanup can retain an in-flight lease for a handler that ignores cancellation.
        // This managed semaphore is reclaimed with the last such session; it has no wait handle.
    }

    private void ReleaseConnection()
    {
        lock (_sessions)
        {
            if (!_connectionsRetired)
            {
                _connections.Release();
            }
        }
    }

    private async Task ServeAsync(
        INinePConnection connection, IFilesystem filesystem, CancellationToken cancellationToken)
    {
        ServerSession session = new(connection, _options, filesystem, _metrics, _openState, InFlight);

        lock (_live)
        {
            _live.Add(session);
        }

        try
        {
            await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException
            or ObjectDisposedException or NinePException)
        {
            _options.Logger.ConnectionEnded(failure.Message);
        }
        finally
        {
            lock (_live)
            {
                _live.Remove(session);
            }

            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _metrics.ConnectionClosed();
                ReleaseConnection();
            }
        }
    }
}
