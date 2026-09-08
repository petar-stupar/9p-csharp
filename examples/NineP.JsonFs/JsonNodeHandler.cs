using System.Globalization;
using System.Text;
using System.Text.Json;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Server;

namespace NineP.JsonFs;

/// <summary>What every jsonfs node answers, whatever kind of JSON value it stands for.</summary>
internal abstract class JsonNodeHandler : IHandler
{
    /// <summary>Creates a handler over one node.</summary>
    /// <param name="node">The node.</param>
    /// <param name="context">The per-attach context.</param>
    /// <param name="parent">The container this node lives in; null for the document's root.</param>
    protected JsonNodeHandler(JsonTreeNode node, JsonFsContext context, JsonDirectoryNode? parent)
    {
        Node = node;
        Context = context;
        Parent = parent;
    }

    /// <summary>The qid: the node's permanent path and its modification counter.</summary>
    public Qid Qid => new(QidTypeOf(Kind), Node.Version, Node.Path);

    /// <summary>What kind of file this node is.</summary>
    public abstract FileKind Kind { get; }

    /// <summary>The node behind this handler.</summary>
    protected JsonTreeNode Node { get; }

    /// <summary>The per-attach context.</summary>
    protected JsonFsContext Context { get; }

    /// <summary>The container this node lives in, so a <c>wstat</c> rename can rekey it there.</summary>
    protected JsonDirectoryNode? Parent { get; }

    /// <summary>The node's length in bytes; zero for a container.</summary>
    protected abstract ulong Length { get; }

    /// <summary>The dialect-neutral attributes the core projects.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The attributes.</returns>
    public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TimeSpec now = new(Context.Clock.GetUtcNow().ToUnixTimeSeconds(), 0);
        ulong size = Length;

        return ValueTask.FromResult(new Attr
        {
            Qid = Qid,
            Kind = Kind,
            Perm = Kind == FileKind.Directory ? Context.DirectoryPerm : Context.FilePerm,
            UserName = Context.Identity.User,
            GroupName = Context.Identity.User,
            ModifierName = Context.Identity.User,
            Uid = Context.Identity.Uid,
            Gid = Context.Identity.Uid,
            Size = size,
            Blocks = (size + 511) / 512,
            ATime = now,
            MTime = now,
            CTime = now,
        });
    }

    /// <summary>
    /// Applies an update whole or refuses it whole (reference §8 rule 27). jsonfs keeps no
    /// attributes of its own — the mode, the owner and the times are all derived — so the two
    /// fields it can honour are the length, when it is zero and the node is a scalar, and the
    /// name. Every field is decided before any of it is applied: an update naming something
    /// jsonfs cannot do changes nothing, and an update naming both a truncation and a rename
    /// performs both. It used to return after the first field it honoured, so a
    /// <c>Twstat</c> carrying a length of zero <b>and</b> a name silently dropped the rename.
    /// </summary>
    /// <param name="update">The fields to change.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the change has been made.</returns>
    /// <exception cref="NinePException">The update names something jsonfs cannot change.</exception>
    public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();

        if (update.IsFsyncRequest)
        {
            return ValueTask.CompletedTask;
        }

        if (update.Perm is not null
            || update.Uid is not null
            || update.Gid is not null
            || update.GroupName is not null
            || update.ATime is not null
            || update.MTime is not null
            || update.ATimeToNow
            || update.MTimeToNow
            || update.CTimeToNow)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EOPNOTSUPP));
        }

        JsonFileHandler? truncating = null;
        bool honoured = false;

        if (update.Size is { } size)
        {
            if (this is JsonFileHandler file)
            {
                // A JSON scalar has no representation for "padded out to n bytes", so a length
                // that is not zero is an operation jsonfs does not have rather than a bad field.
                truncating = size == 0
                    ? file
                    : throw new NinePException(NinePError.FromErrno(Errno.EOPNOTSUPP));
            }
            else if (size != 0)
            {
                // stat(5): a directory's length must stay zero, which is what truncate(2) answers
                // EISDIR for; a length of zero is already true, so there is nothing to perform.
                throw new NinePException(NinePError.FromErrno(Errno.EISDIR));
            }

            honoured = true;
        }

        // stat(5): a wstat that carries a name is a rename inside the same directory, which is
        // the only way 9P2000 and 9P2000.u can move anything (.L has Trenameat).
        if (update.Name is { } renamed)
        {
            Rename(renamed, truncating);
            return ValueTask.CompletedTask;
        }

        if (!honoured)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EOPNOTSUPP));
        }

        truncating?.Truncate();
        return ValueTask.CompletedTask;
    }

    /// <summary>Nothing to release: the tree outlives every fid.</summary>
    /// <param name="wasOpen">Ignored.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A completed task.</returns>
    public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <summary>Reaches stable storage, which for jsonfs is the write-back file when there is one.</summary>
    /// <param name="dataOnly">Ignored: the whole document is written or none of it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A completed task.</returns>
    public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Context.Mutator.Flush();
        return ValueTask.CompletedTask;
    }

    /// <summary>Moves this node to another key inside its own container.</summary>
    /// <param name="newName">The name it takes; the core has already validated it.</param>
    /// <param name="truncating">The scalar to empty in the same mutation, or null.</param>
    /// <exception cref="NinePException">The server is read-only, or the move is not representable.</exception>
    private void Rename(string newName, JsonFileHandler? truncating)
    {
        if (Parent is not { } parent)
        {
            // The document's root has no key to rename.
            throw new NinePException(NinePError.FromErrno(Errno.EPERM));
        }

        Context.Mutator.Mutate(() =>
        {
            JsonChild? found = null;
            foreach (JsonChild child in parent.Children)
            {
                if (ReferenceEquals(child.Node, Node))
                {
                    found = child;
                    break;
                }
            }

            if (found is not { } moving)
            {
                throw new NinePException(NinePError.FromErrno(Errno.ENOENT));
            }

            if (!string.Equals(moving.Name, newName, StringComparison.Ordinal))
            {
                if (parent.IsArray)
                {
                    throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
                }
                if (parent.Find(newName) is not null)
                {
                    throw new NinePException(NinePError.FromErrno(Errno.EEXIST));
                }
            }
            else
            {
                truncating?.Empty();
                return;
            }

            // Rule 27: every check above ran before either change, so the pair is applied
            // together or not at all — one wstat, one outcome.
            truncating?.Empty();
            parent.Replace(moving.Name, new JsonChild(JsonKey.Decode(newName), newName, moving.Node));
            parent.Touch();
        });
    }

    private static QidType QidTypeOf(FileKind kind) =>
        kind == FileKind.Directory ? QidType.QTDIR : QidType.QTFILE;
}
