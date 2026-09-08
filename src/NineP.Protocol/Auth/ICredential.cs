using NineP.Protocol.Transports;

namespace NineP.Protocol.Auth;

/// <summary>
/// Client-side credential: it drives the afid exchange from inside <c>AttachAsync</c>, so a caller
/// never writes protocol code to authenticate (workspace architecture §5).
/// </summary>
public interface ICredential
{
    /// <summary>Runs the client half of the exchange over the afid.</summary>
    /// <param name="channel">The afid, as a read/write pair.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when the exchange has succeeded.</returns>
    /// <exception cref="NinePException">The server refused the credential.</exception>
    ValueTask AuthenticateAsync(IAuthChannel channel, CancellationToken cancellationToken = default);
}
