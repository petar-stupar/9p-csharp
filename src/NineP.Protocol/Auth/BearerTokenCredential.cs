using System.Security.Cryptography;
using NineP.Protocol.Auth.Internal;
using NineP.Protocol.Internal;

namespace NineP.Protocol.Auth;

/// <summary>
/// Writes an OIDC access token the caller already holds (S-30). The packages never perform a
/// login: acquiring a token is the cli's job, and a library that could log in would need to hold
/// a client secret.
/// </summary>
public sealed class BearerTokenCredential : ICredential
{
    private readonly Func<CancellationToken, ValueTask<string>> _tokenProvider;

    /// <summary>Uses a fixed token.</summary>
    /// <param name="accessToken">The access token.</param>
    /// <exception cref="ArgumentException">The token is empty.</exception>
    public BearerTokenCredential(string accessToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);

        _tokenProvider = _ => ValueTask.FromResult(accessToken);
    }

    /// <summary>Fetches the token per attach, so a caller can refresh it out of band.</summary>
    /// <param name="tokenProvider">Produces the current access token.</param>
    /// <exception cref="ArgumentNullException">The provider is null.</exception>
    public BearerTokenCredential(Func<CancellationToken, ValueTask<string>> tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);

        _tokenProvider = tokenProvider;
    }

    /// <summary>Writes the token and waits for the acknowledgement.</summary>
    /// <param name="channel">The afid, as a read/write pair.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when the server accepted the token.</returns>
    /// <exception cref="NinePException">The server refused it.</exception>
    public async ValueTask AuthenticateAsync(
        IAuthChannel channel, CancellationToken cancellationToken = default)
    {
        string token = await _tokenProvider(cancellationToken).ConfigureAwait(false);
        byte[] payload = NinePText.Utf8.GetBytes(token);

        try
        {
            await CredentialExchange
                .WriteAndExpectOkAsync(channel, payload, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }
}
