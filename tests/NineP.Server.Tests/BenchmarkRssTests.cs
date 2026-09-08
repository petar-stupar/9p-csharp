#if NET10_0_OR_GREATER
using System.Diagnostics;
using NineP.Benchmarks;
using Xunit;

namespace NineP.Server.Tests;

/// <summary>
/// AC-e and S-12: benchmark (c) of architecture §9 reports peak RSS through
/// <c>getrusage(RUSAGE_SELF).ru_maxrss</c>, because <see cref="Process.PeakWorkingSet64"/> reads
/// <b>0</b> on this platform (E-5/E-6) and a benchmark that publishes 0 bytes of peak memory is
/// worse than one that publishes nothing. This test is what stops that ever happening again.
/// </summary>
public sealed class BenchmarkRssTests
{
    /// <summary>The smallest peak resident set a .NET process can plausibly have.</summary>
    private const long PlausibleFloor = 8 * 1024 * 1024;

    /// <summary>
    /// The mechanism returns a real number: greater than zero, of the same order as the resident
    /// set this process holds, and never smaller after more memory has been touched.
    /// </summary>
    [Fact]
    public void MaxRssIsNonZeroAndAtLeastWorkingSet()
    {
        long peak = RUsage.PeakBytes();

        Assert.True(peak > 0, "getrusage reported a peak RSS of zero");
        Assert.True(peak >= PlausibleFloor, $"peak RSS {peak} is too small to be a .NET process");

        using Process self = Process.GetCurrentProcess();
        self.Refresh();

        // The two are not the same accounting, and on macOS the difference is not small:
        // Process.WorkingSet64 is the task's phys_footprint, which counts compressed and reserved
        // pages, while ru_maxrss counts resident ones. In a quiet process they agree to three
        // decimal places; under four threads churning 8 MiB blocks the ratio was measured down to
        // 0.427 (peak 41 353 216 against a working set of 96 944 128). The assertion is therefore
        // that they are of the same order — which a stub returning zero or a small constant still
        // fails — and not that one bounds the other.
        Assert.True(
            peak * 4 >= self.WorkingSet64,
            $"peak RSS {peak} is far below the current working set {self.WorkingSet64}");

        // Touching sixty-four mebibytes cannot lower the peak, and on a platform where the call
        // silently returned a constant this is the assertion that would notice.
        byte[] ballast = GC.AllocateUninitializedArray<byte>(64 * 1024 * 1024);
        ballast.AsSpan().Fill(0xA5);

        long after = RUsage.PeakBytes();
        Assert.True(after >= peak, $"peak RSS fell from {peak} to {after}");

        GC.KeepAlive(ballast);
    }
}
#endif
