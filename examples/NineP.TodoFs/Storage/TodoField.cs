namespace NineP.TodoFs.Storage;

/// <summary>The three writable files an item has.</summary>
internal enum TodoField
{
    /// <summary>The item's short label.</summary>
    Label = 0,

    /// <summary>The item's longer description.</summary>
    Description = 1,

    /// <summary>The item's status: <c>open</c> or <c>done</c>, and nothing else.</summary>
    Status = 2,
}
