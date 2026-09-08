using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NineP.Protocol.Internal;
using NineP.Protocol.Transports;

namespace NineP.Protocol.Transports.Internal;

/// <summary>
/// What an accept loop does with a socket error. <c>AcceptAsync</c> returning null is the
/// contract's "this listener is finished" signal, so returning it for every
/// <see cref="SocketException"/> stopped the whole service for one transient refusal — an
/// <c>ECONNABORTED</c> from a peer that gave up between the SYN and the accept, or an
/// <c>EMFILE</c> while the process was briefly out of descriptors — while the process stayed up
/// and looked healthy. Those are conditions the next accept usually survives, so they are logged
/// and retried behind a short backoff; only a listener that has actually been closed ends the
/// loop.
/// </summary>
internal static class AcceptPolicy
{
    /// <summary>How long the first retry waits.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMilliseconds(5);

    /// <summary>The longest a retry waits, however many have failed in a row.</summary>
    public static readonly TimeSpan LongestDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Whether a socket error means the listening socket itself is gone.</summary>
    /// <param name="failure">The error the accept raised.</param>
    /// <returns>True when the accept loop must stop rather than retry.</returns>
    public static bool IsFatal(SocketException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        // Disposal races a pending accept and surfaces here rather than as
        // ObjectDisposedException on some platforms; so does a shutdown of the listening socket.
        return failure.SocketErrorCode is SocketError.OperationAborted
            or SocketError.Interrupted
            or SocketError.Shutdown
            or SocketError.NotSocket
            or SocketError.InvalidArgument;
    }

    /// <summary>
    /// Runs one accept, retrying it behind a backoff for as long as the failures are transient.
    /// </summary>
    /// <typeparam name="T">What the accept produces.</typeparam>
    /// <param name="accept">The accept to run; it is called again after a transient failure.</param>
    /// <param name="logger">Where a retried failure is reported.</param>
    /// <param name="address">The address being listened on, for that report.</param>
    /// <param name="isClosed">Whether the listener has been disposed since the accept began.</param>
    /// <param name="cancellationToken">Stops accepting.</param>
    /// <returns>What the accept produced, or null once the listener is finished.</returns>
    public static async ValueTask<T?> AcceptAsync<T>(
        Func<CancellationToken, ValueTask<T>> accept,
        ILogger logger,
        NinePAddress address,
        Func<bool> isClosed,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(accept);
        ArgumentNullException.ThrowIfNull(isClosed);

        TimeSpan waited = TimeSpan.Zero;

        while (true)
        {
            try
            {
                return await accept(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (SocketException failure) when (!IsFatal(failure) && !isClosed())
            {
                try
                {
                    waited = await BackOffAsync(logger, address, failure, waited, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
            catch (SocketException)
            {
                return null;
            }
        }
    }

    /// <summary>Logs a transient accept failure and waits before the next attempt.</summary>
    /// <param name="logger">Where the failure is reported.</param>
    /// <param name="address">The address being listened on.</param>
    /// <param name="failure">The error the accept raised.</param>
    /// <param name="waited">How long the previous retry waited; zero for the first.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>How long this retry waited, to be passed back on the next one.</returns>
    public static async ValueTask<TimeSpan> BackOffAsync(
        ILogger logger,
        NinePAddress address,
        SocketException failure,
        TimeSpan waited,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(failure);

        TimeSpan next = waited <= TimeSpan.Zero
            ? FirstDelay
            : waited >= LongestDelay ? LongestDelay : waited * 2;

        logger.AcceptFailedRetrying(
            UntrustedText.Sanitize(address.ToString()), failure.SocketErrorCode, next.TotalMilliseconds);

        await Task.Delay(next, cancellationToken).ConfigureAwait(false);
        return next;
    }
}
