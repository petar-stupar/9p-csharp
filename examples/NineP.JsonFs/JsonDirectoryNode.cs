using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NineP.JsonFs;

/// <summary>A JSON object or array: a directory whose entries are its members.</summary>
internal sealed class JsonDirectoryNode : JsonTreeNode
{
    private readonly List<JsonChild> _children = [];
    private ulong _lastCursor;

    /// <summary>Creates an empty container.</summary>
    /// <param name="path">The allocation counter's value for this node.</param>
    /// <param name="isArray">True for a JSON array, false for an object.</param>
    public JsonDirectoryNode(ulong path, bool isArray)
        : base(path) => IsArray = isArray;

    /// <summary>True when this node is a JSON array, whose entry names are its indices.</summary>
    public bool IsArray { get; }

    /// <summary>The members, in document order.</summary>
    public IReadOnlyList<JsonChild> Children => _children;

    /// <summary>Appends a member; the caller has already checked that the name is free.</summary>
    /// <param name="child">The member to add.</param>
    public void Add(JsonChild child) => _children.Add(child with { Cursor = ++_lastCursor });

    internal void Restore(JsonChild[] children)
    {
        _children.Clear();
        _children.AddRange(children);
        // Never reuse a cookie even when a mutation rolls back.
    }

    internal int After(ulong cursor)
    {
        int low = 0;
        int high = _children.Count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (_children[mid].Cursor <= cursor)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }
        return low;
    }

    /// <summary>Finds a member by its file name.</summary>
    /// <param name="name">The name as it appears in the tree.</param>
    /// <returns>The member, or null when there is none.</returns>
    public JsonChild? Find(string name)
    {
        foreach (JsonChild child in _children)
        {
            if (string.Equals(child.Name, name, StringComparison.Ordinal))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>Removes a member by its file name.</summary>
    /// <param name="name">The name as it appears in the tree.</param>
    /// <returns>True when the member was there.</returns>
    public bool Remove(string name)
    {
        int at = IndexOf(name);
        if (at < 0)
        {
            return false;
        }

        _children.RemoveAt(at);
        return true;
    }

    /// <summary>The position of a member, or -1.</summary>
    /// <param name="name">The name as it appears in the tree.</param>
    /// <returns>The index in document order.</returns>
    public int IndexOf(string name)
    {
        for (int i = 0; i < _children.Count; i++)
        {
            if (string.Equals(_children[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Replaces a member's key and name, which is what a rename does to an object.</summary>
    /// <param name="oldName">The name it has now.</param>
    /// <param name="newChild">The member under its new key and name.</param>
    public void Replace(string oldName, JsonChild newChild)
    {
        int at = IndexOf(oldName);
        if (at >= 0)
        {
            _children[at] = newChild with { Cursor = _children[at].Cursor };
        }
    }
}
