namespace NineP.Server.Internal;

/// <summary>
/// A refilling budget of requests per second (reference §8 rule 40). It is lazy: nothing is
/// scheduled, and the tokens earned since the last call are computed from the injected clock when
/// one is asked for, so a session that is quiet for an hour costs nothing and still starts its
/// next burst full.
/// </summary>
/// <remarks>
/// The burst is the in-flight budget of whatever this bucket meters, so metering never refuses a
/// client that is inside its window: a peer that pipelines up to the window and waits for replies
/// proceeds at the refill rate, and only a peer that keeps more than a window's worth of work
/// arriving per second is slowed. A rate of zero disables the bucket entirely, which is the
/// default: only the operator knows what one request costs their handler.
/// </remarks>
internal sealed class TokenBucket
{
    private readonly object _gate = new();
    private readonly double _perSecond;
    private readonly double _burst;
    private readonly TimeProvider _clock;
    private double _tokens;
    private long _stamp;

    /// <summary>Creates a bucket, full.</summary>
    /// <param name="ratePerSecond">Tokens earned per second; zero or less disables metering.</param>
    /// <param name="burst">The most tokens the bucket may hold; at least one when metered.</param>
    /// <param name="clock">The clock the refill is measured against.</param>
    public TokenBucket(int ratePerSecond, int burst, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _perSecond = ratePerSecond;
        _burst = Math.Max(1, burst);
        _clock = clock;
        _tokens = _burst;
        _stamp = clock.GetTimestamp();
    }

    /// <summary>True when this bucket refuses nothing, which is the unmetered default.</summary>
    public bool Unmetered => _perSecond <= 0;

    /// <summary>Takes one token if the budget has one.</summary>
    /// <returns>True when the request may proceed; false when it is over the rate.</returns>
    public bool TryTake()
    {
        if (Unmetered)
        {
            return true;
        }

        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            double elapsed = _clock.GetElapsedTime(_stamp, now).TotalSeconds;
            _stamp = now;

            // A clock that went backwards earns nothing rather than draining the bucket.
            if (elapsed > 0)
            {
                _tokens = Math.Min(_burst, _tokens + (elapsed * _perSecond));
            }

            if (_tokens < 1)
            {
                return false;
            }

            _tokens -= 1;
            return true;
        }
    }
}
