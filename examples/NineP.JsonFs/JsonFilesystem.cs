using System.Globalization;
using System.Text;
using System.Text.Json;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Server;

namespace NineP.JsonFs;

/// <summary>
/// A JSON document served as a 9P tree, exactly as the workspace architecture §7 maps it. The
/// handlers below know nothing about a dialect, a fid or an error shape: that is the point of the
/// handler model, and it is why the same tree answers 9P2000, 9P2000.u and 9P2000.L.
/// </summary>
internal sealed class JsonFilesystem : IFilesystem
{
    private const uint FilePerm = 0x1A4;
    private const uint DirectoryPerm = 0x1ED;

    internal JsonFsMutator Persistence => _mutator;

    private readonly JsonTree _tree;
    private readonly JsonFsMutator _mutator;
    private readonly TimeProvider _clock;

    /// <summary>Serves a loaded document.</summary>
    /// <param name="tree">The document.</param>
    /// <param name="writable">True to accept writes, creates, removes and renames.</param>
    /// <param name="writeBackPath">Where to rewrite the document after a change; null to skip it.</param>
    /// <param name="clock">The clock every reported time comes from.</param>
    public JsonFilesystem(
        JsonTree tree, bool writable, string? writeBackPath = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(tree);

        _tree = tree;
        _clock = clock ?? TimeProvider.System;
        _mutator = new JsonFsMutator(tree, writable, writeBackPath);
    }

    /// <summary>Returns the document's root for this identity.</summary>
    /// <param name="identity">Who the session runs as; the tree is owned by whoever attached.</param>
    /// <param name="aname">Ignored: jsonfs serves one document, so there is one tree.</param>
    /// <param name="cancellationToken">Cancels the attach.</param>
    /// <returns>The root directory handler.</returns>
    public ValueTask<IDirectoryHandler> AttachAsync(
        Identity identity, string aname, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IDirectoryHandler>(
            new JsonDirectoryHandler(_tree.Root, Context(identity), parent: null));
    }

    private JsonFsContext Context(Identity identity) =>
        new(_tree, _mutator, _clock, identity, FilePerm, DirectoryPerm);
}
