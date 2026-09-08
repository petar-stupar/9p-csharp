using System.Globalization;
using NineP.Protocol;
using NineP.Server;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>An entry name that is a number, which is how lists and items are named.</summary>
internal static class TodoIndex
{
    /// <summary>Parses a name as a non-negative decimal index with no leading zeros.</summary>
    /// <param name="name">The entry name.</param>
    /// <param name="index">The index it stands for.</param>
    /// <returns>True when the name is a canonical index.</returns>
    public static bool TryParse(string name, out long index)
    {
        index = 0;

        // "01" and "1" would name one row under two names, which a filesystem must not do.
        if (name.Length == 0 || (name.Length > 1 && name[0] == '0'))
        {
            return false;
        }

        return long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }
}
