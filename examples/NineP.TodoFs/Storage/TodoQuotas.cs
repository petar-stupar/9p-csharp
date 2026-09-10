namespace NineP.TodoFs.Storage;

/// <summary>
/// The storage quotas of ticket 015 (E2): how many lists one user may hold and how many items one
/// list may hold. The store enforces both inside the writer transaction of the create, so the
/// count and the insert are one atomic step and two concurrent creates at the cap cannot both
/// pass; the handler layer never pre-checks them.
/// </summary>
internal sealed record TodoQuotas
{
    /// <summary>The default for <c>--max-lists</c>.</summary>
    public const int DefaultMaxLists = 1000;

    /// <summary>The default for <c>--max-items</c>.</summary>
    public const int DefaultMaxItems = 10_000;

    /// <summary>The most lists one user may hold; a <c>mkdir</c> past it is <c>ENOSPC</c>.</summary>
    public int MaxLists { get; init; } = DefaultMaxLists;

    /// <summary>The most items one list may hold; a <c>mkdir</c> past it is <c>ENOSPC</c>.</summary>
    public int MaxItems { get; init; } = DefaultMaxItems;
}
