using System.Buffers.Binary;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Transports;

namespace NineP.TestSupport;

/// <summary>
/// Runs one afid exchange over a <see cref="MemoryTransport"/> pair, so an
/// <see cref="IAuthenticator"/> and an <see cref="ICredential"/> can be tested against each other
/// across a real wire before the server package exists (S-31). The framing is the test harness's,
/// not 9P's: the server's <c>Twrite</c>/<c>Tread</c> handling is task 33's to deliver.
/// </summary>
public static class AfidPump
{
    private const byte WriteOp = (byte)'W';
    private const byte ReadOp = (byte)'R';

    /// <summary>Runs a credential against an authenticator and reports the identity it produced.</summary>
    /// <param name="authenticator">The server side.</param>
    /// <param name="credential">The client side.</param>
    /// <param name="request">The triple the afid is bound to.</param>
    /// <param name="peer">What the transport learned about the peer, when it learned anything.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The identity the exchange produced, or null when Tauth was refused.</returns>
    /// <exception cref="NinePException">The server refused the credential.</exception>
    public static async Task<Identity?> ExchangeAsync(
        IAuthenticator authenticator,
        ICredential credential,
        AuthRequest request,
        PeerIdentity? peer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticator);
        ArgumentNullException.ThrowIfNull(credential);

        IAuthSession? session = await authenticator.BeginAsync(request, peer, cancellationToken);
        if (session is null)
        {
            return null;
        }

        await using (session)
        {
            (INinePConnection client, INinePConnection server) = MemoryTransport.CreatePair();
            await using (client)
            await using (server)
            {
                Task serving = Task.Run(
                    async () => await ServeAsync(server, session, cancellationToken), cancellationToken);

                try
                {
                    await credential.AuthenticateAsync(new WireChannel(client), cancellationToken);
                }
                finally
                {
                    await client.CloseAsync(CloseReason.Normal, CancellationToken.None);
                    await serving;
                }
            }

            return session.Identity;
        }
    }

    private static async Task ServeAsync(
        INinePConnection connection, IAuthSession session, CancellationToken cancellationToken)
    {
        while (true)
        {
            byte[] header = await ReadExactlyAsync(connection, 5, cancellationToken);
            if (header.Length < 5)
            {
                return;
            }

            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1));

            if (header[0] == WriteOp)
            {
                byte[] payload = await ReadExactlyAsync(connection, count, cancellationToken);
                try
                {
                    await session.WriteAsync(payload, cancellationToken);
                }
                catch (NinePException)
                {
                    // The refusal reaches the client as an empty answer to its next read, which is
                    // what an Rerror on the afid would look like once the server core exists.
                }

                continue;
            }

            ReadOnlyMemory<byte> answer;
            try
            {
                answer = await session.ReadAsync(count, cancellationToken);
            }
            catch (NinePException)
            {
                answer = ReadOnlyMemory<byte>.Empty;
            }

            byte[] framed = new byte[4 + answer.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)answer.Length);
            answer.Span.CopyTo(framed.AsSpan(4));
            await connection.WriteAsync(framed, cancellationToken);
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(
        INinePConnection connection, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int filled = 0;

        while (filled < count)
        {
            int read = await connection.ReadAsync(buffer.AsMemory(filled), cancellationToken);
            if (read == 0)
            {
                return buffer.AsSpan(0, filled).ToArray();
            }

            filled += read;
        }

        return buffer;
    }

    private sealed class WireChannel(INinePConnection connection) : IAuthChannel
    {
        public async ValueTask WriteAsync(
            ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            byte[] framed = new byte[5 + data.Length];
            framed[0] = WriteOp;
            BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(1), (uint)data.Length);
            data.Span.CopyTo(framed.AsSpan(5));
            await connection.WriteAsync(framed, cancellationToken);
        }

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(
            int maxBytes, CancellationToken cancellationToken = default)
        {
            byte[] request = new byte[5];
            request[0] = ReadOp;
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), (uint)maxBytes);
            await connection.WriteAsync(request, cancellationToken);

            byte[] header = await ReadExactlyAsync(connection, 4, cancellationToken);
            if (header.Length < 4)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
            return count == 0 ? ReadOnlyMemory<byte>.Empty : await ReadExactlyAsync(connection, count, cancellationToken);
        }
    }
}
