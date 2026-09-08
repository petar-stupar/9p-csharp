namespace NineP.Server;

/// <summary>
/// One open instance of a file (architecture §4). <see cref="ReadAsync"/> writes straight into the
/// outgoing frame's own payload buffer, so a read costs no copy between the handler and the wire.
/// </summary>
public interface IOpenFile : IAsyncDisposable
{
    /// <summary>Reads into the frame's payload buffer.</summary>
    /// <param name="offset">Where to read from; ignored on an append-only file.</param>
    /// <param name="buffer">The frame's own buffer, already clamped to the reply budget.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes read; 0 at end of file.</returns>
    ValueTask<int> ReadAsync(
        ulong offset, Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Writes a view of the incoming frame.</summary>
    /// <param name="offset">Where to write; ignored on an append-only file.</param>
    /// <param name="data">The bytes, viewing the frame that is still owned by the core.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The number of bytes actually written; fewer is a short write, not an error.</returns>
    ValueTask<int> WriteAsync(
        ulong offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>The current length, used for end of file and for <c>Rgetattr.size</c>.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The length in bytes.</returns>
    ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default);
}
