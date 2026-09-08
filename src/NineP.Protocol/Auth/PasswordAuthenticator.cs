using System.Globalization;
using System.Security.Cryptography;
using NineP.Protocol.Auth.Internal;
using NineP.Protocol.Internal;
using NineP.Protocol.Transports;

namespace NineP.Protocol.Auth;

/// <summary>
/// Default authentication: <c>user\npassword\n</c> over the afid, verified against a hashed store.
/// </summary>
public sealed class PasswordAuthenticator : IAuthenticator
{
    private readonly IPasswordStore _store;

    /// <summary>Creates an authenticator over a credential store.</summary>
    /// <param name="store">Where the hashed credentials live.</param>
    /// <exception cref="ArgumentNullException">The store is null.</exception>
    public PasswordAuthenticator(IPasswordStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <summary>An attach must present a verified afid.</summary>
    public bool IsRequired => true;

    /// <summary>Starts an exchange for any request; the store decides who succeeds.</summary>
    /// <param name="request">The triple the afid will be bound to.</param>
    /// <param name="peer">Unused: a password proves the client, not the transport.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The session.</returns>
    public ValueTask<IAuthSession?> BeginAsync(
        AuthRequest request, PeerIdentity? peer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // CA2000: the session is the core's to dispose once the afid is clunked.
#pragma warning disable CA2000
        return ValueTask.FromResult<IAuthSession?>(new PasswordAuthSession(_store));
#pragma warning restore CA2000
    }
}
