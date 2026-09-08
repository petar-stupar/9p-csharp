using NineP.Protocol;

namespace NineP.TodoFs.Storage;

/// <summary>
/// The qid derivation of the workspace architecture §7: <c>path = (tableTag &lt;&lt; 56) | rowId</c>
/// and <c>version = updated_at</c> truncated to 32 bits. A qid is therefore stable across
/// restarts — it is derived from the row, not from a counter in this process — and it changes
/// exactly when the row does (reference §4.1).
/// </summary>
internal static class TodoQid
{
    /// <summary>The table tag of a row of <c>users</c>.</summary>
    public const long UsersTag = 1;

    /// <summary>The table tag of a row of <c>lists</c>.</summary>
    public const long ListsTag = 2;

    /// <summary>The table tag of a row of <c>items</c>.</summary>
    public const long ItemsTag = 3;

    /// <summary>
    /// The table tag of the fixed nodes — <c>/</c>, <c>/users</c>,
    /// <c>/users/ctl</c> — and of the item field files, whose low bits encode which field.
    /// </summary>
    public const long SyntheticTag = 4;

    private const int TagShift = 56;
    private const long RowMask = (1L << TagShift) - 1;

    /// <summary>How far a row id is shifted to leave room for the field number in the low bits.</summary>
    private const int FieldShift = 4;

    /// <summary>The low-bit slot a list's <c>name</c> file takes; the three item fields take 1..3.</summary>
    private const long ListNameSlot = 8;

    /// <summary>Builds the qid of one row.</summary>
    /// <param name="tableTag">Which table the row is in.</param>
    /// <param name="rowId">The row's primary key, or the synthetic node's number.</param>
    /// <param name="updatedAt">The row's <c>updated_at</c>, in Unix seconds.</param>
    /// <param name="type">The qid type byte.</param>
    /// <returns>The qid.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The row id does not fit 56 bits.</exception>
    public static Qid For(long tableTag, long rowId, long updatedAt, QidType type)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rowId, RowMask);

        return new Qid(type, unchecked((uint)updatedAt), PathOf(tableTag, rowId));
    }

    /// <summary>The qid path of one row, without its version.</summary>
    /// <param name="tableTag">Which table the row is in.</param>
    /// <param name="rowId">The row's primary key.</param>
    /// <returns>The path.</returns>
    public static ulong PathOf(long tableTag, long rowId) =>
        ((ulong)tableTag << TagShift) | (ulong)(rowId & RowMask);

    /// <summary>
    /// The qid of one of the tree's fixed nodes. Their numbers are below 16, which is the region
    /// the field files below leave free: a row id is at least 1 and is shifted left by four.
    /// </summary>
    /// <param name="node">The node's number.</param>
    /// <param name="type">The qid type byte.</param>
    /// <returns>The qid.</returns>
    public static Qid Fixed(int node, QidType type) => For(SyntheticTag, node, 0, type);

    /// <summary>The qid of a list's <c>name</c> file.</summary>
    /// <param name="listId">The list's row id.</param>
    /// <param name="updatedAt">The list's <c>updated_at</c>.</param>
    /// <returns>The qid.</returns>
    public static Qid ListName(long listId, long updatedAt) =>
        For(SyntheticTag, (listId << FieldShift) | ListNameSlot, updatedAt, QidType.QTFILE);

    /// <summary>The qid of one of an item's three field files.</summary>
    /// <param name="itemId">The item's row id.</param>
    /// <param name="field">Which field.</param>
    /// <param name="updatedAt">The item's <c>updated_at</c>.</param>
    /// <returns>The qid.</returns>
    public static Qid ItemField(long itemId, TodoField field, long updatedAt) =>
        For(SyntheticTag, (itemId << FieldShift) | ((long)field + 1), updatedAt, QidType.QTFILE);
}
