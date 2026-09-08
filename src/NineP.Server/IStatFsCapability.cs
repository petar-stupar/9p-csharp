using NineP.Protocol;

namespace NineP.Server;

/// <summary>
/// Optional: filesystem statistics (.L <c>Tstatfs</c>), implemented on the filesystem or on a
/// handler.
/// </summary>
public interface IStatFsCapability
{
    /// <summary>The statistics of the filesystem behind this object.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The statistics.</returns>
    ValueTask<StatFs> StatFsAsync(CancellationToken cancellationToken = default);
}
