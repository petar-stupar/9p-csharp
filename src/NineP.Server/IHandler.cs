using NineP.Protocol;

namespace NineP.Server;

/// <summary>
/// The operations every file type answers (architecture §4). A handler never sees a T-message, a
/// tag, a fid, a dialect or an error shape: the core owns all of that, which is what keeps one
/// handler correct in all three dialects.
/// </summary>
public interface IHandler
{
    /// <summary>The qid of this file; stable for the life of the file (reference §4.1).</summary>
    Qid Qid { get; }

    /// <summary>The unified attributes; the core projects them into the dialect's shape.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The attributes.</returns>
    ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies a partial update; a field this handler cannot change throws EOPNOTSUPP.</summary>
    /// <param name="update">The fields to change; a null member is "do not touch".</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the change has been made.</returns>
    /// <exception cref="NinePException">The update names something this handler cannot change.</exception>
    ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default);

    /// <summary>Notifies the handler when one fid is clunked; other fids may still reference it.</summary>
    /// <param name="wasOpen">True when this fid had been opened.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the handler has been released.</returns>
    ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default);

    /// <summary>Flushes to stable storage.</summary>
    /// <param name="dataOnly">True to commit data without metadata, mirroring <c>Tfsync.datasync</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the data is on stable storage.</returns>
    ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default);
}
