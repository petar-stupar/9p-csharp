using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using NineP.Protocol;

namespace NineP.JsonFs;

/// <summary>
/// Every change to the served document goes through here. Two reasons: read-only is the default
/// and one refusal site is easier to trust than a dozen, and <c>--write-back</c> has to rewrite
/// the file after each mutation — or, with <c>--write-back-delay</c>, once per window — which is
/// one place too.
/// </summary>
internal sealed class JsonFsMutator : IDisposable
{
    /// <summary>
    /// The refusal a read-only server answers a write with: <c>EROFS</c>, and the ename
    /// <see cref="ErrorTable"/> derives from it. The pair has to come from the table, because
    /// 9P2000 carries only the ename and 9P2000.L carries only the errno: any other pairing would
    /// reach the client as a different error in one dialect than in the other.
    /// </summary>
    private static readonly NinePError ReadOnly = NinePError.FromErrno(Errno.EROFS);

    // Deterministic persistence fault injection for tests; production leaves these unset.
    internal Action? BeforeReplace { get; set; }
    internal Action? AfterReplace { get; set; }

    private readonly JsonTree _tree;
    private readonly bool _writable;
    private readonly string? _writeBackPath;
    private readonly TimeSpan _writeBackDelay;
    private readonly TimeProvider _clock;

    // The write-back window, all guarded by the tree's gate: whether the document on disk is
    // behind the one in memory, whether a window is already open, and the timer that closes it.
    private ITimer? _window;
    private bool _dirty;
    private bool _armed;
    private bool _disposed;

    /// <summary>Creates the mutator for one served document.</summary>
    /// <param name="tree">The document.</param>
    /// <param name="writable">True to accept changes at all.</param>
    /// <param name="writeBackPath">Where to rewrite the document; null to keep changes in memory.</param>
    /// <param name="writeBackDelay">
    /// How long after a mutation the rewrite happens, coalescing every mutation inside that window
    /// into one; zero rewrites inside the mutation itself.
    /// </param>
    /// <param name="clock">The clock the window is measured on.</param>
    /// <exception cref="ArgumentOutOfRangeException">The delay is negative.</exception>
    public JsonFsMutator(
        JsonTree tree,
        bool writable,
        string? writeBackPath,
        TimeSpan writeBackDelay = default,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentOutOfRangeException.ThrowIfLessThan(writeBackDelay, TimeSpan.Zero);

        _tree = tree;
        _writable = writable;
        _writeBackPath = writeBackPath;
        _writeBackDelay = writeBackDelay;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>True when this server accepts changes.</summary>
    public bool IsWritable => _writable;

    /// <summary>True while the document on disk is behind the one in memory.</summary>
    public bool IsDirty
    {
        get
        {
            lock (_tree.Gate)
            {
                return _dirty;
            }
        }
    }

    /// <summary>Refuses the caller when the server is read-only.</summary>
    /// <exception cref="NinePException">The server is read-only.</exception>
    public void RequireWritable()
    {
        if (!_writable)
        {
            throw new NinePException(ReadOnly);
        }
    }

    /// <summary>
    /// Runs one change under the tree's lock and writes the document back after it — inside the
    /// call when the delay is zero, at the end of the open window otherwise.
    /// </summary>
    /// <typeparam name="TResult">What the change produces.</typeparam>
    /// <param name="change">The change to apply.</param>
    /// <returns>Whatever the change produced.</returns>
    /// <exception cref="NinePException">The server is read-only, or the change was refused.</exception>
    public TResult Mutate<TResult>(Func<TResult> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        RequireWritable();

        lock (_tree.Gate)
        {
            Action restore = _tree.Capture();
            TResult result;
            try
            {
                result = change();
                _tree.ValidateGrowth();
            }
            catch
            {
                // Restore existing nodes in place: open fids must keep the same objects.
                restore();
                throw;
            }

            if (_writeBackPath is null)
            {
                return result;
            }

            if (_writeBackDelay > TimeSpan.Zero && !_disposed)
            {
                // The rewrite is owed, not done: the first mutation in a window opens it, the rest
                // ride along, and the timer's callback rewrites once. A failure there has no
                // request to answer, so the document stays dirty and the next window retries; an
                // fsync or the shutdown flush is where that failure reaches a caller.
                _dirty = true;
                Arm();
                return result;
            }

            // Persistence errors report failure but keep the in-memory mutation. After rename,
            // an fsync failure cannot truthfully promise that the old disk document survived.
            WriteBack();
            return result;
        }
    }

    /// <summary>Runs one change that produces nothing.</summary>
    /// <param name="change">The change to apply.</param>
    /// <exception cref="NinePException">The server is read-only, or the change was refused.</exception>
    public void Mutate(Action change)
    {
        ArgumentNullException.ThrowIfNull(change);

        Mutate<object?>(() =>
        {
            change();
            return null;
        });
    }

    /// <summary>
    /// Rewrites the document now, which is what an <c>fsync</c> on any node asks for. With a
    /// write-back window open this is also where a rewrite the window failed to make reports.
    /// </summary>
    /// <exception cref="NinePException">The rewrite failed; the document in memory is kept.</exception>
    public void Flush()
    {
        if (_writeBackPath is null)
        {
            return;
        }

        lock (_tree.Gate)
        {
            WriteBack();
            _dirty = false;
        }
    }

    /// <summary>
    /// Closes the write-back window for good and writes back whatever it still owes, so a
    /// graceful shutdown leaves the document on disk as the last mutation left it in memory
    /// (conformance Part B step 8). A rewrite that fails here propagates: the process exits
    /// saying so rather than exiting clean over a stale document.
    /// </summary>
    /// <exception cref="NinePException">The final rewrite failed; the document in memory is kept.</exception>
    public void Dispose()
    {
        lock (_tree.Gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _armed = false;
            _window?.Dispose();
            _window = null;

            if (_dirty)
            {
                WriteBack();
                _dirty = false;
            }
        }
    }

    /// <summary>Opens the write-back window unless one is already open. The caller holds the gate.</summary>
    private void Arm()
    {
        if (_armed)
        {
            return;
        }

        _armed = true;
        if (_window is null)
        {
            _window = _clock.CreateTimer(OnWindowClosed, null, _writeBackDelay, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _window.Change(_writeBackDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The timer's callback: one rewrite for every mutation the window collected.</summary>
    /// <param name="state">Unused; the timer carries none.</param>
    private void OnWindowClosed(object? state)
    {
        lock (_tree.Gate)
        {
            _armed = false;
            if (_disposed || !_dirty)
            {
                return;
            }

            try
            {
                WriteBack();
                _dirty = false;
            }
            catch (NinePException)
            {
                // The same semantics as the synchronous path — the temp file is gone and the
                // document in memory is kept — except that there is no request to answer. The
                // document stays dirty and the next window tries again, so a disk that recovers
                // is caught up without waiting for another mutation.
                Arm();
            }
        }
    }

    /// <summary>
    /// Rewrites the document atomically: a temp file beside the target, flushed to disk, then a
    /// rename over the original, then an fsync of the directory so the rename is as durable as the
    /// bytes. A reader either sees the whole old document or the whole new one, never a
    /// half-written file — which is what <c>WriteBackIsAtomic</c> pins.
    /// </summary>
    private void WriteBack()
    {
        if (_writeBackPath is not { } target)
        {
            return;
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(target)) ?? ".";
        string temporary = Path.Combine(
            directory,
            string.Format(
                CultureInfo.InvariantCulture,
                ".{0}.{1}.tmp",
                Path.GetFileName(target),
                Environment.ProcessId));

        try
        {
            using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // UnsafeRelaxedJsonEscaping: the default encoder re-encodes every non-ASCII rune
                // as \uXXXX, so a hand-written document came back with "héllo — 世界 🚀" turned
                // into escapes. That is valid JSON and a gratuitous diff against the source. The
                // name says "unsafe" because the relaxed set does not escape <, > and & for
                // HTML-embedding; this file is written to disk, never into a page.
                using (Utf8JsonWriter writer = new(
                    stream,
                    new JsonWriterOptions
                    {
                        Indented = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }))
                {
                    _tree.Write(writer);
                }

                stream.Flush(flushToDisk: true);
            }

            BeforeReplace?.Invoke();
            File.Move(temporary, target, overwrite: true);
            AfterReplace?.Invoke();

            // The Flush above committed the temp file's bytes; the rename lives in the directory,
            // and it is the directory that has to reach the disk for the new name to survive a
            // crash. A failure here is reported as EIO like the rest: the document has been
            // renamed, but the durability the flag promises was not delivered, and a write that
            // says so is retried, where one that hides it is trusted.
            DirectorySync.Flush(directory);
        }
        catch (IOException)
        {
            Delete(temporary);
            throw new NinePException(NinePError.FromErrno(Errno.EIO));
        }
        catch (UnauthorizedAccessException)
        {
            Delete(temporary);
            throw new NinePException(NinePError.FromErrno(Errno.EACCES));
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The temp file is already gone, or the directory is unwritable; either way the
            // original document is untouched, which is the guarantee that matters.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: the failure being reported is the write, not the cleanup.
        }
    }
}
