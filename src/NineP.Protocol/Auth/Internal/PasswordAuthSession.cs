using System.Globalization;
using System.Security.Cryptography;
using NineP.Protocol.Internal;
using NineP.Protocol.Transports;

namespace NineP.Protocol.Auth.Internal;

/// <summary>One password exchange: <c>user\npassword\n</c> in, <c>ok\n</c> out.</summary>
internal sealed class PasswordAuthSession(IPasswordStore store) : IAuthSession
{
    private readonly List<byte> _received = [];
    private int _read;
    private bool _decided;

    /// <summary>The identity once the store has verified the credential; null until then.</summary>
    public Identity? Identity { get; private set; }

    /// <summary>Accepts credential bytes until both lines have arrived, then verifies.</summary>
    /// <param name="data">The bytes from one <c>Twrite</c>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes once the credential has been considered.</returns>
    /// <exception cref="NinePException">The credential is malformed or wrong.</exception>
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_decided)
        {
            // The credential is judged once. Without this latch every further Twrite re-ran the
            // whole 600 000-iteration derivation, so one connection could buy MaxAuthBytes worth
            // of them for the price of a few bytes each.
            if (Identity is null)
            {
                throw new NinePException(NinePError.FromEname("authentication failed"));
            }

            return;
        }

        if (_received.Count + data.Length > Limits.Default.MaxAuthBytes)
        {
            throw new NinePException(NinePError.FromEname("authentication failed"));
        }

        _received.AddRange(data.Span);

        byte[] buffer = [.. _received];
        try
        {
            if (!TrySplit(buffer, out string user, out ReadOnlyMemory<byte> password))
            {
                return;
            }

            _decided = true;
            Identity = await store.VerifyAsync(user, password, cancellationToken).ConfigureAwait(false)
                ?? throw new NinePException(NinePError.FromEname("authentication failed"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>Produces <c>ok\n</c> once, and then the empty answer that ends the exchange.</summary>
    /// <param name="maxBytes">The most the client asked for.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The acknowledgement, or empty.</returns>
    /// <exception cref="NinePException">The credential has not been accepted.</exception>
    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(
        int maxBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Identity is null)
        {
            throw new NinePException(NinePError.FromEname("authentication failed"));
        }

        int remaining = Math.Min(maxBytes, TokenAuthenticator.Acknowledgement.Length - _read);
        if (remaining <= 0)
        {
            return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
        }

        ReadOnlyMemory<byte> answer = TokenAuthenticator.Acknowledgement.AsMemory(_read, remaining);
        _read += remaining;
        return ValueTask.FromResult(answer);
    }

    /// <summary>Wipes the credential bytes this session buffered.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        for (int i = 0; i < _received.Count; i++)
        {
            _received[i] = 0;
        }

        _received.Clear();
        return ValueTask.CompletedTask;
    }

    private static bool TrySplit(byte[] buffer, out string user, out ReadOnlyMemory<byte> password)
    {
        user = string.Empty;
        password = default;

        int first = Array.IndexOf(buffer, (byte)'\n');
        if (first < 0)
        {
            return false;
        }

        int second = Array.IndexOf(buffer, (byte)'\n', first + 1);
        if (second < 0)
        {
            return false;
        }

        if (!NinePText.TryDecode(buffer.AsSpan(0, first), out user, out _))
        {
            throw new NinePException(NinePError.FromEname("authentication failed"));
        }

        password = buffer.AsMemory(first + 1, second - first - 1);
        return true;
    }
}
