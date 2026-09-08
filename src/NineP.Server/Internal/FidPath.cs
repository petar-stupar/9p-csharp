namespace NineP.Server.Internal;

/// <summary>A shared path edge; namespace operations synchronize updates across fid aliases.</summary>
/// <param name="Directory">The parent directory reached by ascending this edge.</param>
/// <param name="Name">The child's name in the parent.</param>
/// <param name="Previous">The parent's ancestry, or null at the attach root.</param>
/// <param name="ChildPath">The stable qid path of the child.</param>
internal sealed record FidPath(IDirectoryHandler Directory, string Name, FidPath? Previous, ulong ChildPath)
{
    /// <summary>The parent reached by ascending this edge.</summary>
    public IDirectoryHandler Directory { get; set; } = Directory;

    /// <summary>The current child name.</summary>
    public string Name { get; set; } = Name;

    /// <summary>An attach boundary to ascend to when the real parent is outside that attach.</summary>
    public IDirectoryHandler? AscendTo { get; set; }

    /// <summary>The parent's current ancestry.</summary>
    public FidPath? Previous { get; set; } = Previous;
}
