using System.Globalization;
using NineP.Protocol;
using NineP.Server;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>
/// One of an item's three files. <c>status</c> is the one with a vocabulary: it accepts
/// <c>open</c> or <c>done</c>, with an optional trailing newline, and nothing else.
/// </summary>
internal sealed class TodoItemFieldFile(
    TodoSession session,
    UserRow owner,
    ListRow list,
    ItemRow item,
    TodoField which)
    : TodoFile(session)
{
    /// <summary>The value a fresh item's <c>status</c> carries, and the one a truncation restores.</summary>
    private const string Fresh = "open";

    /// <summary>The file's qid.</summary>
    public override Qid Qid => TodoQid.ItemField(item.Id, which, item.UpdatedAt);

    /// <summary>
    /// What an <c>OTRUNC</c> open does. <c>label</c> and <c>description</c> are free text and
    /// simply become empty. <c>status</c> has a vocabulary of two and the schema's own
    /// <c>CHECK</c> forbids anything else, so its empty state is the value a new item carries,
    /// <c>open</c>; the truncation is performed, and performed at the open, so a truncating open
    /// clunked without a write leaves a defined value behind rather than the old one (reference
    /// §8 rule 27). It is not refused, because <c>echo done &gt; status</c> and
    /// <c>ninep write .../status</c> both open with <c>OTRUNC</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the truncation.</param>
    /// <returns>A task that completes when the row has been changed.</returns>
    protected internal override Task TruncateOnOpenAsync(CancellationToken cancellationToken = default) =>
        which == TodoField.Status
            ? WriteAsync(Fresh, cancellationToken)
            : base.TruncateOnOpenAsync(cancellationToken);

    /// <summary>
    /// What a <c>Twstat</c> or <c>Tsetattr</c> asking for a length of zero does. A zero-length
    /// <c>status</c> is not a value this file can hold, so the truncation is refused rather than
    /// answered with success (reference §8 rule 27); the two free-text fields become empty.
    /// </summary>
    /// <param name="cancellationToken">Cancels the truncation.</param>
    /// <returns>A task that completes when the row has been changed.</returns>
    /// <exception cref="NinePException"><c>status</c> has no zero-length value.</exception>
    protected internal override Task TruncateToZeroAsync(CancellationToken cancellationToken = default) =>
        which == TodoField.Status
            ? throw Invalid()
            : base.TruncateToZeroAsync(cancellationToken);

    /// <summary>The field's current contents.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The text the field holds.</returns>
    protected internal override async Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        ItemRow current = await Session.Store
            .FindItemAsync(owner.Id, list.Id, item.Index, cancellationToken).ConfigureAwait(false)
            ?? throw new NinePException(NinePError.FromErrno(Errno.ENOENT));

        return which switch
        {
            TodoField.Label => current.Label,
            TodoField.Description => current.Description,
            _ => current.Status,
        };
    }

    /// <summary>Replaces the field.</summary>
    /// <param name="value">The new contents.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the row has changed.</returns>
    /// <exception cref="NinePException">The value is not one this field accepts.</exception>
    protected internal override async Task WriteAsync(
        string value, CancellationToken cancellationToken = default)
    {
        string stored = which == TodoField.Status ? Status(value) : value;

        if (!await Session.Store.SetItemFieldAsync(owner.Id, item.Id, which, stored, cancellationToken)
            .ConfigureAwait(false))
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOENT));
        }
    }

    /// <summary>The two words <c>status</c> accepts, with an optional trailing newline.</summary>
    /// <param name="value">What the client wrote.</param>
    /// <returns>The word to store.</returns>
    /// <exception cref="NinePException">The value is not one of the two.</exception>
    private static string Status(string value)
    {
        string trimmed = value.EndsWith('\n') ? value[..^1] : value;

        return trimmed is "open" or "done"
            ? trimmed
            : throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
    }
}
