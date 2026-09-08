using NineP.Protocol;

namespace NineP.Server;

/// <summary>
/// A directory (architecture §4). Every name reaching these methods has already been validated
/// against reference §8 rule 3 by the core, so a handler never has to defend against "..", "/" or
/// a NUL byte in a name.
/// </summary>
public interface IDirectoryHandler : IHandler
{
    /// <summary>Resolves one path element.</summary>
    /// <param name="name">The element; never ".", ".." or a name containing '/'.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The child's handler, or null when the name does not exist.</returns>
    ValueTask<IHandler?> LookupAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Returns a page of entries starting at a cursor.</summary>
    /// <param name="cursor">Where to resume; 0 starts the listing.</param>
    /// <param name="max">The most entries the core can use.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The entries, the cursor after them, and whether the directory ended.</returns>
    ValueTask<DirectoryListing> ReadDirAsync(
        ulong cursor, int max, CancellationToken cancellationToken = default);

    /// <summary>Creates a child of any kind (reference §5.5).</summary>
    /// <param name="request">Everything the five create messages carry, unified.</param>
    /// <param name="cancellationToken">Cancels the create.</param>
    /// <returns>The new child's handler.</returns>
    /// <exception cref="NinePException">The child could not be created.</exception>
    ValueTask<IHandler> CreateAsync(
        CreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes a child.</summary>
    /// <param name="name">The child's name.</param>
    /// <param name="kind">What the core resolved the child to.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns>A task that completes when the child is gone.</returns>
    /// <exception cref="NinePException">The child could not be removed.</exception>
    ValueTask RemoveAsync(string name, FileKind kind, CancellationToken cancellationToken = default);

    /// <summary>Renames a child into another directory of the same filesystem.</summary>
    /// <param name="oldName">The child's name here.</param>
    /// <param name="newParent">The directory to move it into; may be this one.</param>
    /// <param name="newName">The name it takes there.</param>
    /// <param name="cancellationToken">Cancels the rename.</param>
    /// <returns>A task that completes when the child has moved.</returns>
    /// <exception cref="NinePException">The rename could not be performed.</exception>
    ValueTask RenameAsync(
        string oldName,
        IDirectoryHandler newParent,
        string newName,
        CancellationToken cancellationToken = default);
}
