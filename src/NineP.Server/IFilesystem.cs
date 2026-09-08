using NineP.Protocol;
using NineP.Protocol.Auth;

namespace NineP.Server;

/// <summary>
/// The tree a server serves (architecture §4): one root directory per authenticated attach. This
/// is the only interface a developer must implement, and it is handed the identity the
/// authenticator produced, never the <c>uname</c> the client claimed.
/// </summary>
public interface IFilesystem
{
    /// <summary>Returns the root directory for this identity and tree name.</summary>
    /// <param name="identity">Who the session runs as.</param>
    /// <param name="aname">The tree the client asked for; "" is the default tree.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The root directory handler.</returns>
    /// <exception cref="NinePException">This identity may not attach to this tree.</exception>
    ValueTask<IDirectoryHandler> AttachAsync(
        Identity identity, string aname, CancellationToken cancellationToken = default);
}
