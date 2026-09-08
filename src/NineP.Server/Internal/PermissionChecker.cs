using NineP.Protocol;
using NineP.Protocol.Auth;

namespace NineP.Server.Internal;

/// <summary>
/// Evaluates <see cref="Attr.Perm"/> against the fid's <b>implicit identity</b> — the user of the
/// attach that created the fid, never a field of the message being answered (reference
/// §5.2). The check runs <b>before</b> the handler is called (architecture §4), so a handler that
/// forgot to check is still not reachable without permission; handlers may check more.
/// </summary>
internal static class PermissionChecker
{
    private const int OwnerShift = 6;
    private const int GroupShift = 3;

    /// <summary>Refuses the request when the identity lacks the bits it needs.</summary>
    /// <param name="attr">The file's attributes.</param>
    /// <param name="identity">The fid's implicit user.</param>
    /// <param name="access">The bits the request needs.</param>
    /// <param name="dialect">Selects Plan 9 or Unix permission-class evaluation.</param>
    /// <exception cref="NinePException">The identity may not do this.</exception>
    public static void Require(Attr attr, Identity identity, Access access, Dialect dialect = Dialect.P9_2000_L)
    {
        if (!Allows(attr, identity, access, dialect))
        {
            throw new NinePException(NinePError.FromErrno(Errno.EACCES));
        }
    }

    /// <summary>True when the identity holds every bit the request needs.</summary>
    /// <param name="attr">The file's attributes.</param>
    /// <param name="identity">The fid's implicit user.</param>
    /// <param name="access">The bits the request needs.</param>
    /// <param name="dialect">Selects Plan 9 or Unix permission-class evaluation.</param>
    /// <returns>False when any of them is missing.</returns>
    public static bool Allows(Attr attr, Identity identity, Access access, Dialect dialect = Dialect.P9_2000_L)
    {
        ArgumentNullException.ThrowIfNull(attr);
        ArgumentNullException.ThrowIfNull(identity);

        uint granted = (attr.Perm >> ShiftFor(attr, identity)) & 7;
        if (dialect == Dialect.P9_2000)
        {
            // Plan 9 considers every applicable class; Unix selects exactly one class.
            granted = attr.Perm & 7;
            if (IsOwner(attr, identity))
            {
                granted |= ((attr.Perm >> OwnerShift) | (attr.Perm >> GroupShift)) & 7;
            }
            else if (ShiftFor(attr, identity) == GroupShift)
            {
                granted |= (attr.Perm >> GroupShift) & 7;
            }
        }
        return ((uint)access & ~granted & 7) == 0;
    }

    /// <summary>
    /// True when the identity owns the file. stat(5) reserves <c>mode</c>, <c>mtime</c> and
    /// <c>gid</c> changes to the owner (or a group leader, which this workspace does not model).
    /// </summary>
    /// <param name="attr">The file's attributes.</param>
    /// <param name="identity">The fid's implicit user.</param>
    /// <returns>True when the identity is the file's owner.</returns>
    public static bool IsOwner(Attr attr, Identity identity)
    {
        ArgumentNullException.ThrowIfNull(attr);
        ArgumentNullException.ThrowIfNull(identity);

        if (attr.UserName.Length != 0 && string.Equals(attr.UserName, identity.User, StringComparison.Ordinal))
        {
            return true;
        }

        return identity.Uid is uint uid && attr.Uid != Constants.NONUNAME && attr.Uid == uid;
    }

    /// <summary>The access bits an open mode needs (open(5)).</summary>
    /// <param name="mode">The access mode being asked for.</param>
    /// <param name="flags">The flags accompanying it.</param>
    /// <returns>The permission bits the open requires.</returns>
    public static Access ForOpen(OpenMode mode, OpenFlags flags)
    {
        Access needed = mode switch
        {
            OpenMode.Read => Access.Read,
            OpenMode.Write => Access.Write,
            OpenMode.ReadWrite => Access.Read | Access.Write,
            _ => Access.Execute,
        };

        // open(5): OTRUNC needs write permission whatever the access mode is.
        return flags.HasFlag(OpenFlags.Truncate) ? needed | Access.Write : needed;
    }

    private static int ShiftFor(Attr attr, Identity identity)
    {
        if (IsOwner(attr, identity))
        {
            return OwnerShift;
        }

        return attr.GroupName.Length != 0 && identity.Groups.Contains(attr.GroupName, StringComparer.Ordinal)
            ? GroupShift
            : 0;
    }
}
