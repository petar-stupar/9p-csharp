using System.Globalization;
using NineP.Client;
using NineP.Protocol;

namespace NineP.Cli;

/// <summary>
/// The nine commands and their <b>frozen</b> output formats
/// (<c>docs/9p/fixtures/conformance.md</c>). Every byte printed here is compared against
/// <c>sample.expected.txt</c> by the conformance driver of every language in the workspace, so a
/// change to any of these strings is a change to the cross-language contract.
/// </summary>
internal static class CliCommands
{
    /// <summary>Runs one command against an attached session.</summary>
    /// <param name="session">The attached session.</param>
    /// <param name="options">The parsed command line.</param>
    /// <param name="output">Where the command's output goes.</param>
    /// <param name="input">Where <c>write</c> reads its bytes from.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>A task that completes when the command has finished.</returns>
    /// <exception cref="CliUsageException">The command or its arguments are wrong.</exception>
    /// <exception cref="NinePException">The server refused, or the path is of the wrong kind.</exception>
    public static async Task RunAsync(
        NinePSession session,
        CliOptions options,
        Stream output,
        Stream input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(input);

        switch (options.Command)
        {
            case "version":
                await VersionAsync(session, output, cancellationToken).ConfigureAwait(false);
                return;
            case "ls":
                await ListAsync(session, options, output, cancellationToken).ConfigureAwait(false);
                return;
            case "cat":
                await CatAsync(session, One(options), output, cancellationToken).ConfigureAwait(false);
                return;
            case "stat":
                await StatAsync(session, One(options), output, cancellationToken).ConfigureAwait(false);
                return;
            case "write":
                await WriteAsync(session, One(options), output, input, cancellationToken).ConfigureAwait(false);
                return;
            case "mkdir":
                await session.MkdirAsync(One(options), cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            case "rm":
                await session.RemoveAsync(One(options), cancellationToken).ConfigureAwait(false);
                return;
            case "mv":
                await MoveAsync(session, options, cancellationToken).ConfigureAwait(false);
                return;
            case "readlink":
                // The frozen format prints nothing on success; the target is fetched anyway so
                // that a path that is not a symbolic link is reported as the error it is.
                await session.ReadlinkAsync(One(options), cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new CliUsageException("unknown command " + options.Command);
        }
    }

    /// <summary>
    /// Compares two entry names <b>bytewise over their UTF-8 bytes</b> (C locale), which is what
    /// the fixture's ordering is. Neither <see cref="string.CompareTo(string)"/> nor an ordinal
    /// UTF-16 comparison will do: the two disagree above U+FFFF, where UTF-16 orders surrogate
    /// pairs before U+E000..U+FFFF and UTF-8 does not.
    /// </summary>
    /// <param name="left">One name.</param>
    /// <param name="right">The other.</param>
    /// <returns>Negative, zero or positive as the bytes compare.</returns>
    public static int CompareBytewise(string left, string right)
    {
        byte[] a = CliText.Utf8.GetBytes(left);
        byte[] b = CliText.Utf8.GetBytes(right);
        return a.AsSpan().SequenceCompareTo(b.AsSpan());
    }

    /// <summary>
    /// The exit code one failure produces, frozen by <c>docs/9p/fixtures/conformance.md</c>:
    /// 1 protocol or transport error, 2 server error, 3 usage. It lives here rather than in the
    /// entry point so that a caller driving the commands in process reports the same codes.
    /// </summary>
    /// <param name="failure">The exception a command threw.</param>
    /// <returns>The exit code, or null when this is not a failure the cli knows how to report.</returns>
    public static int? ExitCodeFor(Exception failure) => failure switch
    {
        CliUsageException => 3,

        // Version negotiation failing carries no Rerror, only an answer the client cannot use
        // (reference §5.1 step 4), so it is a protocol error and not a server error.
        NinePVersionException or NinePProtocolException => 1,
        NinePException => 2,

        // Everything a transport can fail with before a single 9P byte has moved: a refused
        // connect, a name that does not resolve, a TLS handshake the peer or the trust decision
        // ended, a WebSocket upgrade that was not accepted. None of them carries an Rerror, so
        // none of them is a server error.
        IOException or System.Net.Sockets.SocketException or TimeoutException => 1,
        System.Security.Authentication.AuthenticationException => 1,
        System.Net.WebSockets.WebSocketException => 1,
        _ => null,
    };

    /// <summary>The one error line the cli prints, frozen by the conformance fixture.</summary>
    /// <param name="error">The error the server or the client produced.</param>
    /// <returns>The text written to standard error.</returns>
    public static string FormatError(NinePError error) => string.Format(
        CultureInfo.InvariantCulture, "error: {0} (errno {1})", error.Ename, error.Errno);

    private static string One(CliOptions options) =>
        options.Arguments.Count == 1
            ? options.Arguments[0]
            : throw new CliUsageException(options.Command + " takes exactly one path");

    private static string Pair(CliOptions options, int at) =>
        options.Arguments.Count == 2
            ? options.Arguments[at]
            : throw new CliUsageException("mv takes OLD and NEW");

    private static async Task VersionAsync(
        NinePSession session, Stream output, CancellationToken cancellationToken)
    {
        await WriteLineAsync(
            output,
            string.Format(
                CultureInfo.InvariantCulture,
                "dialect={0} msize={1}",
                NineP.Protocol.Negotiation.Negotiator.VersionString(session.Dialect),
                session.Msize),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ListAsync(
        NinePSession session, CliOptions options, Stream output, CancellationToken cancellationToken)
    {
        string path = One(options);

        // A listing of something that is not a directory is ENOTDIR, in every dialect and whatever
        // the server would have made of a Tread on it (conformance Part A step 5).
        Attr attr = await session.GetAttrAsync(path, cancellationToken).ConfigureAwait(false);
        if (attr.Kind != FileKind.Directory)
        {
            throw new NinePException(NinePError.FromErrno(Errno.ENOTDIR));
        }

        List<DirEntry> entries =
            [.. await session.ReadDirAsync(path, cancellationToken).ConfigureAwait(false)];

        // The order is by name, not by the line the name ends up on. Sorting the composed strings
        // put "-l" in kind order, because the kind is the first column — a listing ordered by
        // something the documented contract never mentions.
        entries.Sort(static (left, right) => CompareBytewise(left.Name, right.Name));

        foreach (DirEntry entry in entries)
        {
            string line = options.LongListing
                ? await DetailedAsync(session, path, entry, cancellationToken).ConfigureAwait(false)
                : Short(entry);

            await WriteLineAsync(output, line, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Short(DirEntry entry) =>
        entry.Kind == FileKind.Directory ? entry.Name + "/" : entry.Name;

    /// <summary>
    /// One <c>-l</c> line: <c>kind size name</c>. The size is the file's own, which costs one
    /// <c>stat</c> per entry — a directory read carries qids and names and not lengths. The qid
    /// path used to sit in that column, and it reads exactly like a size without being one.
    /// </summary>
    /// <param name="session">The attached session.</param>
    /// <param name="directory">The directory being listed.</param>
    /// <param name="entry">The entry to describe.</param>
    /// <param name="cancellationToken">Cancels the stat.</param>
    /// <returns>The line to print.</returns>
    private static async Task<string> DetailedAsync(
        NinePSession session, string directory, DirEntry entry, CancellationToken cancellationToken)
    {
        Attr attr = await session
            .GetAttrAsync(directory.TrimEnd('/') + "/" + entry.Name, cancellationToken)
            .ConfigureAwait(false);

        return string.Format(
            CultureInfo.InvariantCulture, "{0} {1} {2}", KindOf(entry.Kind), attr.Size, Short(entry));
    }

    private static async Task CatAsync(
        NinePSession session, string path, Stream output, CancellationToken cancellationToken)
    {
        // A directory read is EISDIR in .L and a listing in 9P2000; the cli answers the same in
        // both, because "cat a directory" is the caller's mistake either way.
        Attr attr = await session.GetAttrAsync(path, cancellationToken).ConfigureAwait(false);
        if (attr.Kind == FileKind.Directory)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EISDIR));
        }

        byte[] bytes = await session.ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task StatAsync(
        NinePSession session, string path, Stream output, CancellationToken cancellationToken)
    {
        Attr attr = await session.GetAttrAsync(path, cancellationToken).ConfigureAwait(false);

        await WriteLineAsync(
            output,
            string.Format(
                CultureInfo.InvariantCulture,
                "kind={0} size={1} perm={2} qid={3}.{4}.{5}",
                KindOf(attr.Kind),
                attr.Size,
                Convert.ToString((long)(attr.Perm & FilePermissions.Mask), 8),
                (byte)attr.Qid.Type,
                attr.Qid.Version,
                attr.Qid.Path),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAsync(
        NinePSession session, string path, Stream output, Stream input, CancellationToken cancellationToken)
    {
        using MemoryStream buffered = new();
        await input.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
        byte[] data = buffered.ToArray();

        try
        {
            await session.WriteFileAsync(path, data, cancellationToken).ConfigureAwait(false);
        }
        catch (NinePException failure) when (failure.Error.Errno == Errno.ENOENT)
        {
            // "write" creates what is not there yet, which is what conformance Part B step 2 does
            // straight after a mkdir.
            NinePFid created = await session.CreateFileAsync(path, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await using (created.ConfigureAwait(false))
            {
                await created.WriteAllAsync(data, cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteLineAsync(
            output,
            string.Format(CultureInfo.InvariantCulture, "wrote {0}", data.Length),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task MoveAsync(
        NinePSession session, CliOptions options, CancellationToken cancellationToken)
    {
        await session.RenameAsync(Pair(options, 0), Pair(options, 1), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string KindOf(FileKind kind) => kind switch
    {
        FileKind.Directory => "dir",
        FileKind.Symlink => "symlink",
        _ => "file",
    };

    private static async Task WriteLineAsync(
        Stream output, string line, CancellationToken cancellationToken)
    {
        // The output stream is bytes, not a TextWriter: the formats are byte-compared and must not
        // pick up a platform newline or an encoding preamble.
        await output.WriteAsync(CliText.Utf8.GetBytes(line + "\n"), cancellationToken)
            .ConfigureAwait(false);
    }
}
