using System.Security.Cryptography.X509Certificates;
using NineP.Protocol.Transports;

namespace NineP.Protocol.Auth.Internal;

/// <summary>
/// An exchange that is already over: the certificate was the credential. The afid still exists
/// because <c>Tauth</c>/<c>Tattach</c> is the shape the protocol fixes, and a client that reads it
/// gets the same <c>ok\n</c> every other default authenticator answers.
/// </summary>
internal sealed class TlsClientCertSession(Identity identity) : IAuthSession
{
    private int _read;

    /// <summary>The identity the certificate carried.</summary>
    public Identity? Identity => identity;

    /// <summary>Accepts and discards anything the client writes; there is nothing to verify.</summary>
    /// <param name="data">The bytes from one <c>Twrite</c>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A completed task.</returns>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <summary>Produces <c>ok\n</c> once, and then the empty answer that ends the exchange.</summary>
    /// <param name="maxBytes">The most the client asked for.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The acknowledgement, or empty.</returns>
    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(
        int maxBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int remaining = Math.Min(maxBytes, TokenAuthenticator.Acknowledgement.Length - _read);
        if (remaining <= 0)
        {
            return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
        }

        ReadOnlyMemory<byte> answer = TokenAuthenticator.Acknowledgement.AsMemory(_read, remaining);
        _read += remaining;
        return ValueTask.FromResult(answer);
    }

    /// <summary>Nothing secret was buffered, so there is nothing to wipe.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
