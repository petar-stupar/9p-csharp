using System.Security.Cryptography;
using NineP.Protocol.Transports;

namespace NineP.Protocol.Auth.Internal;

/// <summary>One token exchange: bytes in until they match the secret, then <c>ok\n</c> out.</summary>
internal sealed class TokenAuthSession(ReadOnlyMemory<byte> secret, string user) : IAuthSession
{
    private readonly byte[] _received = new byte[secret.Length];
    private int _filled;
    private int _read;

    /// <summary>The identity once the token has matched; null until then.</summary>
    public Identity? Identity { get; private set; }

    /// <summary>Accepts token bytes, however the client chose to chunk its writes.</summary>
    /// <param name="data">The bytes from one <c>Twrite</c>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A completed task.</returns>
    /// <exception cref="NinePException">More bytes arrived than the token can be.</exception>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_filled + data.Length > _received.Length)
        {
            // A token longer than the secret can never match, and buffering it would let a peer
            // spend the server's memory on a guess it has already lost.
            throw new NinePException(NinePError.FromEname("authentication failed"));
        }

        data.Span.CopyTo(_received.AsSpan(_filled));
        _filled += data.Length;

        if (_filled == _received.Length && ConstantTime.Equals(_received, secret.Span))
        {
            Identity = new Identity { User = user };
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Produces <c>ok\n</c> once, and then the empty answer that ends the exchange.</summary>
    /// <param name="maxBytes">The most the client asked for.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The acknowledgement, or empty.</returns>
    /// <exception cref="NinePException">The token has not been accepted.</exception>
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

    /// <summary>Wipes the copy of the token this session buffered.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        CryptographicOperations.ZeroMemory(_received);
        return ValueTask.CompletedTask;
    }
}
