namespace NineP.Protocol.Transports;

/// <summary>A listening endpoint that accepts 9P connections.</summary>
public interface INinePListener : IAsyncDisposable
{
    /// <summary>The address actually bound, carrying the real port when 0 was requested.</summary>
    NinePAddress LocalAddress { get; }

    /// <summary>Accepts the next connection.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The connection, or null once the listener has been disposed.</returns>
    ValueTask<INinePConnection?> AcceptAsync(CancellationToken cancellationToken = default);
}
