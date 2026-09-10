# jsonfs

`jsonfs` serves one JSON document as a 9P tree: objects and arrays become directories, scalars
become files. It is the server the conformance suite runs against, and the smallest complete
example of the handler model. What the tree looks like, how keys are escaped and how writes are
typed is in [docs/examples.md](../../docs/examples.md#jsonfs); this file is about running it.

Every command below is run from the repository root and assumes the .NET 10 SDK pinned by
[global.json](../../global.json). The examples are not packed, so they run out of the build tree.

## Run it

```text
dotnet build
dotnet run --project examples/NineP.JsonFs -- --listen tcp://127.0.0.1:5640 --file docs/9p/fixtures/sample.json
```

The server prints one `listening <address> file=<path> writable=<yes|no>` line per address once it
is bound, then serves until Ctrl-C. A `--listen` port of `0` asks the kernel for a free one, and the
printed line is how you learn it.

```text
usage: jsonfs --listen <url> [--listen <url>...] --file <path.json> [--writable]
              [--write-back] [--write-back-delay <ms>] [--max-entries <n>]
              [--dialects 9P2000,9P2000.u,9P2000.L]
              [--auth none|token:<secret>|password-file:<path>]
              [--tls-cert <pem> --tls-key <pem> --tls-client-ca <pem>]
              [--ws-origin <origin>...] [--msize <bytes>] [--log <level>]
```

| Flag | Meaning |
| --- | --- |
| `--listen <url>` | `tcp://host:port`, `tls://host:port`, `ws://host:port/path` or `wss://host:port/path`; repeatable |
| `--file <path.json>` | the document to serve; refused above 64 MiB, 256 levels of nesting or `--max-entries` entries |
| `--writable` | allow writes, creates, removes and renames in memory; the default is read-only |
| `--write-back` | **implies `--writable`** (there is nothing to write back from a read-only server), and rewrites the source file atomically after each change |
| `--write-back-delay <ms>` | **implies `--write-back`**: coalesce every change inside a window of this many milliseconds into one rewrite; `0` (the default) rewrites inside each change, and a graceful stop writes back what the last window still owes |
| `--max-entries <n>` | the most entries (object keys and array elements, anywhere in the document) the served document may hold, `100000` by default; a create or `mkdir` past it is `ENOSPC`, and a document already past it is refused at startup |
| `--dialects` | which of `9P2000`, `9P2000.u`, `9P2000.L` to offer; all three by default |
| `--auth` | the authenticator, see below; `none` by default |
| `--tls-cert`, `--tls-key` | the server certificate and key, PEM, needed by `tls://` and `wss://` |
| `--tls-client-ca` | a PEM bundle of roots; when given, every TLS client must present a certificate that chains to one |
| `--ws-origin` | an `Origin` the WebSocket listener accepts; repeatable, and without it any origin is accepted |
| `--msize` | the largest message size to negotiate |
| `--log` | `trace`, `debug`, `info`, `warn` (default), `error` or `none`; output goes to standard error |

Exit codes: `0` stopped on request, `1` a runtime failure such as a port in use, `3` a bad command
line or a document jsonfs will not serve.

Talk to it with the conformance client:

```text
dotnet run --project examples/NineP.Cli -- --addr tcp://127.0.0.1:5640 ls -l /
dotnet run --project examples/NineP.Cli -- --addr tcp://127.0.0.1:5640 cat /greeting
```

With `--writable`, `ninep write`, `mkdir`, `rm` and `mv` change the tree in memory; add
`--write-back` to have each change written to the document on disk. `--write-back` on its own is
enough: it turns writing on, because a read-only server has no change to write back, and the
usage text says so rather than leaving the implication to be discovered.

A `--tls-*` flag with no `tls://` or `wss://` `--listen`, or a `--ws-origin` with no `ws://` or
`wss://` one, is accepted and unused; jsonfs logs a warning at startup naming the flag, because a
certificate that is loaded and never presented reads as a server that is protected when it is not.
Raise `--log info` or leave it at the default `warn` to see it.

`mkdir` and file creation ignore the mode the client asked for: a jsonfs file is always `0644` and
a directory always `0755`. JSON has nowhere to keep a mode, a group or a create flag, so
`CreateRequest.Perm`, `.Gid` and `.Flags` are dropped rather than refused — refusing a mode would
make every ordinary `mkdir` from Linux fail, since v9fs always sends one.

## Authentication

### A shared token

```text
dotnet run --project examples/NineP.JsonFs -- --listen tcp://127.0.0.1:5640 --file docs/9p/fixtures/sample.json --auth token:hunter2
dotnet run --project examples/NineP.Cli -- --addr tcp://127.0.0.1:5640 --auth token:hunter2 ls /
```

The client writes the secret to the afid, and the server compares it in constant time. A client
that attaches without a credential gets `permission denied`.

### A password file

`--auth password-file:<path>` verifies `user` and `password` against a text file of one credential
per line. The hash is PBKDF2-HMAC-SHA-256 with a random 16-byte salt and at least 600 000
iterations, base64-encoded, and the file looks like this; blank lines and lines starting with `#`
are ignored:

```text
# user:pbkdf2-sha256$iterations$salt$hash
glenda:pbkdf2-sha256$600000$EzBGnmBd8z0GOaSLhk8nTg==$NNxbNzYLLk2Il5N4l4SpIl64o+YmkKXYGEtZ79HR7l4=
```

There is no command-line tool for producing a line; the hashing function is
`PasswordFileStore.HashPassword` in `NineP.Protocol`, and the .NET 10 SDK can run a single C# file
against the project directly. Save this as `hash-password.cs` anywhere outside the repository:

```csharp
#:project /path/to/9p-csharp/src/NineP.Protocol/NineP.Protocol.csproj
using NineP.Protocol.Auth;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: dotnet run hash-password.cs -- <user> <password>");
    return 3;
}

Console.WriteLine(args[0] + ":" + PasswordFileStore.HashPassword(args[1]));
return 0;
```

Then:

```text
dotnet run hash-password.cs -- glenda hunter2 >> passwd
dotnet run --project examples/NineP.JsonFs -- --listen tcp://127.0.0.1:5640 --file docs/9p/fixtures/sample.json --auth password-file:passwd
```

The `ninep` cli has no password credential, so it cannot log in to this server; it exists to drive
the conformance scenario, which uses tokens. A client that can is a few lines of `NineP.Client`,
and it too runs as a single file:

```csharp
#:project /path/to/9p-csharp/src/NineP.Client/NineP.Client.csproj
using NineP.Client;
using NineP.Protocol.Auth;
using NineP.Protocol.Transports;

// usage: dotnet run password-client.cs -- tcp://127.0.0.1:5640 <user> <password>
await using NinePSession session = await NinePClient.ConnectAsync(
    NinePAddress.Parse(args[0]),
    new ClientOptions { Uname = args[1], Credential = new PasswordCredential(args[1], args[2]) });

await session.AttachAsync();
foreach (var entry in await session.ReadDirAsync("/"))
{
    Console.WriteLine(entry.Name);
}
```

A wrong password, or a user the file does not name, is `permission denied`, and the two cost the
server the same amount of time.

## TLS

`tls://` and `wss://` listeners need a certificate and key in PEM. For development, make a small
certificate authority of your own and sign a server certificate with it. The certificate must name
the address clients will dial in its subject alternative names, or the client refuses it as being
for another host; the example covers both `127.0.0.1` and `localhost`.

```text
# a CA
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 365 \
  -subj "/CN=ninep dev CA" \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" \
  -keyout ca-key.pem -out ca.pem

# a server certificate signed by it
openssl req -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes \
  -subj "/CN=ninep dev server" -addext "subjectAltName=IP:127.0.0.1,DNS:localhost" \
  -keyout server-key.pem -out server.csr
openssl x509 -req -in server.csr -CA ca.pem -CAkey ca-key.pem -CAcreateserial -days 365 \
  -copy_extensions copy -out server.pem
```

Keep `ca-key.pem` out of the repository and off the server; only `ca.pem` is handed to clients.

```text
dotnet run --project examples/NineP.JsonFs -- --listen tls://127.0.0.1:5645 --listen wss://127.0.0.1:5648/9p \
  --file docs/9p/fixtures/sample.json --tls-cert server.pem --tls-key server-key.pem

dotnet run --project examples/NineP.Cli -- --addr tls://127.0.0.1:5645 --tls-ca ca.pem ls /
dotnet run --project examples/NineP.Cli -- --addr wss://127.0.0.1:5648/9p --tls-ca ca.pem version
```

`--tls-ca` makes the client trust that CA in addition to the machine's store. Without it, a
certificate from a private CA is refused: `The remote certificate was rejected`. There is no flag
to skip verification.

TLS 1.2 is the floor and 1.3 is preferred; the version is fixed by the library rather than left to
the machine. A `wss://` listener with no `--ws-origin` accepts any origin and logs a warning saying
so; set it before exposing the listener to browsers.

### Mutual TLS

`--tls-client-ca` turns on client certificates: every TLS connection must present one that chains
to a root in that bundle, or the handshake is refused before a single 9P byte is read. Sign a
client certificate with the same CA:

```text
openssl req -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes \
  -subj "/CN=glenda" -addext "subjectAltName=DNS:glenda" -addext "extendedKeyUsage=clientAuth" \
  -keyout client-key.pem -out client.csr
openssl x509 -req -in client.csr -CA ca.pem -CAkey ca-key.pem -CAcreateserial -days 365 \
  -copy_extensions copy -out client.pem

dotnet run --project examples/NineP.JsonFs -- --listen tls://127.0.0.1:5646 --file docs/9p/fixtures/sample.json \
  --tls-cert server.pem --tls-key server-key.pem --tls-client-ca ca.pem

dotnet run --project examples/NineP.Cli -- --addr tls://127.0.0.1:5646 --tls-ca ca.pem \
  --tls-cert client.pem --tls-key client-key.pem stat /
```

A client without a certificate sees `the server closed the connection`. Note what this does and
does not do in jsonfs: the certificate gates the transport, and `--auth` still decides the 9P
identity. Wiring the certificate's name into the identity is what `TlsClientCertAuthenticator` in
`NineP.Protocol` is for, and [docs/auth.md](../../docs/auth.md) shows how; jsonfs does not expose
it as a flag.

## Conformance

The scenario in [docs/9p/fixtures/conformance.md](../../docs/9p/fixtures/conformance.md) runs
against this server and the cli, across every dialect:

```text
dotnet run --project tests/NineP.Conformance -- self
```
