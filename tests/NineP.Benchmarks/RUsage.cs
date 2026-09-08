using System.Runtime.InteropServices;

namespace NineP.Benchmarks;

/// <summary>
/// Peak resident set size of this process, read from <c>getrusage(RUSAGE_SELF)</c> (S-12).
/// <see cref="System.Diagnostics.Process.PeakWorkingSet64"/> is <b>not</b> used: it reads 0 on
/// macOS (E-5/E-6), and a benchmark that reports 0 bytes of peak memory is worse than one that
/// reports nothing at all.
/// </summary>
public static class RUsage
{
    private const int RusageSelf = 0;

    /// <summary>
    /// The largest resident set this process has held, in bytes. <c>ru_maxrss</c> is bytes on the
    /// BSDs, macOS included, and kibibytes on Linux; both are normalised here.
    /// </summary>
    /// <returns>The peak resident set size in bytes.</returns>
    /// <exception cref="InvalidOperationException"><c>getrusage</c> failed.</exception>
    public static long PeakBytes()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
        {
            throw new PlatformNotSupportedException("getrusage is a POSIX call; this repository's CI is Linux and macOS");
        }

        if (Native.GetRUsage(RusageSelf, out Native.RUsageValue usage) != 0)
        {
            throw new InvalidOperationException(
                "getrusage(RUSAGE_SELF) failed with errno " + Marshal.GetLastWin32Error().ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        }

        return OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()
            ? usage.MaxRss
            : usage.MaxRss * 1024;
    }

    private static class Native
    {
        // SYSLIB1054: the spec (§9 task 41) names DllImport, and this declaration is one call on
        // one blittable struct; LibraryImport would add a generated marshalling stub and
        // AllowUnsafeBlocks to a project that needs neither.
        // CA5392: DefaultDllImportSearchPaths controls the *Windows* loader's search order and is
        // ignored everywhere else. "libc" is resolved by the platform loader on Linux and macOS,
        // which are the only platforms PeakBytes will call it on.
#pragma warning disable SYSLIB1054, CA5392
        [DllImport("libc", EntryPoint = "getrusage", SetLastError = true)]
        public static extern int GetRUsage(int who, out RUsageValue usage);
#pragma warning restore SYSLIB1054, CA5392

        /// <summary>
        /// The BSD <c>struct rusage</c>: two <c>struct timeval</c>s of two words each, then
        /// fourteen <c>long</c>s of which <c>ru_maxrss</c> is the first. <c>struct timeval</c> is
        /// sixteen bytes on both platforms this repository builds for — <c>tv_sec</c> is a
        /// 64-bit <c>time_t</c>, and <c>tv_usec</c> is either a 64-bit <c>suseconds_t</c> (Linux)
        /// or a 32-bit one followed by four bytes of padding (macOS) — so the offset of
        /// <c>ru_maxrss</c> is 32 either way.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct RUsageValue
        {
            public long UserSeconds;
            public long UserMicroseconds;
            public long SystemSeconds;
            public long SystemMicroseconds;
            public long MaxRss;
            public long IntegralSharedText;
            public long IntegralUnsharedData;
            public long IntegralUnsharedStack;
            public long MinorFaults;
            public long MajorFaults;
            public long Swaps;
            public long BlockInputOperations;
            public long BlockOutputOperations;
            public long MessagesSent;
            public long MessagesReceived;
            public long SignalsReceived;
            public long VoluntaryContextSwitches;
            public long InvoluntaryContextSwitches;
        }
    }
}
