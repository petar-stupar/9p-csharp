using System.Text;
using NineP.Protocol;
using NineP.Server;

namespace NineP.TodoFs;

/// <summary>
/// A file of the todofs tree whose contents are one column of one row. Writes are a splice into
/// the current value followed by a whole-value replace, so a client that writes in chunks and one
/// that writes at once produce the same row — and both are bounded at
/// <see cref="Storage.TodoStore.MaxFieldBytes"/>, because a field is a database column and not a
/// place to park a megabyte.
/// <para>
/// Reads answer from the <b>row</b>, never from the splice buffer: what a write left behind is not
/// always what the file holds — <c>status</c> stores <c>done</c> for a written <c>done\n</c>, and
/// <c>/users/ctl</c> reads back the user list rather than the command that changed it.
/// </para>
/// <para>
/// A truncating open performs the truncation when the open is answered rather than deferring it to
/// a write that may never arrive (reference §8 rule 27): see <see cref="TruncateOnOpenAsync"/>.
/// </para>
/// </summary>
internal abstract class TodoFile : TodoNode, IFileHandler
{
    /// <summary>Creates a file node.</summary>
    /// <param name="session">The attach's state.</param>
    protected TodoFile(TodoSession session)
        : base(session)
    {
    }

    /// <summary>Every file of this tree is a regular file.</summary>
    protected override FileKind Kind => FileKind.File;

    /// <summary>The attributes, with the length the row currently holds.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The attributes.</returns>
    public override async ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default)
    {
        Attr attr = await base.GetAttrAsync(cancellationToken).ConfigureAwait(false);
        ulong size = await SizeAsync(cancellationToken).ConfigureAwait(false);

        return attr with { Size = size, Blocks = (size + 511) / 512 };
    }

    /// <summary>
    /// Applies an update whole or refuses it whole (reference §8 rule 27). The length is the only
    /// field a todofs file has — everything else is derived — and a database column cannot be
    /// padded out to a length, so a length of zero is the one update that is representable at all.
    /// Where the empty value is not one the file may hold, <see cref="TruncateToZeroAsync"/>
    /// refuses it rather than answering success and changing nothing.
    /// </summary>
    /// <param name="update">The fields to change.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the change has been made.</returns>
    /// <exception cref="NinePException">The update names something this file cannot change.</exception>
    public override async ValueTask SetAttrAsync(
        SetAttr update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();

        // A wstat that changes nothing is a request to reach stable storage (reference §4.2).
        if (update.IsFsyncRequest)
        {
            return;
        }

        if (NamesADerivedField(update) || update.Size is not 0)
        {
            throw Unsupported();
        }

        await TruncateToZeroAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The length a <c>stat</c> reports. It is a hook of its own because a file that may not be
    /// read by this identity still has to answer a <c>stat</c>: the core stats a file before it
    /// opens it, so computing the length by reading it would refuse the open as well.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The length in bytes.</returns>
    protected virtual async Task<ulong> SizeAsync(CancellationToken cancellationToken) =>
        (ulong)TodoText.Utf8.GetByteCount(await ReadAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Opens the file; the core has already checked permissions. <c>OTRUNC</c> is performed here,
    /// before the open is answered, so that an open clunked without a write leaves the file in the
    /// state the truncation asked for rather than untouched (reference §8 rule 27).
    /// </summary>
    /// <param name="mode">The access mode.</param>
    /// <param name="flags">The flags accompanying the open.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open instance.</returns>
    /// <exception cref="NinePException">This identity may not open the file that way.</exception>
    public virtual async ValueTask<IOpenFile> OpenAsync(
        OpenMode mode, OpenFlags flags, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        OpenField open = new(this);

        try
        {
            if (flags.HasFlag(OpenFlags.Truncate))
            {
                await open.TruncateAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await open.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return open;
    }

    /// <summary>The file's current contents.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The text the file holds.</returns>
    protected internal abstract Task<string> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the file's contents.</summary>
    /// <param name="value">The whole new value.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the row has been changed.</returns>
    /// <exception cref="NinePException">The value is not one this file accepts.</exception>
    protected internal abstract Task WriteAsync(string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// What an <c>OTRUNC</c> open does to the row, performed while the open is being answered.
    /// A free-text field simply becomes empty; a file whose value comes from a vocabulary
    /// overrides this, because emptying it would store a value the file forbids.
    /// </summary>
    /// <param name="cancellationToken">Cancels the truncation.</param>
    /// <returns>A task that completes when the row has been changed.</returns>
    /// <exception cref="NinePException">This file cannot be truncated by an open.</exception>
    protected internal virtual Task TruncateOnOpenAsync(CancellationToken cancellationToken = default) =>
        WriteAsync(string.Empty, cancellationToken);

    /// <summary>
    /// What a <c>Twstat</c> or <c>Tsetattr</c> asking for a length of zero does to the row. A
    /// free-text field becomes empty; a file that has no zero-length value refuses it rather than
    /// answering success for a truncation it did not perform (reference §8 rule 27).
    /// </summary>
    /// <param name="cancellationToken">Cancels the truncation.</param>
    /// <returns>A task that completes when the row has been changed.</returns>
    /// <exception cref="NinePException">This file has no zero-length value.</exception>
    protected internal virtual Task TruncateToZeroAsync(CancellationToken cancellationToken = default) =>
        WriteAsync(string.Empty, cancellationToken);

    /// <summary>
    /// One open of one field. The bytes a write splices into are the file's, not this open's
    /// (<see cref="TodoFieldStates"/>): a client may keep several writes outstanding on one fid and
    /// they arrive in an arbitrary order, so a write merges into a buffer rather than into the row
    /// — and that buffer is shared by every open of the field, so two opens are two views of one
    /// file rather than two files that overwrite each other. Reads never consult that buffer: the
    /// row is what the file holds, and it is what every reader is told.
    /// </summary>
    private sealed class OpenField : IOpenFile
    {
        private readonly TodoFile _file;
        private readonly TodoFieldStates _states;
        private readonly TodoFieldState _state;

        public OpenField(TodoFile file)
        {
            _file = file;
            _states = file.Session.Fields;
            _state = _states.Acquire(file.Qid.Path);
        }

        /// <summary>
        /// Performs the <c>OTRUNC</c> of an open: the row is changed now, and the writers' splice
        /// buffer is emptied with it, so a write through this open — or through one that was
        /// already open — starts from nothing (open(5)).
        /// </summary>
        /// <param name="cancellationToken">Cancels the truncation.</param>
        /// <returns>A task that completes when the row has been changed.</returns>
        public async Task TruncateAsync(CancellationToken cancellationToken)
        {
            await _state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await _file.TruncateOnOpenAsync(cancellationToken).ConfigureAwait(false);
                _state.Value = [];
            }
            finally
            {
                _state.Gate.Release();
            }
        }

        public async ValueTask<int> ReadAsync(
            ulong offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                byte[] bytes = await StoredAsync(cancellationToken).ConfigureAwait(false);
                if (offset >= (ulong)bytes.Length)
                {
                    return 0;
                }

                int at = (int)offset;
                int count = Math.Min(bytes.Length - at, buffer.Length);
                bytes.AsSpan(at, count).CopyTo(buffer.Span);
                return count;
            }
            finally
            {
                _state.Gate.Release();
            }
        }

        public async ValueTask<int> WriteAsync(
            ulong offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (data.IsEmpty)
            {
                return 0;
            }

            // §8.3: a field is capped at 64 KiB, and a write that would cross the cap is refused
            // rather than silently truncated.
            if (offset > (ulong)Storage.TodoStore.MaxFieldBytes
                || (ulong)data.Length > (ulong)Storage.TodoStore.MaxFieldBytes - offset)
            {
                throw new NinePException(NinePError.FromErrno(Errno.EFBIG));
            }

            await _state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                byte[] current = await BaseAsync(cancellationToken).ConfigureAwait(false);
                int end = (int)offset + data.Length;
                byte[] merged = new byte[Math.Max(current.Length, end)];

                current.CopyTo(merged, 0);
                data.Span.CopyTo(merged.AsSpan((int)offset));

                string text;
                try
                {
                    text = TodoText.Utf8.GetString(merged);
                }
                catch (DecoderFallbackException)
                {
                    // A column is text, so bytes that are not UTF-8 cannot become one.
                    throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
                }

                await _file.WriteAsync(text, cancellationToken).ConfigureAwait(false);
                _state.Value = merged;
                return data.Length;
            }
            finally
            {
                _state.Gate.Release();
            }
        }

        public async ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default)
        {
            await _state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return (ulong)(await StoredAsync(cancellationToken).ConfigureAwait(false)).Length;
            }
            finally
            {
                _state.Gate.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            _states.Release(_state);
            return ValueTask.CompletedTask;
        }

        /// <summary>The file's contents, which are the row's and nothing else.</summary>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The bytes the file holds.</returns>
        private async Task<byte[]> StoredAsync(CancellationToken cancellationToken) =>
            TodoText.Utf8.GetBytes(await _file.ReadAsync(cancellationToken).ConfigureAwait(false));

        /// <summary>The bytes this write splices into: what the open writers agreed on last.</summary>
        /// <param name="cancellationToken">Cancels the load.</param>
        /// <returns>The splice base.</returns>
        private async Task<byte[]> BaseAsync(CancellationToken cancellationToken) =>
            _state.Value ??= await StoredAsync(cancellationToken).ConfigureAwait(false);
    }
}
