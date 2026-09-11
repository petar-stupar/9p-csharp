using System.Globalization;
using System.Text;
using System.Text.Json;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Server;

namespace NineP.JsonFs;

/// <summary>Everything a handler needs that is the same for every node of one attach.</summary>
/// <param name="Tree">The served document.</param>
/// <param name="Mutator">The one place a change to the document is made.</param>
/// <param name="Clock">The clock reported times come from.</param>
/// <param name="Identity">Who the session runs as; every node is owned by them.</param>
/// <param name="FilePerm">The permission bits every scalar file reports.</param>
/// <param name="DirectoryPerm">The permission bits every container reports.</param>
internal readonly record struct JsonFsContext(
    JsonTree Tree,
    JsonFsMutator Mutator,
    TimeProvider Clock,
    Identity Identity,
    FilePermissions FilePerm,
    FilePermissions DirectoryPerm);
