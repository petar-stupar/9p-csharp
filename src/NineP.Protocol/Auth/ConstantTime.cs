using System.Security.Cryptography;

namespace NineP.Protocol.Auth;

/// <summary>
/// Constant-time comparison. This is the <b>only</b> byte-comparison path for secrets in this
/// library (workspace architecture §8.3), and a test greps the authentication sources to keep it
/// that way: a comparison that returns early on the first differing byte leaks the secret one byte
/// at a time to anyone who can measure the answer.
/// </summary>
public static class ConstantTime
{
    /// <summary>True when both spans are equal, in time independent of where they differ.</summary>
    /// <param name="left">One secret.</param>
    /// <param name="right">The other.</param>
    /// <returns>True when they are byte-for-byte equal.</returns>
    public static bool Equals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        CryptographicOperations.FixedTimeEquals(left, right);
}
