using System.Globalization;
using System.Security.Cryptography;
using NineP.Protocol.Internal;
using NineP.Protocol.Transports;

namespace NineP.Protocol.Auth;

/// <summary>A store of hashed credentials for <see cref="PasswordAuthenticator"/>.</summary>
public interface IPasswordStore
{
    /// <summary>Verifies a password in constant time and returns the identity.</summary>
    /// <param name="user">The user name the client sent.</param>
    /// <param name="password">The password bytes the client sent.</param>
    /// <param name="cancellationToken">Cancels the verification.</param>
    /// <returns>The identity, or null when the credential is wrong or the user is unknown.</returns>
    ValueTask<Identity?> VerifyAsync(
        string user, ReadOnlyMemory<byte> password, CancellationToken cancellationToken = default);
}
