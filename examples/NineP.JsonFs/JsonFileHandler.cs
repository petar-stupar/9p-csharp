using System.Buffers;
using System.Text;
using NineP.Protocol;
using NineP.Server;

namespace NineP.JsonFs;

/// <summary>
/// A JSON scalar as a regular file: a string's own UTF-8 bytes with nothing added, a number's
/// text, <c>true</c> / <c>false</c>, or nothing at all for <c>null</c> (architecture §7).
/// </summary>
internal sealed class JsonFileHandler : JsonNodeHandler, IFileHandler
{
    private readonly JsonScalarNode _node;

    /// <summary>Creates a handler over one scalar.</summary>
    /// <param name="node">The scalar.</param>
    /// <param name="context">The per-attach context.</param>
    /// <param name="parent">The container it lives in.</param>
    public JsonFileHandler(JsonScalarNode node, JsonFsContext context, JsonDirectoryNode? parent)
        : base(node, context, parent) => _node = node;

    /// <summary>A scalar is a regular file.</summary>
    public override FileKind Kind => FileKind.File;

    /// <summary>The length of the file's bytes.</summary>
    protected override ulong Length
    {
        get
        {
            lock (Context.Tree.Gate)
            {
                return (ulong)JsonText.ByteCount(_node.Text);
            }
        }
    }

    /// <summary>Opens the file; the core has already checked permissions.</summary>
    /// <param name="mode">The access mode.</param>
    /// <param name="flags">The flags accompanying the open.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open instance.</returns>
    /// <exception cref="NinePException">The server is read-only and the open would write.</exception>
    public ValueTask<IOpenFile> OpenAsync(
        OpenMode mode, OpenFlags flags, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool writing = mode is OpenMode.Write or OpenMode.ReadWrite;
        if (writing || flags.HasFlag(OpenFlags.Truncate))
        {
            Context.Mutator.RequireWritable();
        }

        if (flags.HasFlag(OpenFlags.Truncate))
        {
            Truncate();
        }

        // CA2000: the server core owns the open instance and disposes it when the fid is clunked.
#pragma warning disable CA2000
        return ValueTask.FromResult<IOpenFile>(new OpenScalar(_node, Context));
#pragma warning restore CA2000
    }

    /// <summary>Empties the file, which a <c>Twstat</c> to length zero and <c>OTRUNC</c> both ask for.</summary>
    /// <exception cref="NinePException">The server is read-only.</exception>
    public void Truncate() => Context.Mutator.Mutate(_node.Truncate);

    /// <summary>
    /// Empties the file inside a mutation the caller has already opened, so that a <c>Twstat</c>
    /// carrying a truncation <b>and</b> a rename applies both under one lock and writes the
    /// document back once (reference §8 rule 27).
    /// </summary>
    public void Empty() => _node.Truncate();

    /// <summary>
    /// One open scalar. A write splices into the file's current bytes and then re-assigns the
    /// value, so a client that writes in chunks and a client that writes once produce the same
    /// document.
    /// </summary>
    private sealed class OpenScalar(JsonScalarNode node, JsonFsContext context) : IOpenFile
    {
        private byte[] _suffix = [];
        private ulong _suffixOffset;
        public ValueTask<int> ReadAsync(
            ulong offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] bytes;
            lock (context.Tree.Gate)
            {
                bytes = node.ToBytes();
            }

            if (offset >= (ulong)bytes.Length)
            {
                return ValueTask.FromResult(0);
            }

            int at = (int)offset;
            int count = Math.Min(bytes.Length - at, buffer.Length);
            bytes.AsSpan(at, count).CopyTo(buffer.Span);
            return ValueTask.FromResult(count);
        }

        public ValueTask<int> WriteAsync(
            ulong offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (data.IsEmpty)
            {
                return ValueTask.FromResult(0);
            }

            // §7 and JsonTree.MaxScalarBytes: the bound is checked before anything is allocated,
            // because the size of the allocation is exactly what the offset controls.
            if (offset >= (ulong)JsonTree.MaxScalarBytes
                || (ulong)data.Length > (ulong)JsonTree.MaxScalarBytes - offset)
            {
                throw new NinePException(NinePError.FromErrno(Errno.EFBIG));
            }

            int written = data.Length;
            byte[] suffix = [];
            ulong suffixOffset = 0;
            context.Mutator.Mutate(() =>
            {
                byte[] current = node.ToBytes();
                int end = checked((int)offset + written);
                if (_suffix.Length > 0 && offset != _suffixOffset + (ulong)_suffix.Length)
                {
                    throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
                }
                byte[] merged = new byte[Math.Max(current.Length, end)];
                current.CopyTo(merged, 0);
                if (_suffix.Length > 0)
                {
                    _suffix.CopyTo(merged, (int)_suffixOffset);
                }

                data.Span.CopyTo(merged.AsSpan((int)offset));

                int valid = 0;
                while (valid < merged.Length)
                {
                    OperationStatus status = Rune.DecodeFromUtf8(merged.AsSpan(valid), out _, out int consumed);
                    if (status == OperationStatus.NeedMoreData)
                    {
                        suffix = merged.AsSpan(valid).ToArray();
                        suffixOffset = (ulong)valid;
                        break;
                    }
                    if (status != OperationStatus.Done)
                    {
                        throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
                    }

                    valid += consumed;
                }
                node.Assign(JsonText.FromBytes(merged.AsSpan(0, valid)));
            });

            _suffix = suffix;
            _suffixOffset = suffixOffset;
            return ValueTask.FromResult(written);
        }

        public ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (context.Tree.Gate)
            {
                return ValueTask.FromResult((ulong)JsonText.ByteCount(node.Text));
            }
        }

        public ValueTask DisposeAsync()
        {
            bool incomplete = _suffix.Length != 0;
            _suffix = [];
            return incomplete
                ? ValueTask.FromException(new NinePException(NinePError.FromErrno(Errno.EINVAL)))
                : ValueTask.CompletedTask;
        }
    }
}
