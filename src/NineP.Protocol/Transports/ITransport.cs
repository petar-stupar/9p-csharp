namespace NineP.Protocol.Transports;

/// <summary>
/// The user-defined-transport seam (workspace architecture §3): dial and listen. Implementing this
/// interface is the extension point, and <see cref="MemoryTransport"/> is the proof that the seam
/// is real — every test that can run over it does.
/// </summary>
public interface ITransport
{
    /// <summary>The schemes this transport can dial and bind.</summary>
    IReadOnlyCollection<NinePScheme> Schemes { get; }

    /// <summary>Dials an address.</summary>
    /// <param name="address">Where to connect.</param>
    /// <param name="cancellationToken">Cancels the dial.</param>
    /// <returns>The connected connection; the caller disposes it.</returns>
    ValueTask<INinePConnection> ConnectAsync(NinePAddress address, CancellationToken cancellationToken = default);

    /// <summary>Binds an address.</summary>
    /// <param name="address">Where to listen.</param>
    /// <param name="cancellationToken">Cancels the bind.</param>
    /// <returns>The listener; the caller disposes it.</returns>
    ValueTask<INinePListener> ListenAsync(NinePAddress address, CancellationToken cancellationToken = default);
}
