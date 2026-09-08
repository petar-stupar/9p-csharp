using System.Collections.Concurrent;
using System.Globalization;

namespace NineP.TestSupport;

/// <summary>
/// The deadline every test that awaits a network or pipe operation runs under. A 9P test waits on
/// a socket, a pipe, a semaphore or a channel in almost every line, and a token that is never
/// cancelled turns one wedged wait into a job that runs until the CI agent is reaped — which is
/// exactly what this repository hit once: a run that sat at 0% CPU for 1 h 36 min with six tests
/// unreported. Wrapping the ambient token here gives every one of those waits a bound, so a hang
/// is a named failing test within <see cref="Default"/> instead.
/// </summary>
/// <remarks>
/// The deadline is per test, not per wait: the wrapper is cached against the ambient token, so
/// every <c>Ct</c> in one test shares one clock that starts at the first use. Set
/// <c>NINEP_TEST_DEADLINE_SECONDS</c> to widen it on a slow machine, or to <c>0</c> to turn it off
/// while debugging.
/// </remarks>
public static class TestDeadlines
{
    private static readonly ConcurrentDictionary<CancellationToken, CancellationTokenSource> Sources = new();

    /// <summary>How long one test may spend inside the waits it wraps; three minutes by default.</summary>
    /// <remarks>
    /// The slowest shipped test — the conformance self-run, which spawns dozens of processes —
    /// takes about 95 s on the reference machine, so three minutes is roughly twice the worst
    /// honest case and still two orders of magnitude below a hang.
    /// </remarks>
    public static TimeSpan Default { get; } = FromEnvironment();

    /// <summary>True when a deadline is being applied at all.</summary>
    public static bool Enabled => Default > TimeSpan.Zero;

    /// <summary>
    /// The ambient test token with <see cref="Default"/> added to it. The result is cached against
    /// the token handed in, so a test that reads it a hundred times gets one deadline and not a
    /// hundred timers.
    /// </summary>
    /// <param name="ambient">The test runner's own cancellation token.</param>
    /// <returns>A token that is cancelled by the runner or by the deadline, whichever comes first.</returns>
    public static CancellationToken Wrap(CancellationToken ambient)
    {
        // A token that cannot be cancelled has no test behind it — a helper called outside a
        // test, or a caller that passed None — and caching one deadline against None would arm a
        // clock that cancels the rest of the assembly run.
        if (!Enabled || !ambient.CanBeCanceled)
        {
            return ambient;
        }

        return Sources.GetOrAdd(ambient, Start).Token;
    }

    /// <summary>
    /// A source for one wait that wants its own, shorter bound — a step whose whole point is that
    /// it finishes quickly. It is linked to the ambient token, so the test's own deadline still
    /// applies underneath it.
    /// </summary>
    /// <param name="within">How long this particular wait may take.</param>
    /// <param name="ambient">The test runner's own cancellation token.</param>
    /// <returns>The source; the caller disposes it.</returns>
    public static CancellationTokenSource Create(TimeSpan within, CancellationToken ambient)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(Wrap(ambient));
        source.CancelAfter(within);
        return source;
    }

    private static CancellationTokenSource Start(CancellationToken ambient)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(ambient);

        // The entry is dropped when the runner ends the test, so a long assembly run does not
        // accumulate one live source per test case.
        if (!ambient.IsCancellationRequested)
        {
            ambient.Register(static state => Sources.TryRemove((CancellationToken)state!, out _), ambient);
        }

        source.CancelAfter(Default);
        return source;
    }

    private static TimeSpan FromEnvironment()
    {
        string? configured = Environment.GetEnvironmentVariable("NINEP_TEST_DEADLINE_SECONDS");

        return configured is not null
            && double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            && seconds >= 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromMinutes(3);
    }
}
