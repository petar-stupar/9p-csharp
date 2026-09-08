using System.Globalization;
using System.Runtime.InteropServices;

namespace NineP.JsonFs;

/// <summary>
/// <c>fsync(2)</c> on a directory, which is what makes a rename durable. The temp file's own
/// <c>Flush(flushToDisk: true)</c> commits its bytes, but the entry that <c>File.Move</c> rewrote
/// lives in the directory's block, and a crash between the rename and the next journal commit can
/// bring the old name back or lose both. .NET refuses to open a directory through
/// <c>FileStream</c> or <c>File.OpenHandle</c>, so this is one <c>open(2)</c> / <c>fsync(2)</c> /
/// <c>close(2)</c> through libc on Unix. On Windows a directory cannot be flushed this way and
/// NTFS journals the rename itself, so the call is a no-op there.
/// </summary>
internal static class DirectorySync
{
    /// <summary>Commits a directory's entries to stable storage; a no-op on Windows.</summary>
    /// <param name="directory">The directory whose entries were just changed.</param>
    /// <exception cref="IOException">The directory could not be opened or synced.</exception>
    public static void Flush(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        int descriptor = Native.Open(directory, Native.ReadOnly);
        if (descriptor < 0)
        {
            throw Failure("open");
        }

        try
        {
            if (Native.Fsync(descriptor) != 0)
            {
                throw Failure("fsync");
            }
        }
        finally
        {
            // A close that fails has nothing left to tell us: the fsync either happened or threw.
            _ = Native.Close(descriptor);
        }
    }

    private static IOException Failure(string call) => new(string.Format(
        CultureInfo.InvariantCulture,
        "{0}(2) on the written-back document's directory failed with errno {1}",
        call,
        Marshal.GetLastPInvokeError()));

    private static class Native
    {
        /// <summary><c>O_RDONLY</c>, which is 0 on every platform this runs on.</summary>
        public const int ReadOnly = 0;

        // SYSLIB1054: three calls on an int and a path, the same shape as RUsage in the benchmarks;
        // LibraryImport would add a generated marshalling stub and AllowUnsafeBlocks to a project
        // that needs neither.
        // CA5392: DefaultDllImportSearchPaths controls the *Windows* loader's search order and is
        // ignored everywhere else. "libc" is resolved by the platform loader on Linux and macOS,
        // and Flush returns before reaching these on Windows.
#pragma warning disable SYSLIB1054, CA5392
        // CA2101 asks for Unicode marshalling or BestFitMapping = false; the path goes across as
        // UTF-8, which is what the libc on both platforms takes, and best-fit mapping is an ANSI
        // code-page concern that UTF-8 has no equivalent of, so the flag is set to state as much.
        [DllImport(
            "libc", EntryPoint = "open", SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        public static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        public static extern int Fsync(int descriptor);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        public static extern int Close(int descriptor);
#pragma warning restore SYSLIB1054, CA5392
    }
}
