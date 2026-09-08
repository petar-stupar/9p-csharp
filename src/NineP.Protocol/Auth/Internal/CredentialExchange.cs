using System.Security.Cryptography;
using NineP.Protocol.Internal;

namespace NineP.Protocol.Auth.Internal;

/// <summary>The one write-then-read exchange the three shipped credentials share.</summary>
internal static class CredentialExchange
{
    /// <summary>Writes a payload to the afid and requires the server's <c>ok\n</c>.</summary>
    /// <param name="channel">The afid.</param>
    /// <param name="payload">What to write.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>A task that completes when the acknowledgement arrived.</returns>
    /// <exception cref="NinePException">The server answered something else.</exception>
    public static async ValueTask WriteAndExpectOkAsync(
        IAuthChannel channel, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        await channel.WriteAsync(payload, cancellationToken).ConfigureAwait(false);

        ReadOnlyMemory<byte> answer = await channel
            .ReadAsync(TokenAuthenticator.Acknowledgement.Length, cancellationToken)
            .ConfigureAwait(false);

        // ConstantTime even here, where the value is public: it keeps "no byte comparison in
        // Auth/ outside ConstantTime" a rule a grep can enforce rather than a judgement call.
        if (!ConstantTime.Equals(answer.Span, TokenAuthenticator.Acknowledgement))
        {
            throw new NinePException(NinePError.FromEname("authentication failed"));
        }
    }
}
