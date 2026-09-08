# ninep — 9P2000 / 9P2000.u / 9P2000.L for C# / .NET

[![ci](https://github.com/petar-stupar/9p-csharp/actions/workflows/ci.yml/badge.svg)](https://github.com/petar-stupar/9p-csharp/actions/workflows/ci.yml)
[![NuGet NineP.Protocol](https://img.shields.io/nuget/v/NineP.Protocol?label=NineP.Protocol)](https://www.nuget.org/packages/NineP.Protocol)
[![NuGet NineP.Client](https://img.shields.io/nuget/v/NineP.Client?label=NineP.Client)](https://www.nuget.org/packages/NineP.Client)
[![NuGet NineP.Server](https://img.shields.io/nuget/v/NineP.Server?label=NineP.Server)](https://www.nuget.org/packages/NineP.Server)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)](https://github.com/petar-stupar/9p-csharp/blob/main/global.json)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/petar-stupar/9p-csharp/blob/main/LICENSE)

Three packages: a shared **protocol** package (typed messages for all 34 T/R pairs, a bounded
zero-copy codec, TCP / TLS / WebSocket / in-memory / user-defined transports, and the
authentication interfaces), a **client** package, and a **server** package driven by per-file-type
handlers. Two example servers — `jsonfs` and `todofs` — and a conformance `cli` ship in the
repository but are not published.

All three 9P2000 dialects are implemented and negotiated separately: **9P2000**, **9P2000.u** and
**9P2000.L**. The umbrella name "9P2000.uL" never goes on the wire.

| Package | What it holds |
| --- | --- |
| [`NineP.Protocol`](https://github.com/petar-stupar/9p-csharp/tree/main/src/NineP.Protocol) | messages, codec, dialects, the unified `Attr` model, transports, errors, auth interfaces |
| [`NineP.Client`](https://github.com/petar-stupar/9p-csharp/tree/main/src/NineP.Client) | `NinePClient`, `NinePSession`, `NinePFid`, the pipelined tag multiplexer |
| [`NineP.Server`](https://github.com/petar-stupar/9p-csharp/tree/main/src/NineP.Server) | `NinePServer`, the handler interfaces, fid and tag tables, dispatch, permissions |

Target frameworks `net8.0` and `net10.0`. MIT licensed. Version `0.1.0`; see
[CHANGELOG.md](https://github.com/petar-stupar/9p-csharp/blob/main/CHANGELOG.md).

## Install

```text
dotnet add package NineP.Client --version 0.1.0
dotnet add package NineP.Server --version 0.1.0
```

`NineP.Protocol` comes with either of them; add it directly only if you are writing a peer that is
neither, such as a proxy or a transport.

```text
dotnet add package NineP.Protocol --version 0.1.0
```

The packages log through `Microsoft.Extensions.Logging.Abstractions` 8.0.3 — `ILogger`, the
abstraction your host already has — which brings `Microsoft.Extensions.DependencyInjection.Abstractions`
8.0.2 with it. On `net8.0` add `System.IO.Pipelines` 8.0.0, which is inbox from `net9.0` onward. That
is the whole runtime dependency set: three packages on `net8.0`, two on `net10.0`. The logging
version is the lowest one carrying the `[LoggerMessage]` generator and is deliberately not raised,
so your application chooses the version rather than being forced up by this one.

## The 60-second server

A filesystem is a tree of handlers. You implement `IFilesystem` plus one handler interface per file
type; the server core owns version negotiation, the fid and tag tables, `Tflush`, walk semantics,
open state, directory packing, `iounit`, permission checks and the dialect projection of every
attribute — none of which appears below.

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

The tree it serves is one read-only file:

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
        ValueTask.FromResult(new Attr { Qid = Qid, Kind = FileKind.Directory, Perm = 0x1ED });

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
            Perm = 0x124,
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

## The 60-second client

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

`session.ReadDirAsync`, `ReadFileAsync`, `WriteFileAsync`, `MkdirAsync`, `RemoveAsync`,
`RenameAsync`, `GetAttrAsync`, `SymlinkAsync` and `ReadlinkAsync` are the path-shaped API;
`session.Messages` is the one-method-per-T-message API underneath it, and `NinePFid` is the handle
in between. Reads and writes are chunked at `iounit` with four requests outstanding by default,
which is where the throughput in [docs/benchmarks.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/benchmarks.md) comes from.

## Both halves, in one program

This is the program CI installs the packed packages into and runs, and its output is diffed against
the block below it. `ReadmeSnippetTests` and `PackagingTests` run it too, so it cannot drift.

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
        ValueTask.FromResult(new Attr { Qid = Qid, Kind = FileKind.Directory, Perm = 0x1ED });

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
            Perm = 0x124,
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

- **Every message** of all three dialects: 34 T/R pairs, 66 records, all 77 golden vectors of
  `docs/9p/fixtures/wire-vectors.json` round-tripping byte-exactly.
- **Transports**: `tcp://`, `tls://` (1.2 floor, 1.3 preferred, optional mutual TLS), `ws://` and
  `wss://` (RFC 6455, one 9P message per binary frame, origin allow-list), and `memory://` in
  process. Implementing `ITransport` adds your own — see [docs/transports.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/transports.md).
- **Authentication**: the `Tauth` afid exchange with `TokenAuthenticator` and
  `PasswordAuthenticator` (PBKDF2-HMAC-SHA-256, 600 000 iterations) out of the box,
  `TlsClientCertAuthenticator` for mutual TLS, and `IAuthenticator` for your own — the Keycloak
  bearer-token authenticator in `todofs` is the worked example. Plan 9's `p9any`/`p9sk1` is **out of
  scope**: it is DES-based and is not production security.
- **Resource caps** on everything a client controls: msize, the pre-negotiation frame size, fids,
  in-flight requests per connection and per listener, connections per listener, header timeouts,
  and the size and duration of an afid exchange. See [docs/security.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/security.md).

## Authentication

A server authenticates through `ServerOptions.Authenticator`. When one is configured, a client
runs the `Tauth` exchange over an afid and the session then runs as the identity the
**authenticator** produced — never as the `uname` the client claimed. The afid is bound to the
`(uname, n_uname, aname)` triple it was created with, so an afid obtained for one identity cannot
be presented by an attach claiming another. The exchange itself is bounded by
`Limits.MaxAuthBytes` (64 KiB per direction) and `Limits.AuthTimeout` (30 s).

**With no authenticator configured, a `Tattach` carrying `afid = NOFID` is accepted and the
session runs as the identity the client claimed.** That is the correct behaviour for a server
behind an already-authenticated transport — a Unix socket, mutual TLS, or a private network — and
it is the wrong behaviour anywhere else. A server reachable by an untrusted peer must set
`ServerOptions.Authenticator`; an authenticator whose `IsRequired` is true refuses a `NOFID`
attach with `EACCES` / `"authentication failed"`. A server with no authenticator refuses `Tauth`
itself: `Rerror "authentication not required"` in 9P2000 and 9P2000.u, `Rlerror ECONNREFUSED` in
9P2000.L.

Full detail, including how a token is obtained: [docs/auth.md](https://github.com/petar-stupar/9p-csharp/blob/main/docs/auth.md).

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
| `docs/api/` | the generated API reference (`dotnet tool restore && dotnet docfx metadata && dotnet docfx build`) |

## Building and testing

```text
dotnet build -warnaserror && dotnet test && dotnet format --verify-no-changes
dotnet run --project tests/NineP.Conformance -- self
dotnet pack -c Release -o artifacts
```

Warnings are build failures and suppressions carry a reason at the point of suppression. The
conformance run drives this repository's own `jsonfs` and `ninep` as processes, in all three
dialects over TCP, TLS, WebSocket and the in-memory transport, and diffs the result against
`docs/9p/fixtures/sample.expected.txt`.

## Contributing

`main` is protected: nobody pushes to it, and only the owner merges. Fork the repository, branch
from `main`, keep the gate above green, and open a pull request; [CONTRIBUTING.md](https://github.com/petar-stupar/9p-csharp/blob/main/CONTRIBUTING.md)
has the details, including the dependency and documentation rules a change must satisfy.

## Licence

MIT. See [LICENSE](https://github.com/petar-stupar/9p-csharp/blob/main/LICENSE).
