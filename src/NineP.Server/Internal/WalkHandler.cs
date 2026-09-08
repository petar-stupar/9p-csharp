using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Messages;

namespace NineP.Server.Internal;

/// <summary>
/// Walk semantics (reference §5.4, §6.5). The whole point of walking element by element is that a
/// partial walk is not a failure and not a success: the client is told how far it got, and
/// <c>newfid</c> is left unbound, so nothing has to be undone.
/// </summary>
internal static class WalkHandler
{
    /// <summary>Walks a fid and, when the walk is complete, binds the new one.</summary>
    /// <param name="fids">The connection's fid table.</param>
    /// <param name="request">The walk as it came off the wire.</param>
    /// <param name="entry">The fid being walked from, already gated.</param>
    /// <param name="checkSearch">True to require search permission on every directory traversed.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <param name="paths">The live ancestry registry.</param>
    /// <param name="dialect">The negotiated permission semantics.</param>
    /// <returns>The reply, whose qid count says how far the walk got.</returns>
    /// <exception cref="NinePException">The fid is open, newfid is in use, or the first element failed.</exception>
    public static async ValueTask<Rwalk> WalkAsync(
        FidTable fids,
        Twalk request,
        FidEntry entry,
        bool checkSearch,
        PathState paths,
        Dialect dialect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fids);
        ArgumentNullException.ThrowIfNull(entry);

        // walk(5) and srv.c:302-309: a fid that has been opened for I/O cannot be walked or
        // cloned, whether or not any elements were named.
        if (entry.State == FidState.Open)
        {
            throw new NinePException(NinePError.FromEname("cannot clone open fid"));
        }

        if (entry.IsAuth)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EPERM));
        }

        fids.RequireFree(request.NewFid, request.Fid);

        IHandler current = entry.Handler;
        using PathState.WalkCursor cursor = paths.BeginWalk(entry);

        List<Qid> qids = [];

        for (int at = 0; at < request.Wnames.Count; at++)
        {
            // §5.2: a walk is made by the implicit user of the fid being walked from, which the
            // attach fixed and nothing moves.
            IHandler? next;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long version = paths.Version;
                    FidPath? trail;
                    (next, trail) = await StepAsync(
                        current, cursor.Path, request.Wnames[at], entry.Identity, checkSearch, dialect, cancellationToken)
                        .ConfigureAwait(false);
                    if (paths.Advance(cursor, trail, version))
                    {
                        break;
                    }
                }
            }
            catch (NinePException failure) when (at > 0 && failure is not NinePProtocolException)
            {
                return new Rwalk(request.Tag, qids);
            }

            if (next is null)
            {
                // §5.4: a first-element failure is an error and changes nothing; a later one is a
                // partial walk, which returns the prefix and binds nothing.
                return at == 0
                    ? throw new NinePException(NinePError.FromErrno(Errno.ENOENT))
                    : new Rwalk(request.Tag, qids);
            }

            current = next;
            qids.Add(current.Qid);
        }

        Bind(fids, request, entry, current, cursor.Path);
        return new Rwalk(request.Tag, qids);
    }

    private static async ValueTask<(IHandler? Handler, FidPath? Trail)> StepAsync(
        IHandler current,
        FidPath? trail,
        string name,
        Identity identity,
        bool checkSearch,
        Dialect dialect,
        CancellationToken cancellationToken)
    {
        if (current is not IDirectoryHandler directory)
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOTDIR));
        }

        // §5.4: a walk requires search permission on each directory traversed, and the check runs
        // before the lookup — otherwise a caller could probe for names it may not see.
        if (checkSearch)
        {
            Attr attr = await directory.GetAttrAsync(cancellationToken).ConfigureAwait(false);
            PermissionChecker.Require(attr, identity, Access.Execute, dialect);
        }

        if (name == "..")
        {
            return trail is null ? (current, null) : (trail.AscendTo ?? trail.Directory, trail.Previous);
        }

        IHandler? child = await directory.LookupAsync(name, cancellationToken).ConfigureAwait(false);
        return (child, child is null ? trail : new FidPath(directory, name, trail, child.Qid.Path));
    }

    private static void Bind(
        FidTable fids,
        Twalk request,
        FidEntry entry,
        IHandler landing,
        FidPath? trail)
    {
        // walk(5): newfid may equal fid, in which case the fid moves rather than a second one
        // being bound. Everything else about the fid — its tree, its identity — is kept.
        if (request.NewFid == request.Fid)
        {
            entry.Path = trail;
            entry.Handler = landing;
            entry.Parent = trail?.Directory;
            entry.Name = trail?.Name ?? "/";
            return;
        }

        // CA2000: the entry belongs to the table from here on, and the table releases it.
#pragma warning disable CA2000
        FidEntry bound = new(request.NewFid, landing, entry.Identity, entry.Aname)
        {
            Path = trail,
            AttachRoot = entry.AttachRoot,
            Parent = trail?.Directory,
            Name = trail?.Name ?? "/",
            Generation = entry.Generation,
        };
#pragma warning restore CA2000

        fids.Bind(bound);
    }
}
