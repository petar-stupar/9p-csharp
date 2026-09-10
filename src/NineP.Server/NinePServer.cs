using System.Globalization;
using NineP.Protocol;
using NineP.Protocol.Transports;
using NineP.Server.Internal;

namespace NineP.Server;

/// <summary>
/// A 9P server: listeners, dialects, authenticator, limits, logger and clock (architecture §4).
/// The developer supplies an <see cref="IFilesystem"/> of typed handlers; everything in reference
/// §5 and §8 — negotiation, fids, tags, flush, packing, permissions — belongs to this package.
/// </summary>
public sealed class NinePServer : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly ServerMetrics _metrics = new();
    private readonly OpenState _openState = new();
    private readonly AuthThrottle _authThrottle;
    private readonly List<ListenerContext> _listeners = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _listening =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _disposed;

    /// <summary>Creates a server from a validated configuration.</summary>
    /// <param name="options">What to listen on and how to behave.</param>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <exception cref="ArgumentException">No address was configured.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The limits contradict one another.</exception>
    public NinePServer(ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Listen.Count == 0)
        {
            throw new ArgumentException("a server needs at least one address to listen on", nameof(options));
        }

        if (options.Dialects.Count == 0)
        {
            throw new ArgumentException("a server that speaks no dialect can serve nobody", nameof(options));
        }

        options.Limits.Validate();
        _options = options;

        // Server-wide, not per listener: a budget a peer resets by moving from tls:// to tcp://
        // would be no budget.
        _authThrottle = new AuthThrottle(
            options.Limits.MaxAuthFailuresPerAddress,
            options.Limits.AuthFailureWindow,
            options.TimeProvider);
    }

    /// <summary>Addresses actually bound, with real ports; valid once serving has started.</summary>
    public IReadOnlyList<NinePAddress> Endpoints
    {
        get
        {
            lock (_listeners)
            {
                return [.. _listeners.Select(listener => listener.LocalAddress)];
            }
        }
    }

    /// <summary>Counters for messages, errors, bytes and connections (architecture §4).</summary>
    public ServerCounters Counters => _metrics.Snapshot();

    /// <summary>Completes once every configured address is bound, so a test need not poll.</summary>
    internal Task Listening => _listening.Task;

    /// <summary>
    /// Waits until every configured address is bound, after which <see cref="Endpoints"/> carries
    /// the real ports. <see cref="ServeAsync"/> runs until the server stops, so a caller that wants
    /// the bound address starts it, awaits this, and reads <see cref="Endpoints"/>.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting; the server is unaffected.</param>
    /// <returns>A task that completes when every listener is bound.</returns>
    /// <exception cref="ArgumentException">A configured address could not be bound.</exception>
    public async ValueTask ListeningAsync(CancellationToken cancellationToken = default) =>
        await _listening.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Binds every configured listener and serves until the token fires or StopAsync.</summary>
    /// <param name="filesystem">The tree to serve.</param>
    /// <param name="cancellationToken">Stops the server.</param>
    /// <returns>A task that completes when every accept loop has stopped.</returns>
    /// <exception cref="ArgumentNullException">The filesystem is null.</exception>
    /// <exception cref="ArgumentException">No transport owns a configured address's scheme.</exception>
    public async Task ServeAsync(IFilesystem filesystem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filesystem);

        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);

        try
        {
            await BindAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            _listening.TrySetException(failure);
            throw;
        }

        _listening.TrySetResult();

        ListenerContext[] contexts;
        lock (_listeners)
        {
            contexts = [.. _listeners];
        }

        await Task.WhenAll(contexts.Select(context => context.AcceptAsync(filesystem, linked.Token)))
            .ConfigureAwait(false);
    }

    /// <summary>Stops accepting, lets in-flight requests finish within the deadline, then closes.</summary>
    /// <param name="graceful">How long in-flight requests may take to finish.</param>
    /// <param name="cancellationToken">Cuts the wait short.</param>
    /// <returns>A task that completes when everything has closed.</returns>
    public async ValueTask StopAsync(TimeSpan graceful, CancellationToken cancellationToken = default)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        ListenerContext[] contexts;
        lock (_listeners)
        {
            contexts = [.. _listeners];
        }

        foreach (ListenerContext context in contexts)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // A session that has not finished inside the deadline is closed under it; the
            // deadline is what makes shutdown bounded rather than hopeful.
            if (!await context.DrainAsync(graceful).ConfigureAwait(false))
            {
                _options.Logger.ListenerDidNotDrain(context.LocalAddress, graceful);
            }
        }

        await DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Stops immediately and releases every listener and connection.</summary>
    /// <returns>A task that completes when everything has closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);

        ListenerContext[] contexts;
        lock (_listeners)
        {
            contexts = [.. _listeners];
            _listeners.Clear();
        }

        foreach (ListenerContext context in contexts)
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }

        _listening.TrySetCanceled();
        _stopping.Dispose();
    }

    private async ValueTask BindAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ITransport> transports = _options.Transports.Count > 0
            ? _options.Transports
            : [new TcpTransport(), new WebSocketTransport()];

        foreach (NinePAddress address in _options.Listen)
        {
            ITransport transport = transports.FirstOrDefault(candidate => candidate.Schemes.Contains(address.Scheme))
                ?? throw new ArgumentException(
                    string.Format(CultureInfo.InvariantCulture, "no configured transport binds {0}", address),
                    nameof(cancellationToken));

            INinePListener listener = await transport.ListenAsync(address, cancellationToken).ConfigureAwait(false);
            ListenerContext context = new(listener, _options, _metrics, _openState, _authThrottle);

            lock (_listeners)
            {
                _listeners.Add(context);
            }

            _options.Logger.Listening(context.LocalAddress);
        }
    }
}
