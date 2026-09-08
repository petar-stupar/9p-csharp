using NineP.Protocol;

namespace NineP.Server;

/// <summary>A regular file (architecture §4).</summary>
public interface IFileHandler : IHandler
{
    /// <summary>Opens the file; the core has already checked permissions and the open state.</summary>
    /// <param name="mode">The access mode, taken from the low two bits of the request.</param>
    /// <param name="flags">The flags accompanying the open.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open instance; the core disposes it when the fid is clunked.</returns>
    /// <exception cref="NinePException">The file could not be opened.</exception>
    ValueTask<IOpenFile> OpenAsync(
        OpenMode mode, OpenFlags flags, CancellationToken cancellationToken = default);
}
