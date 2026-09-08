using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NineP.JsonFs;

/// <summary>
/// One node of the served document. The qid <c>path</c> is an allocation counter handed out at
/// load and the <c>version</c> is a per-node modification counter, so a qid is stable for the
/// life of the process and changes exactly when the node does (reference §4.1).
/// </summary>
internal abstract class JsonTreeNode
{
    /// <summary>Creates a node with its permanent qid path.</summary>
    /// <param name="path">The allocation counter's value for this node.</param>
    protected JsonTreeNode(ulong path) => Path = path;

    /// <summary>The qid path: this node's identity for the life of the process.</summary>
    public ulong Path { get; }

    /// <summary>The qid version: bumped on every modification.</summary>
    public uint Version { get; internal set; }

    /// <summary>Records that this node changed.</summary>
    public void Touch() => Version++;
}
