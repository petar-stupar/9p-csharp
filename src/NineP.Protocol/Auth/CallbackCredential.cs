using System.Security.Cryptography;
using NineP.Protocol.Internal;

namespace NineP.Protocol.Auth;

/// <summary>An arbitrary exchange written by the caller: the client-side extension point.</summary>
public sealed class CallbackCredential : ICredential
{
    private readonly Func<IAuthChannel, CancellationToken, ValueTask> _exchange;

    /// <summary>Creates a credential that runs the caller's own exchange.</summary>
    /// <param name="exchange">The exchange to run over the afid.</param>
    /// <exception cref="ArgumentNullException">The exchange is null.</exception>
    public CallbackCredential(Func<IAuthChannel, CancellationToken, ValueTask> exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        _exchange = exchange;
    }

    /// <summary>Runs the caller's exchange.</summary>
    /// <param name="channel">The afid, as a read/write pair.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>Whatever the caller's exchange returns.</returns>
    public ValueTask AuthenticateAsync(IAuthChannel channel, CancellationToken cancellationToken = default) =>
        _exchange(channel, cancellationToken);
}
