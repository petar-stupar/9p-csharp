using System.Collections.Concurrent;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;

namespace NineP.Server.Internal;

/// <summary>
/// The open rules of reference §4.5 and §5.5, and the one piece of state they need that outlives a
/// connection: <c>DMEXCL</c> means one open fid <b>across all clients</b>, so the registry belongs
/// to the server rather than to a session.
/// </summary>
internal sealed class OpenState
{
    /// <summary>Live namespace ancestry shared across connections.</summary>
    public PathState Paths { get; } = new();

    private readonly ConcurrentDictionary<ulong, byte> _exclusive = new();
    private readonly Dictionary<ulong, FileGate> _fileGates = [];

    /// <summary>Serializes size-changing operations on a file across fids and connections.</summary>
    /// <param name="path">The file's stable qid path.</param>
    /// <param name="cancellationToken">Cancels waiting for another operation.</param>
    /// <returns>A lease keeping append's size lookup and write atomic.</returns>
    public async ValueTask<IDisposable> AcquireFileAsync(ulong path, CancellationToken cancellationToken)
    {
        FileGate gate;
        lock (_fileGates)
        {
            if (!_fileGates.TryGetValue(path, out gate!))
            {
                gate = new FileGate();
                _fileGates.Add(path, gate);
            }

            gate.Users++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new FileLease(this, path, gate);
        }
        catch
        {
            ReturnFile(path, gate, acquired: false);
            throw;
        }
    }

    private void ReturnFile(ulong path, FileGate gate, bool acquired)
    {
        lock (_fileGates)
        {
            if (acquired)
            {
                gate.Semaphore.Release();
            }

            if (--gate.Users == 0)
            {
                _fileGates.Remove(path);
                gate.Semaphore.Dispose();
            }
        }
    }

    private sealed class FileGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class FileLease(OpenState owner, ulong path, FileGate gate) : IDisposable
    {
        public void Dispose() => owner.ReturnFile(path, gate, acquired: true);
    }

    /// <summary>Decodes a 9P2000 / .u <c>Topen.mode</c> byte.</summary>
    /// <param name="mode">The byte the client sent.</param>
    /// <returns>The access mode and the flags it carries.</returns>
    /// <exception cref="NinePException">The byte sets a bit reference §4.5 does not define.</exception>
    public static (OpenMode Mode, OpenFlags Flags) Decode(byte mode) =>
        ModeBits.TryFromOpenByte(mode, out OpenMode access, out OpenFlags flags)
            ? (access, flags)
            : throw new NinePException(NinePError.FromEname("bad open mode"));

    /// <summary>Decodes a .L <c>Tlopen.flags</c> word; the flags it does not name are ignored.</summary>
    /// <param name="flags">The word the client sent.</param>
    /// <returns>The access mode and the flags this workspace honours.</returns>
    public static (OpenMode Mode, OpenFlags Flags) DecodeLinux(uint flags)
    {
        ModeBits.FromLinuxFlags(flags, out OpenMode access, out OpenFlags honoured);
        return (access, honoured);
    }

    /// <summary>
    /// Checks that this fid may be opened at all: reference §5.5 forbids opening an already-open
    /// fid, a directory may only be opened for reading or searching, and reference §8 rule 23
    /// forbids the shape this used to allow — an <c>Ropen</c> or <c>Rlopen</c> with no open file
    /// behind it, whose every <c>Tread</c> then answered zero bytes as if the file were empty. A
    /// symbolic link is <c>ELOOP</c>, which is what <c>O_NOFOLLOW</c> means as well; a fifo, socket
    /// or device this server cannot open is <c>ENXIO</c>; and <c>O_DIRECTORY</c> on anything but a
    /// directory is <c>ENOTDIR</c>.
    /// </summary>
    /// <param name="entry">The fid being opened.</param>
    /// <param name="attr">The file's attributes.</param>
    /// <param name="mode">The access mode asked for.</param>
    /// <param name="flags">The flags asked for.</param>
    /// <exception cref="NinePException">The open is not allowed.</exception>
    public static void Validate(FidEntry entry, Attr attr, OpenMode mode, OpenFlags flags)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(attr);

        if (entry.State == FidState.Open)
        {
            throw new NinePException(NinePError.FromEname("bad open mode"));
        }

        if (entry.IsAuth)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EPERM));
        }

        // Rule 23: opening a symbolic link is ELOOP whether or not the client asked for
        // O_NOFOLLOW — 9P resolves nothing on the client's behalf, so there is no other file for
        // the open to land on. Either half is enough to recognise one: a tree may report the kind
        // without implementing ISymlinkHandler, and Treadlink then answers EINVAL.
        if (attr.Kind == FileKind.Symlink || entry.Handler is ISymlinkHandler)
        {
            throw new NinePException(NinePError.FromErrno(Errno.ELOOP));
        }

        // Rule 23 and rule 12: O_DIRECTORY is decoded, so it is also honoured.
        if (flags.HasFlag(OpenFlags.Directory) && attr.Kind != FileKind.Directory)
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOTDIR));
        }

        if (attr.Kind != FileKind.Directory)
        {
            // Rule 23: a handler that is not an IFileHandler has no open file to give, and a
            // success reply with nothing behind it is never sent.
            if (entry.Handler is not IFileHandler)
            {
                throw new NinePException(NinePError.FromErrno(Errno.ENXIO));
            }

            return;
        }

        // open(5): a directory may only be read or searched, and truncating or removing one
        // through an open flag is refused rather than quietly ignored.
        if (mode is not (OpenMode.Read or OpenMode.Exec))
        {
            throw new NinePException(NinePError.FromErrno(Errno.EISDIR));
        }

        if (flags.HasFlag(OpenFlags.Truncate) || flags.HasFlag(OpenFlags.RemoveOnClose))
        {
            throw new NinePException(NinePError.FromErrno(Errno.EISDIR));
        }
    }

    /// <summary>
    /// Takes the exclusive-use lock a <c>DMEXCL</c> file carries. Reference §5.5 makes it one open
    /// fid at a time across every client, so a second open fails rather than queues.
    /// </summary>
    /// <param name="attr">The file's attributes.</param>
    /// <exception cref="NinePException">The file is already open somewhere.</exception>
    public void Acquire(Attr attr)
    {
        ArgumentNullException.ThrowIfNull(attr);

        if (!attr.Flags.HasFlag(FileFlags.Exclusive))
        {
            return;
        }

        if (!_exclusive.TryAdd(attr.Qid.Path, 0))
        {
            throw new NinePException(NinePError.FromEname("file exists"));
        }
    }

    /// <summary>Releases the exclusive-use lock when the open fid is clunked.</summary>
    /// <param name="entry">The fid being released.</param>
    /// <param name="exclusive">True when this fid held the lock.</param>
    public void Release(FidEntry entry, bool exclusive)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (exclusive)
        {
            _exclusive.TryRemove(entry.Handler.Qid.Path, out _);
        }
    }

    /// <summary>True when the file behind a qid is currently held exclusively.</summary>
    /// <param name="path">The qid path.</param>
    /// <returns>True when some fid holds the exclusive-use lock.</returns>
    public bool IsHeldExclusively(ulong path) => _exclusive.ContainsKey(path);
}
