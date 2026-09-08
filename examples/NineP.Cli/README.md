# ninep

`ninep` is the conformance client: a small command-line 9P client whose output formats are frozen
by [docs/9p/fixtures/conformance.md](../../docs/9p/fixtures/conformance.md), so that every
language in the workspace can be diffed against one expected file. It is also the quickest way to
poke at any 9P server, including the two examples beside it. The commands and their exact output
are described in [docs/examples.md](../../docs/examples.md#ninep); this file is about running it
and getting it connected.

Every command below is run from the repository root and assumes the .NET 10 SDK pinned by
[global.json](../../global.json).

## Run it

```text
dotnet build
dotnet run --project examples/NineP.Cli -- --addr tcp://127.0.0.1:5640 ls -l /
```

```text
usage: ninep [--addr <url>] [--dialect 9P2000|9P2000.u|9P2000.L] [--uname <u>]
             [--aname <a>] [--auth none|token:<secret>|bearer:<token>|oidc-device|oidc-password]
             [--auth-optional] [--oidc-issuer <url>] [--oidc-client-id <id>]
             [--msize <bytes>] [--tls-ca <pem>] [--tls-cert <pem> --tls-key <pem>]
             <command> [args]

commands: version | ls [-l] PATH | cat PATH | stat PATH | write PATH
          mkdir PATH | rm PATH | mv OLD NEW | readlink PATH
```

A flag that cannot apply to the rest of the command line is a usage error (exit `3`), not a flag
that is read and then ignored: `-l` belongs to `ls` alone, `--tls-ca` / `--tls-cert` / `--tls-key`
need a `tls://` or `wss://` `--addr`, and `--oidc-issuer` / `--oidc-client-id` need `--auth
oidc-device` or `--auth oidc-password`. The command line is checked before anything is dialled, so
these are exit `3` and never a connection failure that happens to be exit `1`.

`write PATH` takes its data from standard input, truncates, and creates the file if it is absent.
`--addr` defaults to `tcp://127.0.0.1:564`. `--uname` defaults to your local user name, because a
server owns an attached tree by the attaching user. `--dialect` pins one dialect; otherwise the
client offers `9P2000.L`, then `.u`, then `9P2000`.

Exit codes: `0` success, `1` a protocol or transport error, `2` the server refused something
(`Rerror` or `Rlerror`, printed as `error: <ename> (errno <n>)`), `3` usage.

To have a server to talk to:

```text
dotnet run --project examples/NineP.JsonFs -- --listen tcp://127.0.0.1:5640 --file docs/9p/fixtures/sample.json
```

## Addresses and TLS

| `--addr` | Transport |
| --- | --- |
| `tcp://host:port` | plain TCP |
| `tls://host:port` | TLS over TCP |
| `ws://host:port/path` | WebSocket |
| `wss://host:port/path` | WebSocket over TLS |

The TLS flags apply to `tls://` and `wss://` only. With a `tcp://` or `ws://` address they are a
**usage error** naming the flag and the scheme (exit `3`), rather than a certificate that is loaded
and then never presented — which is how a connection a caller believed was encrypted turns out not
to be.

- `--tls-ca <pem>` adds a root to trust, in addition to the machine's store. A server certificate
  from a private CA is refused without it, and there is no flag to skip verification. The
  certificate must name the host you dial, `127.0.0.1` or `localhost` for a local server.
- `--tls-cert <pem> --tls-key <pem>` present a client certificate, for a server that requires one.

How to make a development CA, a server certificate and a client certificate with `openssl` is in
[the jsonfs README](../NineP.JsonFs/README.md#tls); the same files work against `todofs`.

```text
dotnet run --project examples/NineP.Cli -- --addr tls://127.0.0.1:5645 --tls-ca ca.pem ls /
dotnet run --project examples/NineP.Cli -- --addr tls://127.0.0.1:5646 --tls-ca ca.pem \
  --tls-cert client.pem --tls-key client-key.pem stat /
```

## Authentication

| `--auth` | What is sent over the afid | Use against |
| --- | --- | --- |
| `none` | nothing; a `NOFID` attach | a server with no authenticator (the default) |
| `token:<secret>` | the secret itself | `jsonfs --auth token:<secret>` |
| `bearer:<token>` | an OIDC access token you already hold | `todofs` |
| `oidc-device` | a token from the RFC 8628 device grant | `todofs`, interactively |
| `oidc-password` | a token from the resource-owner password grant | `todofs`, in development only |

The two `oidc-*` grants need `--oidc-issuer <url>` and `--oidc-client-id <id>`, and the identity
the token proves must match `--uname`; a mismatch is refused by the server.

- `oidc-device` prints `open <url> and enter the code <code>` on standard error, then polls the
  issuer until you have approved the login in a browser.
- `oidc-password` reads the password from `$NINEP_PASSWORD`, or from the terminal when that is
  unset; never from the command line. It prints a warning on every use: the grant is disabled by
  default in Keycloak and belongs to development and tests.
- `--auth-optional` falls back to an anonymous attach when the server answers that it requires no
  authentication; without it that answer ends the run with exit `2`. The fallback prints
  `ninep: server requires no authentication; attached anonymously` on **standard error**, because a
  credential that was presented and then dropped is exactly the case where a caller believes the
  session is authenticated and it is not. Standard output is byte-identical either way, which is
  what the conformance fixture freezes.

There is no password-file credential in the cli. A server started with
`jsonfs --auth password-file:<path>` is reached from library code with `PasswordCredential`; the
jsonfs README carries a ten-line client that does it.

### Getting a token

Without a Keycloak, the repository's own fake issuer serves the discovery document, the JWKS and
both grants on loopback, and prints four ready-made bearer tokens:

```text
dotnet run --project tests/NineP.Conformance -- fake-issuer
```

```text
NINEP_PASSWORD=hunter2 dotnet run --project examples/NineP.Cli -- --addr tcp://127.0.0.1:5641 \
  --uname glenda --auth oidc-password --oidc-issuer http://127.0.0.1:<port> --oidc-client-id todofs-cli ls /users

dotnet run --project examples/NineP.Cli -- --addr tcp://127.0.0.1:5641 --uname glenda \
  --auth bearer:<token> ls /users
```

Against a real Keycloak, point `--oidc-issuer` at the realm, for example
`http://localhost:8080/realms/ninep`, and use the client id you registered there. Setting the
realm up is described in [the todofs README](../NineP.TodoFs/README.md#keycloak).

Tokens are held in memory for the life of the process and are never written to disk or logged.
