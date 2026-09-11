using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Server;
using NineP.TestSupport;

namespace NineP.Benchmarks;

/// <summary>
/// A tree of exactly one file, <c>/stream</c>, whose reads are zeros and whose writes are counted
/// and discarded. Benchmark (a) of architecture §9 measures the framing, the transport and the
/// client window; a handler that kept a gibibyte in a <c>byte[]</c> would measure the array
/// instead, and would put that gibibyte into the peak RSS of benchmark (c).
/// </summary>
internal sealed class SyntheticFilesystem(ulong length) : IFilesystem, IDirectoryHandler
{
    private static readonly TimeSpec Epoch = new(0, 0);

    private readonly SyntheticFile _file = new(length);

    /// <summary>The server's independent write count.</summary>
    public long BytesWritten => _file.BytesWritten;

    /// <summary>The directory's qid; path 1, because the file takes path 2.</summary>
    public Qid Qid => new(QidType.QTDIR, 0, 1);

    /// <summary>Every attach lands on the same root, whoever asks.</summary>
    /// <param name="identity">Ignored; the benchmark server authenticates nobody.</param>
    /// <param name="aname">Ignored.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The root directory.</returns>
    public ValueTask<IDirectoryHandler> AttachAsync(
        Identity identity, string aname, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IDirectoryHandler>(this);

    /// <summary>The root's attributes.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The attributes.</returns>
    public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new Attr
        {
            Qid = Qid,
            Kind = FileKind.Directory,
            Perm = Perms.P0777,
            UserName = "bench",
            GroupName = "bench",
            ATime = Epoch,
            MTime = Epoch,
            CTime = Epoch,
        });

    /// <summary>Nothing about this tree can be changed.</summary>
    /// <param name="update">Ignored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A failed task.</returns>
    public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EROFS));

    /// <summary>Clunking the root costs nothing.</summary>
    /// <param name="wasOpen">Ignored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A completed task.</returns>
    public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <summary>There is nothing to flush.</summary>
    /// <param name="dataOnly">Ignored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A completed task.</returns>
    public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <summary>Resolves the single name this tree has.</summary>
    /// <param name="name">The entry to look up.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The file, or null.</returns>
    public ValueTask<IHandler?> LookupAsync(string name, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IHandler?>(
            string.Equals(name, SyntheticFile.Name, StringComparison.Ordinal) ? _file : null);

    /// <summary>Lists the one entry.</summary>
    /// <param name="cursor">Where the listing resumes.</param>
    /// <param name="max">How many bytes the reply may carry; unused, one entry always fits.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The listing.</returns>
    public ValueTask<DirectoryListing> ReadDirAsync(
        ulong cursor, int max, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(cursor == 0
            ? new DirectoryListing([new DirEntry(SyntheticFile.Name, _file.Qid, FileKind.File, 1)], 1, true)
            : new DirectoryListing([], cursor, true));

    /// <summary>Nothing may be created here.</summary>
    /// <param name="request">Ignored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A failed task.</returns>
    public ValueTask<IHandler> CreateAsync(
        CreateRequest request, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EROFS));

    /// <summary>Nothing may be removed here.</summary>
    /// <param name="name">Ignored.</param>
    /// <param name="kind">Ignored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A failed task.</returns>
    public ValueTask RemoveAsync(string name, FileKind kind, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EROFS));

    /// <summary>Nothing may be renamed here.</summary>
    /// <param name="oldName">Ignored.</param>
    /// <param name="newParent">Ignored.</param>
    /// <param name="newName">Ignored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A failed task.</returns>
    public ValueTask RenameAsync(
        string oldName,
        IDirectoryHandler newParent,
        string newName,
        CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EROFS));
}

/// <summary>The one file of the synthetic tree: a stream of zeros of a fixed length.</summary>
internal sealed class SyntheticFile(ulong length) : IFileHandler
{
    /// <summary>The name the tree exposes it under.</summary>
    public const string Name = "stream";

    private static readonly TimeSpec Epoch = new(0, 0);

    /// <summary>The file's qid.</summary>
    public Qid Qid => new(QidType.QTFILE, 0, 2);

    private long _bytesWritten;

    /// <summary>How many bytes writes have delivered since the process started.</summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>Records a write; the bytes themselves are discarded.</summary>
    /// <param name="count">How many bytes arrived.</param>
    public void Wrote(int count) => Interlocked.Add(ref _bytesWritten, count);

    /// <summary>The file's attributes.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The attributes.</returns>
    public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new Attr
        {
            Qid = Qid,
            Kind = FileKind.File,
            Perm = Perms.P0666,
            Size = length,
            Blocks = (length + 511) / 512,
            UserName = "bench",
            GroupName = "bench",
            ATime = Epoch,
            MTime = Epoch,
            CTime = Epoch,
        });

    /// <summary>A benchmark file has nothing to set.</summary>
    /// <param name="update">Ignored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A completed task.</returns>
    public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <summary>Clunking costs nothing.</summary>
    /// <param name="wasOpen">Ignored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A completed task.</returns>
    public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <summary>There is nothing to flush.</summary>
    /// <param name="dataOnly">Ignored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A completed task.</returns>
    public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <summary>Opens the file; the same instance serves every open.</summary>
    /// <param name="mode">Ignored.</param>
    /// <param name="flags">Ignored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>This instance.</returns>
    // CA2000: the contract of IFileHandler.OpenAsync is that the core disposes the open instance
    // when the fid is clunked, which is exactly what the memory tree in tests/ relies on too.
#pragma warning disable CA2000
    public ValueTask<IOpenFile> OpenAsync(
        OpenMode mode, OpenFlags flags, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IOpenFile>(new SyntheticOpenFile(this, length));
#pragma warning restore CA2000

    /// <summary>One open instance of the synthetic file.</summary>
    private sealed class SyntheticOpenFile(SyntheticFile file, ulong length) : IOpenFile
    {
        /// <summary>Fills the buffer with zeros up to the end of the file.</summary>
        /// <param name="offset">Where the read starts.</param>
        /// <param name="buffer">Where the bytes go.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>How many bytes were produced; 0 at end of file.</returns>
        public ValueTask<int> ReadAsync(
            ulong offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (offset >= length)
            {
                return ValueTask.FromResult(0);
            }

            int count = (int)Math.Min((ulong)buffer.Length, length - offset);
            buffer.Span[..count].Clear();
            return ValueTask.FromResult(count);
        }

        /// <summary>Counts the bytes and discards them.</summary>
        /// <param name="offset">Ignored.</param>
        /// <param name="data">The bytes the client sent.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        /// <returns>How many bytes were accepted, which is all of them.</returns>
        public ValueTask<int> WriteAsync(
            ulong offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            file.Wrote(data.Length);
            return ValueTask.FromResult(data.Length);
        }

        /// <summary>The file's length.</summary>
        /// <param name="cancellationToken">Cancels the call.</param>
        /// <returns>The length.</returns>
        public ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(length);

        /// <summary>Closing an open file costs nothing.</summary>
        /// <returns>A completed task.</returns>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
