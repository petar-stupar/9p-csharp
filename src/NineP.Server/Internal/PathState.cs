using NineP.Protocol;

namespace NineP.Server.Internal;

/// <summary>Tracks only live fids so renames can update shared ancestry without retaining history.</summary>
internal sealed class PathState
{
    private readonly HashSet<FidEntry> _entries = [];
    private readonly HashSet<WalkCursor> _walks = [];
    private readonly Dictionary<ulong, MutationGate> _gates = [];
    private long _version;

    /// <summary>Serializes mutations of overlapping directory operands, after fid leases.</summary>
    /// <param name="selectPaths">Selects the stable qids of the affected directory entries.</param>
    /// <param name="cancellationToken">Cancels waiting for the affected directories.</param>
    /// <returns>The namespace lease.</returns>
    public async ValueTask<IDisposable> AcquireAsync(Func<IEnumerable<ulong>> selectPaths, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IDisposable? lease = await TryAcquireAsync(selectPaths, cancellationToken).ConfigureAwait(false);
            if (lease is not null)
            {
                return lease;
            }
        }
    }

    private async ValueTask<IDisposable?> TryAcquireAsync(Func<IEnumerable<ulong>> selectPaths, CancellationToken cancellationToken)
    {
        ulong[] keys = selectPaths().Distinct().Order().ToArray();
        List<(ulong Key, MutationGate Gate)> held = [];
        try
        {
            foreach (ulong key in keys)
            {
                MutationGate gate;
                lock (_gates)
                {
                    if (!_gates.TryGetValue(key, out gate!))
                    {
                        gate = new MutationGate();
                        _gates.Add(key, gate);
                    }

                    gate.Users++;
                }

                try
                {
                    await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    Return(key, gate, false);
                    throw;
                }

                held.Add((key, gate));
            }

            if (!keys.SequenceEqual(selectPaths().Distinct().Order()))
            {
                foreach ((ulong key, MutationGate gate) in held)
                {
                    Return(key, gate, true);
                }

                held.Clear();
                return null;
            }

            return new NamespaceLease(this, held);
        }
        catch
        {
            foreach ((ulong key, MutationGate gate) in held)
            {
                Return(key, gate, true);
            }

            throw;
        }
    }

    private void Return(ulong key, MutationGate gate, bool acquired)
    {
        lock (_gates)
        {
            if (acquired)
            {
                gate.Semaphore.Release();
            }

            if (--gate.Users == 0)
            {
                _gates.Remove(key);
                gate.Semaphore.Dispose();
            }
        }
    }

    /// <summary>Tracks temporary ancestry until a walk binds it or returns a partial result.</summary>
    /// <param name="entry">The starting fid.</param>
    /// <returns>The temporary path owner.</returns>
    public WalkCursor BeginWalk(FidEntry entry)
    {
        WalkCursor cursor = new(this, entry.Path, entry.AttachRoot);
        lock (_entries)
        {
            _walks.Add(cursor);
        }

        return cursor;
    }

    /// <summary>The current namespace revision, sampled before calling a lookup handler.</summary>
    public long Version
    {
        get
        {
            lock (_entries)
            {
                return _version;
            }
        }
    }

    /// <summary>Publishes a lookup only if no rename occurred while its handler was running.</summary>
    /// <param name="cursor">The temporary owner.</param>
    /// <param name="path">The new path.</param>
    /// <param name="version">The version sampled before lookup.</param>
    /// <returns>False when the lookup must be retried.</returns>
    public bool Advance(WalkCursor cursor, FidPath? path, long version)
    {
        lock (_entries)
        {
            if (version != _version)
            {
                return false;
            }

            cursor.Path = path;
            return true;
        }
    }

    /// <summary>Registers a bound fid.</summary>
    /// <param name="entry">The live fid.</param>
    public void Add(FidEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    /// <summary>Forgets a retired fid and permits its ancestry to be collected.</summary>
    /// <param name="entry">The retired fid.</param>
    public void Remove(FidEntry entry)
    {
        lock (_entries)
        {
            _entries.Remove(entry);
        }
    }

    /// <summary>Rejects moving a directory underneath itself, which would create cyclic ancestry.</summary>
    /// <param name="child">The moved handler.</param>
    /// <param name="destination">The destination fid.</param>
    public static void ValidateMove(IHandler child, FidEntry destination)
    {
        if (child is not IDirectoryHandler)
        {
            return;
        }

        if (destination.Handler.Qid.Path == child.Qid.Path)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
        }

        for (FidPath? path = destination.Path; path is not null; path = path.Previous)
        {
            if (path.Directory.Qid.Path == child.Qid.Path)
            {
                throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
            }
        }
    }

    /// <summary>Rebases every live alias of a successfully renamed directory entry.</summary>
    /// <param name="child">The moved handler.</param>
    /// <param name="from">The original directory.</param>
    /// <param name="oldName">The original name.</param>
    /// <param name="to">The new directory.</param>
    /// <param name="newName">The new name.</param>
    /// <param name="destinationPath">The destination's ancestry.</param>
    public void Move(IHandler child, IDirectoryHandler from, string oldName,
        IDirectoryHandler to, string newName, FidPath? destinationPath)
    {
        // Collect before mutating: changing one shared edge can otherwise change a later scan.
        Dictionary<FidPath, IHandler> affected = new(ReferenceEqualityComparer.Instance);
        lock (_entries)
        {
            foreach (FidEntry entry in _entries)
            {
                Collect(entry.Path, entry.AttachRoot);
            }

            foreach (WalkCursor walk in _walks)
            {
                Collect(walk.Path, walk.Root);
            }

            _version++;

            foreach ((FidPath path, IHandler root) in affected)
            {
                (IDirectoryHandler parent, FidPath? previous) = Rebase(to, destinationPath, root);
                path.Directory = to;
                path.AscendTo = parent.Qid.Path == to.Qid.Path ? null : parent;
                path.Name = newName;
                path.Previous = previous;
            }
        }
        void Collect(FidPath? trail, IHandler root)
        {
            for (FidPath? path = trail; path is not null; path = path.Previous)
            {
                if (path.ChildPath == child.Qid.Path && path.Directory.Qid.Path == from.Qid.Path
                    && path.Name == oldName)
                {
                    affected.TryAdd(path, root);
                }
            }
        }
    }

    private static (IDirectoryHandler Parent, FidPath? Previous) Rebase(
        IDirectoryHandler parent, FidPath? path, IHandler root)
    {
        if (parent.Qid.Path == root.Qid.Path)
        {
            return (parent, null);
        }

        List<FidPath> edges = [];
        for (FidPath? current = path; current is not null; current = current.Previous)
        {
            edges.Add(current);
            if (current.Directory.Qid.Path != root.Qid.Path)
            {
                continue;
            }

            // Share the existing chain when it already stops at this attach root.
            if (current.Previous is null)
            {
                return (parent, path);
            }

            FidPath? truncated = null;
            for (int at = edges.Count - 1; at >= 0; at--)
            {
                FidPath edge = edges[at];
                truncated = new FidPath(edge.Directory, edge.Name, truncated, edge.ChildPath);
            }

            return (parent, truncated);
        }

        // A move outside a restricted attach cannot grant that attach access to a new ancestor.
        return (root as IDirectoryHandler ?? parent, null);
    }

    private sealed class MutationGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class NamespaceLease(PathState owner, List<(ulong Key, MutationGate Gate)> held) : IDisposable
    {
        public void Dispose()
        {
            foreach ((ulong key, MutationGate gate) in held)
            {
                owner.Return(key, gate, true);
            }
        }
    }

    /// <summary>A bounded temporary owner for a walk that has not yet bound its destination fid.</summary>
    /// <param name="owner">The registry.</param>
    /// <param name="path">The initial ancestry.</param>
    /// <param name="root">The attach boundary.</param>
    internal sealed class WalkCursor(PathState owner, FidPath? path, IHandler root) : IDisposable
    {
        /// <summary>The current temporary ancestry.</summary>
        public FidPath? Path { get; set; } = path;

        /// <summary>The attach boundary.</summary>
        public IHandler Root { get; } = root;

        /// <summary>Forgets unbound temporary ancestry on every exit path.</summary>
        public void Dispose()
        {
            lock (owner._entries)
            {
                owner._walks.Remove(this);
            }
        }
    }
}
