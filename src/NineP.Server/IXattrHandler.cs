using NineP.Protocol;

namespace NineP.Server;

/// <summary>Optional: extended attributes (.L). Absent means <c>EOPNOTSUPP</c>.</summary>
public interface IXattrHandler
{
    /// <summary>The NUL-separated name list a <c>Txattrwalk</c> with an empty name returns.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The packed list of attribute names.</returns>
    ValueTask<ReadOnlyMemory<byte>> ListXattrAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one attribute.</summary>
    /// <param name="name">The attribute's name.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The attribute's bytes.</returns>
    ValueTask<ReadOnlyMemory<byte>> GetXattrAsync(
        string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one attribute. The core calls this when a <c>Txattrcreate</c> fid is clunked and the
    /// <c>attr_size</c> it promised was non-zero; a zero-length value never reaches here, because
    /// that request is a removal (see <see cref="RemoveXattrAsync"/>).
    /// </summary>
    /// <param name="name">The attribute's name.</param>
    /// <param name="value">The bytes to store; never empty.</param>
    /// <param name="flags">Whether the attribute must or must not already exist.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the attribute has been stored.</returns>
    ValueTask SetXattrAsync(
        string name, ReadOnlyMemory<byte> value, XattrFlags flags, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes one attribute. The core calls this when a <c>Txattrcreate</c> fid whose
    /// <c>attr_size</c> was zero is clunked: that is how v9fs and diod spell <c>removexattr(2)</c>
    /// on the wire, and reference §8 rule 21 makes it a removal rather than an empty value.
    /// Removing an attribute that is not there is not an error.
    /// </summary>
    /// <param name="name">The attribute's name.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the attribute is gone.</returns>
    ValueTask RemoveXattrAsync(string name, CancellationToken cancellationToken = default);
}
