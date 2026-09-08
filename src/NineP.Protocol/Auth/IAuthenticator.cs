using NineP.Protocol.Transports;

namespace NineP.Protocol.Auth;

/// <summary>
/// Server-side authentication (workspace architecture §5). Returning null from
/// <see cref="BeginAsync"/> refuses the <c>Tauth</c> outright.
/// </summary>
public interface IAuthenticator
{
    /// <summary>True when an attach must present a verified afid; false lets NOFID attaches through.</summary>
    bool IsRequired { get; }

    /// <summary>Starts an afid exchange, or refuses the <c>Tauth</c>.</summary>
    /// <param name="request">The triple the afid will be bound to.</param>
    /// <param name="peer">What the transport learned about the peer, when it learned anything.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The session, or null to refuse (S-23 chooses the refusal's shape).</returns>
    ValueTask<IAuthSession?> BeginAsync(
        AuthRequest request, PeerIdentity? peer, CancellationToken cancellationToken = default);
}
