using System.Diagnostics;
using System.Globalization;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests;

/// <summary>
/// Reference §5.3: "the tag ... may be reused immediately" once the reply has arrived, and §8
/// rule 6 says a server refuses a tag that is <em>still</em> outstanding. Linux v9fs allocates
/// lowest-free-first, so a serial workload puts every request on the same tag and reuses it the
/// instant the reply lands. The server must therefore have freed the tag <b>before</b> the reply
/// reaches the wire, not after: freeing it afterwards leaves a window in which the client's next,
/// perfectly legal request is refused <c>"duplicate tag"</c> (<c>EINVAL</c> in <c>.L</c>) and
/// dropped.
/// <para>
/// The window is the handful of instructions between two statements on one thread and opens only
/// when that thread is pre-empted inside it, so no deterministic test can pin it. This is a
/// bounded stress regression instead, and it runs in every <c>dotnet test</c>: the budget is
/// <c>NINEP_TAG_STRESS_SECONDS</c>, five seconds by default, because the defect showed itself
/// within 1.5 s on every run it was measured against, and CI's own step runs it once more at
/// 45 s on one target framework (two concurrent runs would share the machine and dilute the race
/// each is looking for). It carries the <c>Stress</c> trait for a filter that wants it alone.
/// </para>
/// <para>
/// <b>Mutation (the defect it was written against):</b> in both
/// <c>ServerSession.CompleteAsync</c> overloads, move <c>_tags.Release(pending)</c> back below
/// the <c>EnqueueAsync</c> call. Measured on the reference machine (Apple M-series, Debug,
/// net10.0, in-memory transport): <b>5 of 5 runs failed</b>, every one of the sixteen connections
/// drawing <c>Rlerror EINVAL</c> after between 29 and 29 202 round trips, so a run failed within
/// 0.6-1.5 s of its 45 s budget. With the release above the enqueue, 5 of 5 runs saw <b>zero</b>
/// refusals, at 22 150 334 round trips per 45 s run. At the five-second default (2026-09-08,
/// same machine, Debug, net10.0) the mutation failed <b>3 of 3 runs</b> within 0.4-0.9 s, after
/// 1 820 to 12 013 round trips on the connection that drew the refusal; the unmutated test ran
/// 5.1 s on each target framework.
/// </para>
/// </summary>
public sealed class TagReuseStressTests
{
    /// <summary>How many connections hammer one tag each, in parallel.</summary>
    private const int Connections = 16;

    /// <summary>The tag every connection reuses; v9fs would use 0, and the number does not matter.</summary>
    private const ushort ReusedTag = 1;

    /// <summary>Enough round trips to detect the defect with a wide margin; the clock usually wins first.</summary>
    private const long RoundTripBudget = 2_000_000;

    /// <summary>
    /// The wall-clock bound: <c>NINEP_TAG_STRESS_SECONDS</c>, or five seconds. CI's stress step
    /// sets 45; the per-test deadline of <see cref="TestDeadlines"/> is the ceiling either way.
    /// </summary>
    private static readonly TimeSpan Budget = BudgetFromEnvironment();

    /// <summary>
    /// The round trips a run must reach to count as a green with detection power behind it:
    /// 4 000 per second of budget. The reference machine does about 490 000 a second, and the
    /// defect showed itself within 29 202 on every one of the sixteen connections, so the floor
    /// leaves two orders of magnitude of headroom for a slower or a busier machine.
    /// </summary>
    private static readonly long Floor = (long)(4_000 * Budget.TotalSeconds);

    /// <summary>
    /// Sixteen connections, every request under the same tag, reused the moment its reply arrives:
    /// not one of them may be answered with an error, because not one of them is illegal.
    /// </summary>
    /// <returns>A task that completes when the budget is spent.</returns>
    [Fact(Timeout = 180_000)]
    [Trait("Category", "Stress")]
    public async Task ATagReusedTheInstantItsReplyArrivesIsNeverRefused()
    {
        CancellationToken ambient = TestContext.Current.CancellationToken;
        await using ServerHarness harness = await ServerHarness.StartAsync();

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ambient);
        budget.CancelAfter(Budget);

        long trips = 0;
        Stopwatch clock = Stopwatch.StartNew();
        Task<string?>[] runs = [.. Enumerable
            .Range(0, Connections)
            .Select(_ => Task.Run(() => HammerAsync(harness, () => Interlocked.Increment(ref trips), budget.Token), ambient))];

        string?[] refusals = await Task.WhenAll(runs);
        clock.Stop();

        long observed = Interlocked.Read(ref trips);
        string report = string.Format(
            CultureInfo.InvariantCulture,
            "{0} round trips on tag {1} across {2} connections in {3:F1} s",
            observed,
            ReusedTag,
            Connections,
            clock.Elapsed.TotalSeconds);

        TestContext.Current.TestOutputHelper?.WriteLine(report);

        Assert.All(refusals, refusal => Assert.True(refusal is null, refusal + " — " + report));

        // A run that barely moved would be a green with no detection power behind it; see Floor.
        Assert.True(observed >= Floor, report);
    }

    private static TimeSpan BudgetFromEnvironment()
    {
        string? configured = Environment.GetEnvironmentVariable("NINEP_TAG_STRESS_SECONDS");

        return configured is not null
            && double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// One connection: attach, then <c>Tgetattr</c> the root over and over under
    /// <see cref="ReusedTag"/>, reusing the tag as soon as the previous reply is decoded.
    /// </summary>
    /// <param name="harness">The server to talk to.</param>
    /// <param name="count">Called once per completed round trip.</param>
    /// <param name="cancellationToken">Ends the run when the budget is spent.</param>
    /// <returns>Null when every reply was an <c>Rgetattr</c>, else what went wrong.</returns>
    private static async Task<string?> HammerAsync(
        ServerHarness harness, Action count, CancellationToken cancellationToken)
    {
        try
        {
            await using WireClient client =
                await WireClient.ConnectAsync(harness, Dialect.P9_2000_L, cancellationToken: cancellationToken);

            await client.AttachAsync(1, cancellationToken);

            for (long trip = 0; trip < RoundTripBudget && !cancellationToken.IsCancellationRequested; trip++)
            {
                await client.SendAsync(new Tgetattr(ReusedTag, 1, GetAttrMask.Basic), cancellationToken);

                byte[] reply = await client.ReceiveFrameAsync(cancellationToken);
                if (reply.Length == 0)
                {
                    return "the server closed the connection";
                }

                MessageType type = MessageCodec.PeekType(reply);
                if (type != MessageType.Rgetattr)
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "round trip {0} on tag {1} drew {2} (errno {3}), not Rgetattr",
                        trip,
                        MessageCodec.PeekTag(reply),
                        type,
                        type == MessageType.Rlerror
                            ? MessageCodec.Decode<Rlerror>(reply, Dialect.P9_2000_L).Ecode
                            : 0);
                }

                count();
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            // The budget is spent; everything this connection saw was legal.
            return null;
        }
    }
}
