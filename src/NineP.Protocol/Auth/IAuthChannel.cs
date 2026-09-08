using NineP.Protocol.Transports;

namespace NineP.Protocol.Auth;

/// <summary>The client's read/write view of an afid during <c>AttachAsync</c>.</summary>
public interface IAuthChannel
{
    /// <summary>Writes credential bytes to the afid.</summary>
    /// <param name="data">The bytes to write.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when the bytes have been written.</returns>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Reads the server's answer from the afid.</summary>
    /// <param name="maxBytes">The most to ask for.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The bytes the server produced; empty at the end of the exchange.</returns>
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(int maxBytes, CancellationToken cancellationToken = default);
}
