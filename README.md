# ninep — 9P2000 / 9P2000.u / 9P2000.L for C# / .NET

[![ci](https://github.com/petar-stupar/9p-csharp/actions/workflows/ci.yml/badge.svg)](https://github.com/petar-stupar/9p-csharp/actions/workflows/ci.yml)
[![NuGet NineP.Protocol](https://img.shields.io/nuget/v/NineP.Protocol?label=NineP.Protocol)](https://www.nuget.org/packages/NineP.Protocol)
[![NuGet NineP.Client](https://img.shields.io/nuget/v/NineP.Client?label=NineP.Client)](https://www.nuget.org/packages/NineP.Client)
[![NuGet NineP.Server](https://img.shields.io/nuget/v/NineP.Server?label=NineP.Server)](https://www.nuget.org/packages/NineP.Server)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)](https://github.com/petar-stupar/9p-csharp/blob/main/global.json)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/petar-stupar/9p-csharp/blob/main/LICENSE)

**9P** is the file protocol of Plan 9: a client walks a tree of named files over a byte stream,
and a server answers with whatever it chooses to present as files, from a real disk to a JSON
document to a database. Linux mounts it as `v9fs`, QEMU and WSL share host directories over it,
and it is the simplest sane way to give a program a filesystem-shaped API over a socket.

`ninep` is a complete, production-grade implementation for .NET of all three dialects in use
today, **9P2000**, **9P2000.u** and **9P2000.L**, each negotiated separately and none invented.
You write a client in a dozen lines, or a server as a tree of small handler classes, and the
library owns the protocol: negotiation, fids and tags, `Tflush`, walks, open state, directory
packing, permissions, and every one of the wire-level rules a hostile peer would test.

| Package | What it holds |
| --- | --- |
| [`NineP.Protocol`](https://github.com/petar-stupar/9p-csharp/tree/main/src/NineP.Protocol) | the typed messages of all 34 T/R pairs, a bounded zero-copy codec, the dialects and the unified `Attr` model, the TCP / TLS / WebSocket / in-memory transports, errors and the authentication interfaces |
| [`NineP.Client`](https://github.com/petar-stupar/9p-csharp/tree/main/src/NineP.Client) | `NinePClient`, `NinePSession`, `NinePFid`: a pipelined client with a path-shaped file API and a one-method-per-message API underneath |
| [`NineP.Server`](https://github.com/petar-stupar/9p-csharp/tree/main/src/NineP.Server) | `NinePServer` and the handler interfaces: you implement one class per file type and the server core does the rest |

Targets `net8.0` and `net10.0`. MIT licensed. Version `0.1.0`; the history is in
[CHANGELOG.md](https://github.com/petar-stupar/9p-csharp/blob/main/CHANGELOG.md). Two example servers, `jsonfs` and `todofs`, and a conformance
`ninep` command-line tool live in the repository and are not published.

## Install

```text
dotnet add package NineP.Client
dotnet add package NineP.Server
```

`NineP.Protocol` comes with either. Add it on its own only for a peer that is neither, such as a
proxy or a transport.

The packages log through `Microsoft.Extensions.Logging.Abstractions` (`ILogger`, at the 8.0 floor
so your application chooses the version) and, on `net8.0` only, use `System.IO.Pipelines`. That is
the whole runtime dependency set.

## A server in sixty seconds

A filesystem is a tree of handlers. Implement `IFilesystem` and one handler interface per file
type; nothing below touches a message, a fid or a dialect.

<!-- snippet:server -->
```csharp
static async Task<(NinePServer Server, Task Serving)> StartHelloServerAsync(NinePAddress address)
{
    var options = new ServerOptions { Listen = [address] };
    var server = new NinePServer(options);

    // ServeAsync runs until the server is stopped, so hold its task rather than awaiting it.
    Task serving = server.ServeAsync(new HelloFilesystem());
    await server.ListeningAsync();

    return (server, serving);
}
```

The tree behind it: one directory holding one read-only file.

<!-- snippet:tree -->
```csharp
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
```

Every T-message maps onto exactly one handler method; [docs/server.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/server.md) has the
table. Optional capabilities such as locks, extended attributes, links and `statfs` are separate
interfaces, and a handler that lacks one is answered `EOPNOTSUPP` rather than crashing.

## A client in sixty seconds

<!-- snippet:client -->
```csharp
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
```

`ReadDirAsync`, `ReadFileAsync`, `WriteFileAsync`, `MkdirAsync`, `RemoveAsync`, `RenameAsync`,
`GetAttrAsync`, `SymlinkAsync` and `ReadlinkAsync` are the path-shaped API on `NinePSession`;
`NinePFid` is the handle in between, and `session.Messages` is the one-method-per-T-message API
underneath. Reads and writes are chunked at `iounit` with four requests in flight by default,
which is where the throughput in [docs/benchmarks.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/benchmarks.md) comes from.

## Both halves in one program

Paste this into a console application that references `NineP.Client` and `NineP.Server`, and it
prints the two lines below. CI installs the packed packages into exactly such a program and diffs
its output, and the test suite runs it too, so it cannot drift from what ships.

<!-- ci:snippet -->
```csharp
using System.Text;
using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Transports;
using NineP.Server;

var options = new ServerOptions { Listen = [NinePAddress.Parse("tcp://127.0.0.1:0")] };
await using var server = new NinePServer(options);

Task serving = server.ServeAsync(new HelloFilesystem());
await server.ListeningAsync();

await using (NinePSession session = await NinePClient.ConnectAsync(
    server.Endpoints[0], new ClientOptions { Uname = Environment.UserName }))
{
    await using NinePFid root = await session.AttachAsync();

    foreach (DirEntry entry in await session.ReadDirAsync("/"))
    {
        Console.WriteLine(entry.Name);
    }

    Console.Write(Encoding.UTF8.GetString(await session.ReadFileAsync("/hello.txt")));
}

await server.StopAsync(TimeSpan.FromSeconds(5));
await serving;

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
```

<!-- ci:expected -->
```text
hello.txt
hello, 9P
```
<!-- ci:end -->

## What is implemented

- **Every message** of all three dialects: 34 T/R pairs, 66 records, and the 77 golden wire
  vectors round-tripping byte-exactly.
- **Transports**: `tcp://`, `tls://` (TLS 1.2 floor, 1.3 preferred, optional mutual TLS), `ws://`
  and `wss://` (RFC 6455, one 9P message per binary frame, origin allow-list), and `memory://`
  in process. Implementing `ITransport` adds your own; see
  [docs/transports.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/transports.md).
- **Authentication**: the `Tauth` afid exchange, with `TokenAuthenticator` and
  `PasswordAuthenticator` (PBKDF2-HMAC-SHA-256, 600 000 iterations) shipped,
  `TlsClientCertAuthenticator` for mutual TLS, and `IAuthenticator` for your own; the Keycloak
  bearer-token authenticator in `todofs` is the worked example. Plan 9's `p9any`/`p9sk1` is out of
  scope: it is DES-based and not production security.
- **Resource caps** on everything a client controls: msize, the pre-negotiation frame size, fids,
  in-flight requests per connection and per listener, connections per listener, header timeouts,
  and the size and duration of an afid exchange. See [docs/security.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/security.md).
- **Honest failure**: nothing silent maps to success. A request the negotiated dialect cannot
  carry is refused rather than sent with a field dropped, and every framing violation closes the
  connection with a logged reason.

## Interop

Measured against other implementations, in both directions, on 2026-09-10; every row is an opt-in
test in the suite that anyone can rerun (`tests/interop/setup.sh` fetches the peers). The detail,
the commands and what the runs found are in
[docs/interop.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/interop.md).

| peer | role | dialects | result |
| --- | --- | --- | --- |
| Linux kernel v9fs (6.1, Debian 12) | client, mounts our server | 9P2000, 9P2000.u, 9P2000.L | pass |
| diod 1.0.24 | server, driven by our client | 9P2000.L | pass |
| hugelgupf/p9 `p9ufs` v0.4.1 | server, driven by our client | 9P2000.L | pass |
| plan9port `9p` (2026-08-26) | client, reads our server | 9P2000 | pass |

## Before you expose a server

A server with no `ServerOptions.Authenticator` accepts a `Tattach` with `afid = NOFID` and runs
the session as the identity the client **claimed**. That is right behind an already-authenticated
transport, such as mutual TLS or a private network, and wrong anywhere else. A server an untrusted
peer can reach must set an authenticator; one whose `IsRequired` is true refuses a `NOFID` attach
with `EACCES`. When an authenticator is configured the session runs as the identity **it**
produced, never the claimed `uname`, and the afid is bound to the `(uname, n_uname, aname)` it was
created for. [docs/auth.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/auth.md) has the full picture, including how a token is
obtained.

## Documentation

| | |
| --- | --- |
| [docs/api.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/api.md) | the complete public API, type by type and member by member |
| [docs/protocol.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/protocol.md) | the wire protocol and how it maps onto the C# types |
| [docs/server.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/server.md) | the handler table: every T-message to the handler method that serves it |
| [docs/client.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/client.md) | sessions, fids, pipelining, cancellation and the file API |
| [docs/transports.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/transports.md) | the addresses, the four shipped transports, and writing your own |
| [docs/auth.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/auth.md) | the afid exchange, the shipped authenticators, and OIDC |
| [docs/examples.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/examples.md) | `jsonfs`, `todofs` and the `ninep` cli |
| [docs/security.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/security.md) | the caps, the validation rules, and what is out of scope |
| [docs/benchmarks.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/benchmarks.md) | measured throughput, latency, peak RSS and codec ns/op |
| [docs/interop.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/interop.md) | what this implementation has been run against, and what it has not |
| [docs/releasing.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/releasing.md) | how a version reaches nuget.org: tag, checks, Trusted Publishing |
| [`docs/api/`](https://github.com/petar-stupar/9p-csharp/tree/main/docs/api) | the generated API reference (`dotnet tool restore && dotnet docfx metadata && dotnet docfx build`) |

## Building and testing

```text
dotnet build -warnaserror && dotnet test && dotnet format --verify-no-changes
dotnet run --project tests/NineP.Conformance -- self
dotnet pack -c Release -o artifacts
```

Warnings are build failures and every suppression carries a reason. The conformance run drives
the repository's own `jsonfs` and `ninep` as processes, in all three dialects over TCP, TLS,
WebSocket and the in-memory transport, and diffs the result against a fixed expected output. CI
runs the same gate on Linux, macOS and Windows.

## Contributing

`main` is protected: nobody pushes to it, and only the owner merges. Fork the repository, branch
from `main`, keep the gate above green, and open a pull request.
[CONTRIBUTING.md](https://github.com/petar-stupar/9p-csharp/blob/main/CONTRIBUTING.md) has the details, including the dependency and
documentation rules a change must satisfy.

## Licence

MIT. See [LICENSE](https://github.com/petar-stupar/9p-csharp/blob/main/LICENSE).
