using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>What one fid may see: who it runs as, which row that is, and whether it may use ctl.</summary>
/// <param name="Identity">The identity this fid runs as.</param>
/// <param name="User">The row that identity owns, or null when it has none.</param>
/// <param name="IsAdmin">True when the identity carries the configured admin role.</param>
internal readonly record struct TodoView(Identity Identity, UserRow? User, bool IsAdmin);
