using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NineP.JsonFs;

/// <summary>One member of a container: its JSON key, its file name and the node itself.</summary>
/// <param name="Key">The key as it appears in the document, or the index text in an array.</param>
/// <param name="Name">The file name, which is <see cref="JsonKey.Encode"/> of the key.</param>
/// <param name="Node">The member's node.</param>
/// <param name="Cursor">Stable directory cookie, assigned on insertion.</param>
internal readonly record struct JsonChild(string Key, string Name, JsonTreeNode Node, ulong Cursor = 0);
