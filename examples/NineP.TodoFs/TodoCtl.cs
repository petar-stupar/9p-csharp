using System.Text;
using NineP.Protocol;
using NineP.Server;
using NineP.TodoFs.Storage;

namespace NineP.TodoFs;

/// <summary>
/// <c>/users/ctl</c>: the administrator's control file. Reading it lists the users, one per
/// line and sorted bytewise by name; writing it
/// takes exactly one command, <c>add &lt;name&gt;</c> or <c>remove &lt;name&gt;</c>, and anything
/// else is <c>EINVAL</c>. Both directions need the realm role <c>--admin-role</c>, so an ordinary
/// user cannot even learn who else exists (§8.3).
/// </summary>
internal sealed class TodoCtl(TodoSession session) : TodoFile(session)
{
    /// <summary>The file's name inside <c>/users</c>.</summary>
    public const string Name = "ctl";

    /// <summary>The fixed node number this file's qid carries.</summary>
    public const int Node = 3;

    /// <summary>The control file's qid.</summary>
    public override Qid Qid => TodoQid.Fixed(Node, QidType.QTFILE);

    /// <summary>Opens the file, which only an administrator may do in either direction.</summary>
    /// <param name="mode">The access mode.</param>
    /// <param name="flags">The flags accompanying the open.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open instance.</returns>
    /// <exception cref="NinePException">This identity does not carry the admin role.</exception>
    public override ValueTask<IOpenFile> OpenAsync(
        OpenMode mode, OpenFlags flags, CancellationToken cancellationToken = default)
    {
        if (!View.IsAdmin)
        {
            throw Denied();
        }

        return base.OpenAsync(mode, flags, cancellationToken);
    }

    /// <summary>
    /// <c>OTRUNC</c> on the control file. It keeps no bytes of its own — its read side renders the
    /// user table and its write side takes one command — so there is nothing for a truncation to
    /// remove, and it removes nothing. What it does do is empty the splice buffer of the open,
    /// which is what makes <c>ninep write /users/ctl</c> send a fresh command rather than splice
    /// into the last one. The exception to reference §8 rule 27 is deliberate and documented in
    /// <c>docs/examples.md</c>: refusing the open would refuse every documented way of writing to
    /// this file, and there is no length here that a client could set.
    /// </summary>
    /// <param name="cancellationToken">Cancels the truncation.</param>
    /// <returns>A completed task.</returns>
    protected internal override Task TruncateOnOpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A <c>Twstat</c> or <c>Tsetattr</c> asking for a length of zero is refused: the control
    /// file's length is the rendering of the user table and not a value a client may set, and a
    /// truncation that is not performed is refused rather than answered with success (reference
    /// §8 rule 27). Emptying it by deleting every user is not what a truncation means.
    /// </summary>
    /// <param name="cancellationToken">Cancels the truncation.</param>
    /// <returns>Nothing; this always throws.</returns>
    /// <exception cref="NinePException">The control file has no settable length.</exception>
    protected internal override Task TruncateToZeroAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw Invalid();
    }

    /// <summary>The length a stat reports, which an ordinary user may learn is zero.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The listing's length for an administrator, zero for anyone else.</returns>
    protected override async Task<ulong> SizeAsync(CancellationToken cancellationToken) =>
        View.IsAdmin
            ? (ulong)TodoText.Utf8.GetByteCount(await ReadAsync(cancellationToken).ConfigureAwait(false))
            : 0;

    /// <summary>The users, one per line.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The listing.</returns>
    /// <exception cref="NinePException">This identity does not carry the admin role.</exception>
    protected internal override async Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!View.IsAdmin)
        {
            throw Denied();
        }

        // Bytewise over the UTF-8 bytes, which is the order every listing in this workspace uses
        // and the order the cli's `ls` produces. The rows come back in insertion order, which is
        // an order nothing documents and which changes as users are added and removed.
        List<string> users =
        [
            .. (await Session.Store.ListUsersAsync(cancellationToken).ConfigureAwait(false))
                .Select(row => row.Name),
        ];

        users.Sort(static (left, right) =>
            TodoText.Utf8.GetBytes(left).AsSpan().SequenceCompareTo(TodoText.Utf8.GetBytes(right)));

        StringBuilder text = new();
        foreach (string user in users)
        {
            text.Append(user).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Applies exactly one command.</summary>
    /// <param name="value">The bytes the client wrote.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the command has been applied.</returns>
    /// <exception cref="NinePException">The identity is not an administrator, or the command is malformed.</exception>
    protected internal override async Task WriteAsync(
        string value, CancellationToken cancellationToken = default)
    {
        if (!View.IsAdmin)
        {
            throw Denied();
        }

        (string verb, string name) = Parse(value);

        if (verb == "add")
        {
            await Session.Store.EnsureUserAsync(name, cancellationToken).ConfigureAwait(false);
            return;
        }

        // remove takes the user's lists and items with it, through the schema's cascade.
        await Session.Store.RemoveUserAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Splits one command. Exactly one command per write, exactly two words, and a name that is
    /// not empty: anything else is <c>EINVAL</c> rather than a guess at what was meant.
    /// </summary>
    /// <param name="value">The bytes the client wrote.</param>
    /// <returns>The verb and the user name.</returns>
    /// <exception cref="NinePException">The command is not one of the two.</exception>
    private static (string Verb, string Name) Parse(string value)
    {
        string command = value.EndsWith('\n') ? value[..^1] : value;

        if (command.Contains('\n', StringComparison.Ordinal))
        {
            throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
        }

        string[] words = command.Split(' ');
        if (words.Length != 2 || words[1].Length == 0 || words[0] is not ("add" or "remove"))
        {
            throw new NinePException(NinePError.FromErrno(Errno.EINVAL));
        }

        return (words[0], words[1]);
    }
}
