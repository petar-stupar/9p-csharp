using System.Text;
using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Transports;
using NineP.Server;

namespace NineP.Docs.Tests.Snippets;

/// <summary>
/// The source of the README's two 60-second examples. These are the bytes the README carries,
/// character for character: <c>ReadmeSnippetTests</c> extracts the regions below, compares them
/// with the fenced blocks between <c>&lt;!-- snippet:server --&gt;</c> and
/// <c>&lt;!-- snippet:client --&gt;</c>, and then <b>runs</b> them. Compiling them is what keeps
/// them valid C#; running them is what keeps them true; comparing them is what keeps the README
/// from drifting away from either.
/// </summary>
internal static class ReadmeSnippets
{
    // snippet:server
    static async Task<(NinePServer Server, Task Serving)> StartHelloServerAsync(NinePAddress address)
    {
        var options = new ServerOptions { Listen = [address] };
        var server = new NinePServer(options);

        // ServeAsync runs until the server is stopped, so hold its task rather than awaiting it.
        Task serving = server.ServeAsync(new HelloFilesystem());
        await server.ListeningAsync();

        return (server, serving);
    }
    // endsnippet

    // snippet:client
    static async Task<string> ReadHelloAsync(NinePAddress address)
    {
        var options = new ClientOptions { Uname = Environment.UserName };

        await using NinePSession session = await NinePClient.ConnectAsync(address, options);
        await using NinePFid root = await session.AttachAsync();

        foreach (DirEntry entry in await session.ReadDirAsync("/"))
        {
            Console.WriteLine(entry.Name);
        }

        return Encoding.UTF8.GetString(await session.ReadFileAsync("/hello.txt"));
    }
    // endsnippet

    /// <summary>Runs both examples against each other, which is what the test asserts on.</summary>
    /// <returns>What the client read out of the server's tree.</returns>
    public static async Task<string> RunAsync()
    {
        (NinePServer server, Task serving) =
            await StartHelloServerAsync(NinePAddress.Parse("tcp://127.0.0.1:0"));

        await using (server.ConfigureAwait(false))
        {
            string greeting = await ReadHelloAsync(server.Endpoints[0]);

            await server.StopAsync(TimeSpan.FromSeconds(5));
            await serving.ConfigureAwait(false);

            return greeting;
        }
    }
}

/// <summary>
/// The source of the README's <c>&lt;!-- snippet:tree --&gt;</c> block: a filesystem of one
/// read-only file. A developer writes handlers and nothing else — no T-message, no tag, no fid, no
/// dialect and no error shape appears below, because the server core owns all of them.
/// </summary>
// snippet:tree
sealed class HelloFilesystem : IFilesystem, IDirectoryHandler
{
    static readonly byte[] Greeting = "hello, 9P\n"u8.ToArray();
    static readonly HelloFile File = new(Greeting);

    public Qid Qid => new(QidType.QTDIR, 0, 1);

    public ValueTask<IDirectoryHandler> AttachAsync(
        Identity identity, string aname, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IDirectoryHandler>(this);

    public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new Attr
        {
            Qid = Qid,
            Kind = FileKind.Directory,
            Perm = FilePermissions.OwnerAll                  // 0755
                 | FilePermissions.GroupReadExecute
                 | FilePermissions.OtherReadExecute,
        });

    public ValueTask<IHandler?> LookupAsync(string name, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IHandler?>(name == "hello.txt" ? File : null);

    public ValueTask<DirectoryListing> ReadDirAsync(
        ulong cursor, int max, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(cursor == 0
            ? new DirectoryListing([new DirEntry("hello.txt", File.Qid, FileKind.File, 1)], 1, true)
            : new DirectoryListing([], cursor, true));

    // Everything this tree cannot do is one line each, and the core turns the exception into the
    // right error shape for whichever dialect the session negotiated.
    public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EROFS));

    public ValueTask<IHandler> CreateAsync(CreateRequest request, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EROFS));

    public ValueTask RemoveAsync(string name, FileKind kind, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EROFS));

    public ValueTask RenameAsync(
        string oldName, IDirectoryHandler newParent, string newName, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EROFS));

    public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

sealed class HelloFile(byte[] contents) : IFileHandler, IOpenFile
{
    public Qid Qid => new(QidType.QTFILE, 0, 2);

    public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new Attr
        {
            Qid = Qid,
            Kind = FileKind.File,
            Perm = FilePermissions.AllRead,                  // 0444
            Size = (ulong)contents.Length,
        });

    public ValueTask<IOpenFile> OpenAsync(
        OpenMode mode, OpenFlags flags, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IOpenFile>(this);

    public ValueTask<int> ReadAsync(
        ulong offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int at = (int)Math.Min(offset, (ulong)contents.Length);
        int count = Math.Min(buffer.Length, contents.Length - at);

        contents.AsMemory(at, count).CopyTo(buffer);
        return ValueTask.FromResult(count);
    }

    public ValueTask<int> WriteAsync(
        ulong offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EROFS));

    public ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult((ulong)contents.Length);

    public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EROFS));

    public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
// endsnippet
