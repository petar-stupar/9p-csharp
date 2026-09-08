namespace NineP.Server;

/// <summary>A symbolic link (architecture §4).</summary>
public interface ISymlinkHandler : IHandler
{
    /// <summary>The link's target.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The text the link points at; answered to <c>Treadlink</c> and to the .u extension.</returns>
    ValueTask<string> ReadlinkAsync(CancellationToken cancellationToken = default);
}
