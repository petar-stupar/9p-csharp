using System.Globalization;
using System.Security.Cryptography;
using NineP.Protocol.Internal;
using NineP.Protocol.Transports;

namespace NineP.Protocol.Auth;

/// <summary>
/// A password file of <c>user:pbkdf2-sha256$iterations$salt$hash</c> lines (S-15). The BCL has no
/// argon2id and no vetted .NET package for it exists, so the architecture's stated fallback —
/// PBKDF2-HMAC-SHA-256 at 600 000 iterations — is what ships.
/// </summary>
public sealed class PasswordFileStore : IPasswordStore
{
    /// <summary>The iteration count this workspace requires (S-15).</summary>
    public const int MinimumIterations = 600_000;

    /// <summary>The algorithm label every stored line begins with.</summary>
    public const string Algorithm = "pbkdf2-sha256";

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly Dictionary<string, StoredCredential> _users;
    private readonly StoredCredential _absent;

    private PasswordFileStore(Dictionary<string, StoredCredential> users)
    {
        _users = users;

        // The credential an unknown user is verified against. Returning early for a name the file
        // does not carry answered in microseconds where a known name took a full 600 000-iteration
        // derivation, which enumerates accounts by stopwatch. Its iteration count is taken from
        // the file so the two paths cost the same; a file whose lines disagree about the count
        // still differs by count, which is why HashPassword writes one count per file.
        _absent = new StoredCredential(
            users.Count == 0 ? MinimumIterations : users.Values.First().Iterations,
            RandomNumberGenerator.GetBytes(SaltBytes),
            RandomNumberGenerator.GetBytes(HashBytes));
    }

    /// <summary>Loads a password file.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The store.</returns>
    /// <exception cref="FormatException">A line is malformed; the message names its number.</exception>
    public static PasswordFileStore Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        Dictionary<string, StoredCredential> users = new(StringComparer.Ordinal);
        string[] lines = File.ReadAllLines(path);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || !TryParse(line[(colon + 1)..], out StoredCredential credential))
            {
                throw new FormatException(string.Format(
                    CultureInfo.InvariantCulture, "{0}: line {1} is not a credential line", path, i + 1));
            }

            users[line[..colon]] = credential;
        }

        return new PasswordFileStore(users);
    }

    /// <summary>Hashes a password into the value half of a file line.</summary>
    /// <param name="password">The password to hash.</param>
    /// <param name="iterations">The PBKDF2 iteration count; never below the workspace minimum.</param>
    /// <returns>The <c>pbkdf2-sha256$iterations$salt$hash</c> text.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The iteration count is below the minimum.</exception>
    public static string HashPassword(string password, int iterations = MinimumIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MinimumIterations);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Derive(NinePText.Utf8.GetBytes(password), salt, iterations);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}${1}${2}${3}",
            Algorithm,
            iterations,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    /// <summary>
    /// Verifies a password in constant time. "Constant" covers the unknown user too: a name the
    /// file does not carry is derived against a dummy credential of the same iteration count and
    /// compared against a dummy hash, so the answer takes the same work either way and a caller
    /// cannot enumerate accounts with a stopwatch. Exactly one derivation runs per call.
    /// </summary>
    /// <param name="user">The user name the client sent.</param>
    /// <param name="password">The password bytes the client sent.</param>
    /// <param name="cancellationToken">Cancels the verification.</param>
    /// <returns>The identity, or null.</returns>
    public ValueTask<Identity?> VerifyAsync(
        string user, ReadOnlyMemory<byte> password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();

        bool known = _users.TryGetValue(user, out StoredCredential credential);
        if (!known)
        {
            credential = _absent;
        }

        byte[] derived = Derive(password.ToArray(), credential.Salt, credential.Iterations);
        try
        {
            // Both halves are evaluated: the comparison runs for the unknown user as well, and
            // "known" is folded in afterwards rather than short-circuiting around the work.
            bool matches = ConstantTime.Equals(derived, credential.Hash);
            return ValueTask.FromResult(known && matches ? new Identity { User = user } : null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    private static byte[] Derive(byte[] password, byte[] salt, int iterations)
    {
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    private static bool TryParse(string value, out StoredCredential credential)
    {
        credential = default;
        string[] parts = value.Split('$');

        if (parts.Length != 4
            || !string.Equals(parts[0], Algorithm, StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations)
            || iterations < MinimumIterations)
        {
            return false;
        }

        byte[] salt;
        byte[] hash;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length < SaltBytes || hash.Length != HashBytes)
        {
            return false;
        }

        credential = new StoredCredential(iterations, salt, hash);
        return true;
    }

    private readonly record struct StoredCredential(int Iterations, byte[] Salt, byte[] Hash);
}
