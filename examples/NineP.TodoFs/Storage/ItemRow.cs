namespace NineP.TodoFs.Storage;

/// <summary>One row of the <c>items</c> table.</summary>
/// <param name="Id">The row id.</param>
/// <param name="ListId">The owning list.</param>
/// <param name="Index">The item's number, which is its directory name.</param>
/// <param name="Label">The item's <c>label</c> file.</param>
/// <param name="Description">The item's <c>description</c> file.</param>
/// <param name="Status">The item's <c>status</c> file: <c>open</c> or <c>done</c>.</param>
/// <param name="CreatedAt">Unix seconds at creation.</param>
/// <param name="UpdatedAt">Unix seconds at the last change; the qid version.</param>
internal readonly record struct ItemRow(
    long Id,
    long ListId,
    long Index,
    string Label,
    string Description,
    string Status,
    long CreatedAt,
    long UpdatedAt);
