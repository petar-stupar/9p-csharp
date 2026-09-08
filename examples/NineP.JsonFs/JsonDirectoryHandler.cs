using System.Globalization;
using NineP.Protocol;
using NineP.Server;

namespace NineP.JsonFs;

/// <summary>
/// A JSON object or array as a directory. An object's entry names are its keys, percent-encoded;
/// an array's are its indices, so <c>/list/0</c> is the first element (architecture §7).
/// </summary>
internal sealed class JsonDirectoryHandler : JsonNodeHandler, IDirectoryHandler
{
    private readonly JsonDirectoryNode _node;

    /// <summary>Creates a handler over one container.</summary>
    /// <param name="node">The container.</param>
    /// <param name="context">The per-attach context.</param>
    /// <param name="parent">The container it lives in; null for the document's root.</param>
    public JsonDirectoryHandler(JsonDirectoryNode node, JsonFsContext context, JsonDirectoryNode? parent)
        : base(node, context, parent) => _node = node;

    /// <summary>A container is a directory in every dialect.</summary>
    public override FileKind Kind => FileKind.Directory;

    /// <summary>A directory reports no length of its own (reference §4.2).</summary>
    protected override ulong Length => 0;

    /// <summary>Resolves one entry name.</summary>
    /// <param name="name">The name; the core has already validated it.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The child's handler, or null when there is no such entry.</returns>
    public ValueTask<IHandler?> LookupAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (Context.Tree.Gate)
        {
            return ValueTask.FromResult(_node.Find(name) is JsonChild child
                ? Wrap(child.Node)
                : null);
        }
    }

    /// <summary>Returns a page of entries starting at a cursor.</summary>
    /// <param name="cursor">Where to resume; 0 starts the listing.</param>
    /// <param name="max">The most entries the core can use.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The page.</returns>
    public ValueTask<DirectoryListing> ReadDirAsync(
        ulong cursor, int max, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<DirEntry> page = [];
        bool ended;

        lock (Context.Tree.Gate)
        {
            int start = _node.After(cursor);
            int at = start;

            while (at < _node.Children.Count && page.Count < max)
            {
                JsonChild child = _node.Children[at];
                at++;
                page.Add(new DirEntry(child.Name, QidOf(child.Node), KindOf(child.Node), child.Cursor));
            }

            ended = at >= _node.Children.Count;
            cursor = page.Count == 0 ? cursor : page[^1].Cursor;
        }

        return ValueTask.FromResult(new DirectoryListing(page, cursor, ended));
    }

    /// <summary>
    /// Creates a member. A file becomes an empty string and a directory an empty object; an array
    /// accepts only its next index, which is what makes <c>write /list/6</c> an append and
    /// <c>write /list/9</c> an <c>EINVAL</c> (conformance Part B step 6).
    /// </summary>
    /// <param name="request">Everything the five create messages carry, unified.</param>
    /// <param name="cancellationToken">Cancels the create.</param>
    /// <returns>The new member's handler.</returns>
    /// <exception cref="NinePException">The server is read-only, or the name is not creatable.</exception>
    public ValueTask<IHandler> CreateAsync(
        CreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Kind is not (FileKind.File or FileKind.Directory))
        {
            // jsonfs has no symlinks, devices, fifos or sockets: JSON cannot represent one.
            throw new NinePException(NinePError.FromErrno(Errno.EOPNOTSUPP));
        }

        return ValueTask.FromResult(Context.Mutator.Mutate<IHandler>(() =>
        {
            if (_node.Find(request.Name) is not null)
            {
                throw new NinePException(NinePError.FromEname("file exists"));
            }

            string key = _node.IsArray ? RequireNextIndex(request.Name) : JsonKey.Decode(request.Name);
            JsonTreeNode created = request.Kind == FileKind.Directory
                ? new JsonDirectoryNode(Context.Tree.NextPath(), isArray: false)
                : new JsonScalarNode(Context.Tree.NextPath(), JsonScalarKind.Text, string.Empty);

            _node.Add(new JsonChild(key, request.Name, created));
            _node.Touch();
            return Wrap(created)!;
        }));
    }

    /// <summary>
    /// Removes a member. A non-empty container is <c>ENOTEMPTY</c> (Part B step 5), and in an
    /// array only the last element may go, so that the remaining indices stay the names they were.
    /// </summary>
    /// <param name="name">The member's name.</param>
    /// <param name="kind">What the core resolved the member to.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns>A task that completes when the member is gone.</returns>
    /// <exception cref="NinePException">The server is read-only, or the member may not be removed.</exception>
    public ValueTask RemoveAsync(
        string name, FileKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Context.Mutator.Mutate(() =>
        {
            int at = _node.IndexOf(name);
            if (at < 0)
            {
                throw new NinePException(NinePError.FromErrno(Errno.ENOENT));
            }

            if (_node.Children[at].Node is JsonDirectoryNode { Children.Count: > 0 })
            {
                throw new NinePException(NinePError.FromErrno(Errno.ENOTEMPTY));
            }

            if (_node.IsArray && at != _node.Children.Count - 1)
            {
                throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
            }

            _node.Remove(name);
            _node.Touch();
        });

        return ValueTask.CompletedTask;
    }

    /// <summary>Moves a member, which for an object is a key rename.</summary>
    /// <param name="oldName">The member's name here.</param>
    /// <param name="newParent">The container to move it into; may be this one.</param>
    /// <param name="newName">The name it takes there.</param>
    /// <param name="cancellationToken">Cancels the rename.</param>
    /// <returns>A task that completes when the member has moved.</returns>
    /// <exception cref="NinePException">The server is read-only, or the move is not representable.</exception>
    public ValueTask RenameAsync(
        string oldName,
        IDirectoryHandler newParent,
        string newName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newParent);
        cancellationToken.ThrowIfCancellationRequested();

        if (newParent is not JsonDirectoryHandler target)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
        }

        Context.Mutator.Mutate(() =>
        {
            int at = _node.IndexOf(oldName);
            if (at < 0)
            {
                throw new NinePException(NinePError.FromErrno(Errno.ENOENT));
            }

            if (ReferenceEquals(_node, target._node) && string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                return;
            }

            // A container cannot be moved into itself or into anything under it: the result is a
            // cycle no walk from the root can reach, and the whole moved subtree then vanishes
            // from a --write-back document, which serialises only what the root still reaches.
            if (Encloses(_node.Children[at].Node, target._node))
            {
                throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
            }

            if (target._node.Find(newName) is not null)
            {
                throw new NinePException(NinePError.FromEname("file exists"));
            }

            // An array's names are its indices, so a member cannot be renamed inside one and can
            // only leave it from the end; anything else would renumber the elements after it.
            if (_node.IsArray && at != _node.Children.Count - 1)
            {
                throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
            }

            string key = target._node.IsArray ? target.RequireNextIndex(newName) : JsonKey.Decode(newName);
            JsonChild moved = _node.Children[at];
            _node.Remove(oldName);
            target._node.Add(new JsonChild(key, newName, moved.Node));
            _node.Touch();
            target._node.Touch();
        });

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Whether a container is the node itself or lives somewhere beneath it. The walk is
    /// iterative because the tree's depth is bounded only at load time, and a run of
    /// <c>mkdir</c> can take it past that.
    /// </summary>
    /// <param name="node">The node being moved.</param>
    /// <param name="candidate">The container it would be moved into.</param>
    /// <returns>True when the move would put the node inside itself.</returns>
    private static bool Encloses(JsonTreeNode node, JsonDirectoryNode candidate)
    {
        Stack<JsonTreeNode> pending = new();
        pending.Push(node);

        while (pending.Count > 0)
        {
            JsonTreeNode current = pending.Pop();
            if (ReferenceEquals(current, candidate))
            {
                return true;
            }

            if (current is JsonDirectoryNode container)
            {
                foreach (JsonChild child in container.Children)
                {
                    pending.Push(child.Node);
                }
            }
        }

        return false;
    }

    private static Qid QidOf(JsonTreeNode node) => node switch
    {
        JsonDirectoryNode => new Qid(QidType.QTDIR, node.Version, node.Path),
        _ => new Qid(QidType.QTFILE, node.Version, node.Path),
    };

    private static FileKind KindOf(JsonTreeNode node) =>
        node is JsonDirectoryNode ? FileKind.Directory : FileKind.File;

    /// <summary>The index text an array's next member must carry.</summary>
    /// <param name="name">The name the client asked to create.</param>
    /// <returns>The name, once it is the next index.</returns>
    /// <exception cref="NinePException">The name is not the array's next index.</exception>
    private string RequireNextIndex(string name)
    {
        string next = _node.Children.Count.ToString(CultureInfo.InvariantCulture);
        return string.Equals(name, next, StringComparison.Ordinal)
            ? name
            : throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
    }

    private IHandler? Wrap(JsonTreeNode node) => node switch
    {
        JsonDirectoryNode container => new JsonDirectoryHandler(container, Context, _node),
        JsonScalarNode scalar => new JsonFileHandler(scalar, Context, _node),
        _ => null,
    };
}
