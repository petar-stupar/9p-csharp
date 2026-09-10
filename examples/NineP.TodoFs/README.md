# todofs

`todofs` serves a SQLite database as a per-user to-do tree, authenticated with OIDC access tokens
from a Keycloak realm. Each user sees only their own lists and items; an administrator manages the
users through a control file. The tree, the isolation rules and the token checks are described in
[docs/examples.md](../../docs/examples.md#todofs); this file is about running it, with the
repository's fake issuer or with a real Keycloak.

Every command below is run from the repository root and assumes the .NET 10 SDK pinned by
[global.json](../../global.json).

## Run it

```text
usage: todofs --listen <url>... --db <path.sqlite> --oidc-issuer <url>
              --oidc-audience <aud> [--admin-role todofs-admin] [--jwks-cache <secs>]
              [--allow-insecure-issuer] [--max-lists 1000] [--max-items 10000]
              [--dialects 9P2000,9P2000.u,9P2000.L]
              [--tls-cert <pem> --tls-key <pem> --tls-client-ca <pem>] [--log <level>]
```

| Flag | Meaning |
| --- | --- |
| `--listen <url>` | `tcp://`, `tls://`, `ws://` or `wss://` address; repeatable |
| `--db <path.sqlite>` | the database; created with the schema when the file is empty or absent |
| `--oidc-issuer <url>` | the realm; its discovery document is at `<url>/.well-known/openid-configuration` |
| `--oidc-audience <aud>` | the `aud` claim every token must carry |
| `--admin-role` | the realm role that may read and write `/users/ctl`; `todofs-admin` by default |
| `--jwks-cache <secs>` | how long the realm's signing keys are cached; five minutes by default, and **raised to five minutes** when a shorter one is asked for |
| `--allow-insecure-issuer` | fetch discovery and keys over plain HTTP; for a loopback development issuer only |
| `--max-lists <n>` | the most lists one user may hold, `1000` by default; the `mkdir` that would exceed it is `ENOSPC` and nothing is created |
| `--max-items <n>` | the most items one list may hold, `10000` by default; the `mkdir` that would exceed it is `ENOSPC` and nothing is created |
| `--tls-cert`, `--tls-key`, `--tls-client-ca` | as for jsonfs, see [its README](../NineP.JsonFs/README.md#tls) |
| `--log` | `trace`, `debug`, `info`, `warn` (default), `error` or `none`, to standard error |

The server prints `listening <address> db=<path>` per address once bound. Exit codes: `0` stopped
on request, `1` a runtime failure, `3` a bad command line or a database at a newer schema version
than this build.

A token is the only credential: an attach without one is `permission denied`. Every token is
checked for signature (RS256 or ES256, against the realm's published keys), issuer, audience,
expiry and not-before; the user is its `preferred_username`, and the realm roles in
`realm_access.roles` decide administration.

## Quick start with the fake issuer

The conformance project carries the OIDC issuer the test suite uses, runnable as a program. It
speaks the discovery document, the JWKS, the password grant and the device grant on a loopback
port, and prints four pre-minted tokens: `glenda`, `glenda` with the admin role, `bob`, and an
expired one.

```text
dotnet build
dotnet run --project tests/NineP.Conformance -- fake-issuer
```

```text
issuer             http://127.0.0.1:52781
audience           todofs
client-id          todofs-cli
password grant     glenda / hunter2

token glenda       eyJhbGciOiJSUzI1NiIs…
token glenda+admin eyJhbGciOiJSUzI1NiIs…
token bob          eyJhbGciOiJSUzI1NiIs…
token expired      eyJhbGciOiJSUzI1NiIs…
```

The port changes on every start; copy the printed issuer. In a second terminal:

```text
dotnet run --project examples/NineP.TodoFs -- --listen tcp://127.0.0.1:5641 --db /tmp/todo.sqlite \
  --oidc-issuer http://127.0.0.1:52781 --oidc-audience todofs --allow-insecure-issuer
```

`--allow-insecure-issuer` is needed because the fake issuer is plain HTTP; without it todofs
refuses to fetch the realm's keys. In a third terminal, log in with the password grant and look
around; `--uname` must be the user the token names:

```text
NINEP_PASSWORD=hunter2 dotnet run --project examples/NineP.Cli -- --addr tcp://127.0.0.1:5641 \
  --uname glenda --auth oidc-password --oidc-issuer http://127.0.0.1:52781 --oidc-client-id todofs-cli ls /users
```

```text
warning: the password grant is for development and test only
ctl
glenda/
```

A user who authenticates but has no row is created on attach, which is why `glenda/` is already
there. The pre-minted tokens are bearer credentials; the admin one is what `/users/ctl` needs:

```text
ADMIN=<token glenda+admin>
printf 'add bob\n' | dotnet run --project examples/NineP.Cli -- --addr tcp://127.0.0.1:5641 --uname glenda --auth bearer:$ADMIN write /users/ctl
dotnet run --project examples/NineP.Cli -- --addr tcp://127.0.0.1:5641 --uname glenda --auth bearer:$ADMIN cat /users/ctl
```

```text
bob
glenda
```

Lists and items are directories named by number, and the number must be the next free one,
starting at `0`; anything else is `bad argument`. Fields are files:

```text
T="--addr tcp://127.0.0.1:5641 --uname glenda --auth bearer:<token glenda>"
dotnet run --project examples/NineP.Cli -- $T mkdir /users/glenda/0
printf 'groceries' | dotnet run --project examples/NineP.Cli -- $T write /users/glenda/0/name
dotnet run --project examples/NineP.Cli -- $T mkdir /users/glenda/0/0
printf 'milk' | dotnet run --project examples/NineP.Cli -- $T write /users/glenda/0/0/label
printf 'done\n' | dotnet run --project examples/NineP.Cli -- $T write /users/glenda/0/0/status
dotnet run --project examples/NineP.Cli -- $T ls -l /users/glenda/0/0
```

```text
file 0 description
file 4 label
file 4 status
```

`status` accepts `open` or `done`; every field is capped at 64 KiB; `rm` removes an item, or a
list that has no items. A `bob` token sees only `/users/bob`, and nothing tells it that `glenda`
exists.

A few things worth knowing before you script against it:

- **Truncation happens at the open.** `ninep write` and `echo … > file` both open with `O_TRUNC`,
  and todofs performs the truncation there rather than waiting for a write that may never arrive.
  `label`, `description` and a list's `name` become empty; `status` becomes `open`, the value a new
  item carries, because the schema allows only `open` or `done` and so it has no zero-length value.
  So a truncating open of `status` that is then clunked without a write leaves `open` behind, the
  way `echo > file` on a real file leaves an empty one behind when the write fails.
- **A `wstat` / `Tsetattr` length of zero is a different request**, and it is refused where the file
  has no zero-length value: `EINVAL` for `status` and for `/users/ctl`. Any other field in the same
  update — a mode, a group, a time, a name — refuses the whole update with `EOPNOTSUPP` and changes
  nothing; todofs derives every attribute but the length. `/users/ctl` is the one file whose
  truncating open changes nothing at all: it keeps no bytes of its own, its read side renders the
  user table and its write side takes one command.
- **A read always answers from the database**, including on the fid that just wrote. Writing
  `done\n` to `status` and reading it back gives `done`, and writing `add bob` to `/users/ctl` and
  reading it back gives the user list.
- **`mkdir` ignores the mode**: a todofs file is always `0644` and a directory always `0755`. There
  is no column for a mode, a group or a create flag, and refusing a mode would make every ordinary
  `mkdir` from Linux fail, since v9fs always sends one.
- **`--jwks-cache` below five minutes is raised to five minutes.** That is
  `ConfigurationManager<T>.MinimumAutomaticRefreshInterval` in the identity library todofs uses,
  and it is a floor the library will not go below; a shorter value is clamped rather than honoured.
- **A `--tls-*` flag with no `tls://` or `wss://` `--listen` is unused**, and todofs logs a warning
  at startup naming it: a certificate that is loaded and never presented reads as a server that is
  protected when it is not.

## Keycloak

[docker-compose.yml](docker-compose.yml) starts a Keycloak 26 in development mode on
`http://localhost:8080`, with the bootstrap administrator `admin` / `admin`. No test touches it.

```text
cd examples/NineP.TodoFs
docker compose up -d
docker compose logs -f keycloak      # until "Listening on: http://0.0.0.0:8080"
```

Open `http://localhost:8080` and sign in to the administration console. todofs needs a realm, a
public client the cli can use, an audience in the access token, an administrator role and some
users. The steps below were run against Keycloak 26.0 through the administration API, which
creates the same objects the console does. In the console:

1. **Realm.** In the realm drop-down at the top left, *Create realm*, name `ninep`, *Create*.
   Everything below happens inside that realm.
2. **Client.** *Clients* → *Create client*. Client type *OpenID Connect*, client ID `todofs-cli`,
   *Next*. Under *Capability config* leave *Client authentication* **off** (a public client, which
   is what a command-line tool is), and under *Authentication flow* tick **Direct access grants**
   for `--auth oidc-password` and **OAuth 2.0 Device Authorization Grant** for
   `--auth oidc-device`. *Next*, *Save*.
3. **Audience.** Keycloak does not put a client's own id in `aud`, and todofs checks that claim
   against `--oidc-audience`. *Client scopes* → *Create client scope*, name `todofs-audience`,
   type *Default*, *Save*. On its *Mappers* tab, *Configure a new mapper* → *Audience*: name
   `todofs`, *Included Custom Audience* `todofs`, *Add to access token* on, *Save*. Then
   *Clients* → `todofs-cli` → *Client scopes* → *Add client scope* → `todofs-audience` → *Add* as
   *Default*.
4. **Role.** *Realm roles* → *Create role*, name `todofs-admin`, *Save*.
5. **Users.** *Users* → *Create user*, username `glenda`, and fill in **email, first name and
   last name** too: Keycloak 26's default user profile requires them, and a user without them is
   refused every login with `Account is not fully set up`. *Create*. On the *Credentials* tab,
   *Set password*, with *Temporary* off. On the *Role mapping* tab, *Assign role*, filter by realm
   roles, tick `todofs-admin`, *Assign*. Create `bob` the same way without the role.

The issuer is `http://localhost:8080/realms/ninep`. It is plain HTTP, so todofs needs
`--allow-insecure-issuer` here just as with the fake issuer:

```text
dotnet run --project examples/NineP.TodoFs -- --listen tcp://127.0.0.1:5641 --db /tmp/todo.sqlite \
  --oidc-issuer http://localhost:8080/realms/ninep --oidc-audience todofs --allow-insecure-issuer
```

Log in with the device grant, which opens nothing itself but prints the URL and code to visit:

```text
dotnet run --project examples/NineP.Cli -- --addr tcp://127.0.0.1:5641 --uname glenda \
  --auth oidc-device --oidc-issuer http://localhost:8080/realms/ninep --oidc-client-id todofs-cli ls /users
```

```text
open http://localhost:8080/realms/ninep/device?user_code=XXXX-XXXX and enter the code XXXX-XXXX
```

Approve it in the browser as `glenda`, and the listing appears. The password grant works the same
way as against the fake issuer, with the password you set:

```text
NINEP_PASSWORD=<password> dotnet run --project examples/NineP.Cli -- --addr tcp://127.0.0.1:5641 \
  --uname glenda --auth oidc-password --oidc-issuer http://localhost:8080/realms/ninep --oidc-client-id todofs-cli ls /users
```

`--uname` has to be the Keycloak username, since that is what `preferred_username` carries; a
token for `glenda` presented with `--uname bob` is refused.

Checks worth knowing when it does not work:

- `permission denied` on attach with a fresh token usually means the audience: decode the token at
  the `.` boundaries and look for `"aud"` containing `todofs`. If it is missing, the client scope
  of step 3 is not attached to `todofs-cli` as a default scope.
- The cli's `oidc-password` failing with `invalid_grant` or `unauthorized_client` means *Direct
  access grants* is off on the client, or the password is still marked temporary.
  `Account is not fully set up` is the user profile: the user has no email, first name or last
  name, or a required action such as *Update profile* is pending on the *Details* tab.
- The device grant failing with `advertises no device_authorization_endpoint` means the realm has
  the grant off; it is per client in step 2, but the realm must advertise the endpoint, which
  Keycloak 26 does by default.
- `/users/ctl` answering `permission denied` means the token has no `todofs-admin` in
  `realm_access.roles`; role mappings take effect on the next token, not on tokens already issued.

Outside development, put Keycloak behind HTTPS and drop `--allow-insecure-issuer`: with it, anyone
between todofs and the realm can substitute the signing keys, and every token todofs then accepts
is forgeable.

`docker compose down -v` removes the container and its data, realm included; Keycloak in
development mode keeps its state in the container's own volume only.

## TLS

The `--tls-*` flags are the same as jsonfs's, and the certificates made by the recipe in
[its README](../NineP.JsonFs/README.md#tls) work unchanged:

```text
dotnet run --project examples/NineP.TodoFs -- --listen tls://127.0.0.1:5641 --db /tmp/todo.sqlite \
  --oidc-issuer http://127.0.0.1:52781 --oidc-audience todofs --allow-insecure-issuer \
  --tls-cert server.pem --tls-key server-key.pem

dotnet run --project examples/NineP.Cli -- --addr tls://127.0.0.1:5641 --tls-ca ca.pem \
  --uname glenda --auth bearer:<token> ls /users
```

The transport and the token are independent: TLS protects the bearer token in flight, which is
the reason to use it anywhere but loopback, and the token still decides who the user is.
