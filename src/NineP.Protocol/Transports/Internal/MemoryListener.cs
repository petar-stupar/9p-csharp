using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Threading.Channels;

namespace NineP.Protocol.Transports.Internal;

/// <summary>An in-process listener: a queue of connections a dialer has already built.</summary>
internal sealed class MemoryListener(NinePAddress address, Action unbind) : INinePListener
{
    private readonly Channel<INinePConnection> _pending =
        Channel.CreateUnbounded<INinePConnection>(new UnboundedChannelOptions { SingleReader = true });

    private int _disposed;

    /// <summary>The address this listener is bound to.</summary>
    public NinePAddress LocalAddress => address;

    /// <summary>Accepts the next connection.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The connection, or null once the listener has been disposed.</returns>
    public async ValueTask<INinePConnection?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _pending.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>Hands a freshly dialled connection to whoever is accepting.</summary>
    /// <param name="connection">The server end.</param>
    /// <returns>False when the listener has already been disposed.</returns>
    public bool TryEnqueue(INinePConnection connection) => _pending.Writer.TryWrite(connection);

    /// <summary>Unbinds the name and wakes any pending accept with null.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _pending.Writer.TryComplete();
            unbind();
        }

        return ValueTask.CompletedTask;
    }
}
