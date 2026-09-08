using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using NineP.Protocol;

namespace NineP.JsonFs;

/// <summary>
/// Every change to the served document goes through here. Two reasons: read-only is the default
/// and one refusal site is easier to trust than a dozen, and <c>--write-back</c> has to rewrite
/// the file after each mutation, which is one place too.
/// </summary>
internal sealed class JsonFsMutator
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

    /// <summary>Creates the mutator for one served document.</summary>
    /// <param name="tree">The document.</param>
    /// <param name="writable">True to accept changes at all.</param>
    /// <param name="writeBackPath">Where to rewrite the document; null to keep changes in memory.</param>
    public JsonFsMutator(JsonTree tree, bool writable, string? writeBackPath)
    {
        ArgumentNullException.ThrowIfNull(tree);

        _tree = tree;
        _writable = writable;
        _writeBackPath = writeBackPath;
    }

    /// <summary>True when this server accepts changes.</summary>
    public bool IsWritable => _writable;

    /// <summary>Refuses the caller when the server is read-only.</summary>
    /// <exception cref="NinePException">The server is read-only.</exception>
    public void RequireWritable()
    {
        if (!_writable)
        {
            throw new NinePException(ReadOnly);
        }
    }

    /// <summary>Runs one change under the tree's lock and writes the document back after it.</summary>
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

    /// <summary>Rewrites the document, which is what an <c>fsync</c> on any node asks for.</summary>
    public void Flush()
    {
        if (_writeBackPath is null)
        {
            return;
        }

        lock (_tree.Gate)
        {
            WriteBack();
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
