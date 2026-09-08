namespace NineP.TodoFs.Storage;

/// <summary>One row of the <c>users</c> table.</summary>
/// <param name="Id">The row id, which is half of every qid path under this user.</param>
/// <param name="Name">The user name, which is what the authenticator proved.</param>
/// <param name="CreatedAt">Unix seconds at creation.</param>
/// <param name="UpdatedAt">Unix seconds at the last change; the qid version.</param>
internal readonly record struct UserRow(long Id, string Name, long CreatedAt, long UpdatedAt);
