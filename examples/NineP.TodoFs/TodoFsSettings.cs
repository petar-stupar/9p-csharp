namespace NineP.TodoFs;

/// <summary>The settings the tree needs that are not per-attach.</summary>
/// <param name="AdminRole">The realm role that may use <c>/users/ctl</c>.</param>
internal sealed record TodoFsSettings(string AdminRole = "todofs-admin");
