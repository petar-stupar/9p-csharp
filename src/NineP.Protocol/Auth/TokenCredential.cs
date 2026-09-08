using System.Security.Cryptography;
using NineP.Protocol.Auth.Internal;
using NineP.Protocol.Internal;

namespace NineP.Protocol.Auth;

/// <summary>Writes an opaque token to the afid and expects <c>ok\n</c> (mirror of <see cref="TokenAuthenticator"/>).</summary>
public sealed class TokenCredential : ICredential
{
    private readonly ReadOnlyMemory<byte> _token;

    /// <summary>Creates a credential over a token the caller holds.</summary>
    /// <param name="token">The token bytes.</param>
    /// <exception cref="ArgumentException">The token is empty.</exception>
    public TokenCredential(ReadOnlyMemory<byte> token)
    {
        if (token.IsEmpty)
        {
            throw new ArgumentException("an empty token authenticates nobody", nameof(token));
        }

        _token = token;
    }

    /// <summary>Writes the token and waits for the acknowledgement.</summary>
    /// <param name="channel">The afid, as a read/write pair.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when the server accepted the token.</returns>
    /// <exception cref="NinePException">The server refused it.</exception>
    public ValueTask AuthenticateAsync(IAuthChannel channel, CancellationToken cancellationToken = default) =>
        CredentialExchange.WriteAndExpectOkAsync(channel, _token, cancellationToken);
}
