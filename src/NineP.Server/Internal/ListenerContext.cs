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
    private readonly AuthThrottle _authThrottle;
    private readonly SemaphoreSlim _connections;
    private readonly AddressCounter _addresses;
    private readonly HashSet<Task> _sessions = [];
    private readonly HashSet<ServerSession> _live = [];
    private bool _connectionsRetired;

    /// <summary>Creates a context over a bound listener.</summary>
    /// <param name="listener">The listener; the context owns it.</param>
    /// <param name="options">The server configuration.</param>
    /// <param name="metrics">The counters this listener contributes to.</param>
    /// <param name="openState">The server-wide open state, which owns the DMEXCL registry.</param>
    /// <param name="authThrottle">The server-wide per-address auth budget (reference §8 rule 40).</param>
    public ListenerContext(
        INinePListener listener,
        ServerOptions options,
        ServerMetrics metrics,
        OpenState openState,
        AuthThrottle authThrottle)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(openState);
        ArgumentNullException.ThrowIfNull(authThrottle);

        _listener = listener;
        _options = options;
        _metrics = metrics;
        _openState = openState;
        _authThrottle = authThrottle;
        _connections = new SemaphoreSlim(options.Limits.MaxConnectionsPerListener);
        _addresses = new AddressCounter(options.Limits.MaxConnectionsPerAddress);
        InFlight = new SemaphoreSlim(options.Limits.MaxInFlightPerListener);
        Rate = new TokenBucket(
            options.Limits.MaxRequestsPerSecondPerListener,
            options.Limits.MaxInFlightPerListener,
            options.TimeProvider);
    }

    /// <summary>The address actually bound, carrying the real port when 0 was requested.</summary>
    public NinePAddress LocalAddress => _listener.LocalAddress;

    /// <summary>The requests in flight across every connection of this listener.</summary>
    public SemaphoreSlim InFlight { get; }

    /// <summary>The listener-wide request rate budget (reference §8 rule 40).</summary>
    public TokenBucket Rate { get; }

    /// <summary>Live connections held for one address; for tests and diagnostics.</summary>
    /// <param name="address">The peer address to report.</param>
    /// <returns>The number of connections that address holds.</returns>
    public int ConnectionsFor(string address) => _addresses.HeldFor(address);

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

                // The per-address cap can only be applied here: the listener cap is taken before
                // the accept, but an address is not known until a connection exists.
                string? peer = PeerOf(connection);
                if (!_addresses.TryHold(peer, out bool firstRefusal))
                {
                    if (firstRefusal && peer is not null)
                    {
                        _options.Logger.ConnectionsPerAddressExceeded(
                            peer, _options.Limits.MaxConnectionsPerAddress);
                    }

                    _metrics.ConnectionRefused();
                    await CloseRefusedAsync(connection).ConfigureAwait(false);
                    ReleaseConnection();
                    continue;
                }

                _metrics.ConnectionAccepted();
                Task serving = Task.Run(
                    () => ServeAsync(connection, peer, filesystem, cancellationToken), CancellationToken.None);

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

    private static string? PeerOf(INinePConnection connection) =>
        ServerSession.PeerAddressOf(connection);

    private async Task CloseRefusedAsync(INinePConnection connection)
    {
        try
        {
            await connection.CloseAsync(CloseReason.ResourceLimit).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A refused peer must not be able to end the accept loop.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            _options.Logger.CleanupFailedSafely(failure);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(
        INinePConnection connection, string? peer, IFilesystem filesystem, CancellationToken cancellationToken)
    {
        ServerSession session = new(
            connection, _options, filesystem, _metrics, _openState, InFlight, Rate, _authThrottle);

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
                _addresses.Release(peer);
                ReleaseConnection();
            }
        }
    }
}
