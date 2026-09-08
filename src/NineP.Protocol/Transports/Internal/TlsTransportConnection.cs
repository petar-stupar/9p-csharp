using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NineP.Protocol.Internal;

namespace NineP.Protocol.Transports.Internal;

/// <summary>One authenticated TLS connection.</summary>
internal sealed class TlsTransportConnection(SslStream ssl, INinePConnection inner, NinePAddress address)
    : INinePConnection
{
    private int _closed;

    /// <summary>The address of the peer.</summary>
    public NinePAddress RemoteAddress => address;

    /// <summary>The peer's client certificate when mutual TLS was used, and its network address.</summary>
    public PeerIdentity? PeerIdentity { get; } = new PeerIdentity
    {
        ClientCertificate = ssl.RemoteCertificate is null
            ? null
            : X509CertificateLoader2.FromCertificate(ssl.RemoteCertificate),
        RemoteAddress = inner.PeerIdentity?.RemoteAddress,
    };

    /// <summary>The negotiated protocol, so a test can assert the floor was honoured.</summary>
    public SslProtocols NegotiatedProtocol => ssl.SslProtocol;

    /// <summary>Why this end closed, once it has.</summary>
    public CloseReason? LocalCloseReason { get; private set; }

    /// <summary>Reads up to <c>buffer.Length</c> bytes of plaintext.</summary>
    /// <param name="buffer">Where the bytes go.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes read; zero at the end of the stream.</returns>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ssl.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The peer went away without a close_notify; that is an end of stream, not a crash.
            return 0;
        }
    }

    /// <summary>Writes exactly one complete 9P message.</summary>
    /// <param name="message">The frame, <c>size[4]</c> included.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the record has been handed to the stream.</returns>
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        await ssl.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        await ssl.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the connection, sending <c>close_notify</c> before the socket goes away so that the
    /// peer can tell an orderly close from a truncation attack (RK-57).
    /// </summary>
    /// <param name="reason">Why the connection is closing.</param>
    /// <param name="cancellationToken">Unused; the shutdown record is one write.</param>
    /// <returns>A task that completes when the stream and the socket have closed.</returns>
    public async ValueTask CloseAsync(CloseReason reason, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        LocalCloseReason = reason;

        try
        {
            await ssl.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The peer is already gone; there is nobody left to send close_notify to.
        }

        await ssl.DisposeAsync().ConfigureAwait(false);
        await inner.CloseAsync(reason, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Closes the connection normally if it is still open.</summary>
    /// <returns>A task that completes when the connection has closed.</returns>
    public ValueTask DisposeAsync() => CloseAsync(CloseReason.Normal);
}
