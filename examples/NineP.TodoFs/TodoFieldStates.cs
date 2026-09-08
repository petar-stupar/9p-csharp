namespace NineP.TodoFs;

/// <summary>
/// The fields that are open, one entry per <b>file</b> rather than one per open.
/// <para>
/// A field is a database column and a 9P write is a splice into its current value, so a write has
/// to merge into something: a client may keep several writes outstanding on one fid (architecture
/// §6), they arrive in an arbitrary order, and a read-modify-write against the row would let one of
/// them overwrite what another had just committed. Holding that merge buffer per open, which is
/// what the first implementation did, made two opens of one field two different files — a write
/// through one was invisible to the other's reads, and the other's next partial write merged into
/// its stale copy and dropped the first writer's change, acknowledged as written.
/// </para>
/// <para>
/// So there is one state per file, shared by every open of it in this process and dropped when the
/// last of them closes; the next open reads the row again. The store is the truth, and this is what
/// the writers that are open right now agree on in the meantime.
/// </para>
/// <para>
/// It is a <b>write</b> buffer and nothing else. A read answers from the row, because what a write
/// left in this buffer is not always what the file holds: <c>status</c> stores <c>done</c> for a
/// written <c>done\n</c>, and <c>/users/ctl</c> reads back the user list rather than the
/// <c>add alice</c> that changed it. Reads used to answer from here, so a fid that had written to
/// either file read its own command back until the last open closed.
/// </para>
/// </summary>
internal sealed class TodoFieldStates
{
    private readonly Dictionary<ulong, TodoFieldState> _open = [];

    /// <summary>Takes the state of one file, creating it when this is its first open.</summary>
    /// <param name="path">The file's qid path, which is its row and column.</param>
    /// <returns>The shared state; the caller releases it exactly once.</returns>
    internal TodoFieldState Acquire(ulong path)
    {
        lock (_open)
        {
            if (!_open.TryGetValue(path, out TodoFieldState? state))
            {
                state = new TodoFieldState(path);
                _open.Add(path, state);
            }

            state.Opens++;
            return state;
        }
    }

    /// <summary>Gives one open's share back, dropping the state when it was the last.</summary>
    /// <param name="state">The state this open acquired.</param>
    internal void Release(TodoFieldState state)
    {
        lock (_open)
        {
            if (--state.Opens > 0)
            {
                return;
            }

            _open.Remove(state.Path);
            state.Dispose();
        }
    }
}
