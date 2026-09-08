using NineP.Protocol.Transports;

namespace NineP.Protocol.Auth;

/// <summary>
/// One in-progress afid exchange. The core bounds it at 64 KiB and 30 s (workspace architecture
/// §5); what flows over it is this workspace's business, not 9P's.
/// </summary>
public interface IAuthSession : IAsyncDisposable
{
    /// <summary>The identity once the exchange has succeeded; null until then, and an attach presenting
    /// an afid whose session is still null fails EACCES.</summary>
    Identity? Identity { get; }

    /// <summary>Receives bytes the client wrote to the afid.</summary>
    /// <param name="data">The bytes from one <c>Twrite</c>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when the bytes have been consumed.</returns>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Produces bytes for the client to read from the afid.</summary>
    /// <param name="maxBytes">The most the client asked for.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The bytes to send; empty means the end of the exchange.</returns>
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(int maxBytes, CancellationToken cancellationToken = default);
}
