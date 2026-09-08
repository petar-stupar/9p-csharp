namespace NineP.TodoFs;

/// <summary>One open file's shared bytes, and the gate that orders the writes into them.</summary>
/// <param name="path">The file's qid path.</param>
internal sealed class TodoFieldState(ulong path) : IDisposable
{
    /// <summary>The file's qid path, which is how the registry finds this state again.</summary>
    public ulong Path => path;

    /// <summary>Orders every read and every read-modify-write of this file, across its opens.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>
    /// The splice base the open writers agree on, or null until one of them has read the row. A
    /// truncating open sets it to nothing; reads never consult it.
    /// </summary>
    public byte[]? Value { get; set; }

    /// <summary>How many opens hold this state; guarded by the registry's own lock.</summary>
    public int Opens { get; set; }

    /// <summary>Releases the gate once the last open has gone.</summary>
    public void Dispose() => Gate.Dispose();
}
