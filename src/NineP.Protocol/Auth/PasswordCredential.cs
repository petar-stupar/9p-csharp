using System.Security.Cryptography;
using NineP.Protocol.Auth.Internal;
using NineP.Protocol.Internal;

namespace NineP.Protocol.Auth;

/// <summary>Writes <c>user\npassword\n</c> to the afid (mirror of <see cref="PasswordAuthenticator"/>).</summary>
public sealed class PasswordCredential : ICredential
{
    private readonly string _user;
    private readonly string _password;

    /// <summary>Creates a credential over a user name and a password.</summary>
    /// <param name="user">The user name; it may not contain a newline.</param>
    /// <param name="password">The password; it may not contain a newline.</param>
    /// <exception cref="ArgumentException">Either value contains a newline.</exception>
    public PasswordCredential(string user, string password)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);

        // The wire form is line-delimited, so a newline inside either field would let a caller
        // forge the other one.
        if (user.Contains('\n', StringComparison.Ordinal) || password.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("a newline cannot appear in a password credential", nameof(user));
        }

        _user = user;
        _password = password;
    }

    /// <summary>Writes the two lines and waits for the acknowledgement.</summary>
    /// <param name="channel">The afid, as a read/write pair.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when the server accepted the credential.</returns>
    /// <exception cref="NinePException">The server refused it.</exception>
    public async ValueTask AuthenticateAsync(
        IAuthChannel channel, CancellationToken cancellationToken = default)
    {
        byte[] payload = NinePText.Utf8.GetBytes(_user + "\n" + _password + "\n");
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
