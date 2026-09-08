using NineP.Protocol;

namespace NineP.Server;

/// <summary>
/// Optional: hard links (.L <c>Tlink</c>), implemented on the directory that receives the link.
/// </summary>
public interface ILinkCapability
{
    /// <summary>Creates a hard link to an existing file in this directory.</summary>
    /// <param name="name">The name the link takes.</param>
    /// <param name="target">The file being linked to.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the link exists.</returns>
    ValueTask LinkAsync(string name, IHandler target, CancellationToken cancellationToken = default);
}
