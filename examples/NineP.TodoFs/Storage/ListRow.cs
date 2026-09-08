namespace NineP.TodoFs.Storage;

/// <summary>One row of the <c>lists</c> table.</summary>
/// <param name="Id">The row id.</param>
/// <param name="UserId">The owning user; every query is scoped by it.</param>
/// <param name="Index">The list's number, which is its directory name.</param>
/// <param name="Name">The list's name, which is its <c>name</c> file.</param>
/// <param name="CreatedAt">Unix seconds at creation.</param>
/// <param name="UpdatedAt">Unix seconds at the last change; the qid version.</param>
internal readonly record struct ListRow(
    long Id, long UserId, long Index, string Name, long CreatedAt, long UpdatedAt);
