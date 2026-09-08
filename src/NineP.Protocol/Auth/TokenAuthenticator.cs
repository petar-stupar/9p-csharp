using System.Security.Cryptography;
using NineP.Protocol.Auth.Internal;
using NineP.Protocol.Transports;

namespace NineP.Protocol.Auth;

/// <summary>
/// Default authentication: the client writes an opaque token to the afid once, the server compares
/// it in constant time and answers <c>ok\n</c> (workspace architecture §5). Plan 9's
/// <c>p9any</c>/<c>p9sk1</c> is deliberately out of scope: it is DES-based and is not production
/// security.
/// </summary>
public sealed class TokenAuthenticator : IAuthenticator
{
    /// <summary>What the server writes back once the token has been accepted.</summary>
    internal static readonly byte[] Acknowledgement = "ok\n"u8.ToArray();

    private readonly Func<AuthRequest, ReadOnlyMemory<byte>?> _lookup;

    /// <summary>Verifies against one shared secret; the identity is the claimed uname.</summary>
    /// <param name="secret">The token every client must present.</param>
    /// <exception cref="ArgumentException">The secret is empty.</exception>
    public TokenAuthenticator(ReadOnlyMemory<byte> secret)
    {
        if (secret.IsEmpty)
        {
            throw new ArgumentException("an empty token would authenticate everyone", nameof(secret));
        }

        _lookup = _ => secret;
    }

    /// <summary>Verifies against a per-user secret lookup; the identity is the resolved user.</summary>
    /// <param name="secretLookup">Returns the secret for a request, or null to refuse it.</param>
    /// <exception cref="ArgumentNullException">The lookup is null.</exception>
    public TokenAuthenticator(Func<AuthRequest, ReadOnlyMemory<byte>?> secretLookup)
    {
        ArgumentNullException.ThrowIfNull(secretLookup);

        _lookup = secretLookup;
    }

    /// <summary>An attach must present a verified afid.</summary>
    public bool IsRequired => true;

    /// <summary>Starts an exchange, or refuses a request the lookup has no secret for.</summary>
    /// <param name="request">The triple the afid will be bound to.</param>
    /// <param name="peer">Unused: a token proves the client, not the transport.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The session, or null when there is no secret for this request.</returns>
    public ValueTask<IAuthSession?> BeginAsync(
        AuthRequest request, PeerIdentity? peer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ReadOnlyMemory<byte>? secret = _lookup(request);
        if (secret is not { IsEmpty: false } expected)
        {
            return ValueTask.FromResult<IAuthSession?>(null);
        }

        // CA2000: the session is the core's to dispose once the afid is clunked.
#pragma warning disable CA2000
        return ValueTask.FromResult<IAuthSession?>(new TokenAuthSession(expected, request.Uname));
#pragma warning restore CA2000
    }
}
