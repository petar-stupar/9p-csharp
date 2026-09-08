using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NineP.JsonFs;

/// <summary>What a JSON scalar becomes on the filesystem (workspace architecture §7).</summary>
internal enum JsonScalarKind
{
    /// <summary>A JSON string: the file holds its UTF-8 bytes exactly, with nothing added.</summary>
    Text = 0,

    /// <summary>A JSON number: the file holds the number's text (see <see cref="JsonNumber"/>).</summary>
    Number = 1,

    /// <summary>A JSON boolean: the file holds <c>true</c> or <c>false</c>.</summary>
    Boolean = 2,

    /// <summary>JSON <c>null</c>: the file is empty.</summary>
    Null = 3,
}
