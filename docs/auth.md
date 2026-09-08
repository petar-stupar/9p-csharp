# Authentication

9P's own contribution to authentication is a shape, not a mechanism. `Tauth` opens an **afid**; the
client writes a credential to it and may read a response; the client then presents that afid in
`Tattach`. What flows over the afid is not 9P's business. This document is what this workspace put
there.

## The shape the core enforces

```csharp
public interface IAuthenticator
{
    bool IsRequired { get; }
    ValueTask<IAuthSession?> BeginAsync(
        AuthRequest request, PeerIdentity? peer, CancellationToken cancellationToken = default);
}

public interface IAuthSession : IAsyncDisposable
{
    Identity? Identity { get; }                    // null until the exchange has succeeded
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(int maxBytes, CancellationToken cancellationToken = default);
}
```

`BeginAsync` returning `null` refuses the `Tauth` outright. `AuthRequest` is the
`(Uname, NUname, Aname)` the client asked with, and `peer` is whatever the transport learned — the
client certificate for mutual TLS, the `Origin` and headers for a WebSocket.

Four rules the **core** enforces, so that no authenticator can get them wrong:

1. **An attach with an afid whose session has no identity is `EACCES` / `"authentication
   failed"`.** `IAuthSession.Identity` staying null is the only way to say "not yet".
2. **The session's identity is the one `Identity` returned** — never the `uname` or `n_uname` the
   client claimed. A client may claim anything; the authenticator decides.
3. **An afid is bound to the `(uname, n_uname, aname)` triple it was created with**, and an attach
   presenting it must match, with an empty `uname` and an `n_uname` of `NONUNAME` accepted as
   "unspecified". attach(5) binds `uname` and `aname` only, which in `.u` and `.L` would let an afid
   obtained for `n_uname = 1000` satisfy an attach claiming `n_uname = 1001`.
4. **The exchange is bounded**: `Limits.MaxAuthBytes` (64 KiB in each direction) and
   `Limits.AuthTimeout` (30 s). Exceeding either aborts the exchange, and the attach that follows is
   `EACCES`.

## What a server with no authenticator does

`ServerOptions.Authenticator` left null means **no authentication**:

- `Tauth` is refused: `Rerror "authentication not required"` in 9P2000 and 9P2000.u, and
  `Rlerror ECONNREFUSED (111)` in 9P2000.L;
- `Tattach` with `afid = NOFID` succeeds, and the session runs as the `uname` / `n_uname` **as
  claimed**.

That is correct behind an already-authenticated transport — a Unix socket, mutual TLS, a private
network — and wrong anywhere else. A server whose peers are not already authenticated must set an
authenticator.

An authenticator whose `IsRequired` is true also refuses a `NOFID` attach, so there is no path
around it. One that is installed but *not* required still judges every afid presented to it, and
additionally lets an unauthenticated attach through with nothing proved about it: the filesystem
then sees `Identity.Anonymous`, and `Identity.IsAuthenticated` is how it tells.

## The shipped authenticators

### `TokenAuthenticator`

The client writes an opaque token once; the server compares it with a configured secret and the
afid becomes readable as `ok\n`. The comparison is `ConstantTime.Equals`, which is
`CryptographicOperations.FixedTimeEquals`, so the length of the matching prefix is not observable.

```csharp
var server = new NinePServer(new ServerOptions
{
    Listen = [address],
    Authenticator = new TokenAuthenticator(secret),
});

var session = await NinePClient.ConnectAsync(address, new ClientOptions
{
    Credential = new TokenCredential(secret),
});
```

The second constructor takes `Func<AuthRequest, ReadOnlyMemory<byte>?>`, for a per-user or per-tree
secret; returning null refuses that request without revealing whether the user exists.

### `PasswordAuthenticator`

The client writes `user\npassword\n`; the server verifies it against an `IPasswordStore`.
`PasswordFileStore` is the shipped one: a text file of `user:hash` lines where the hash is
**PBKDF2-HMAC-SHA-256 with at least 600 000 iterations**, which is the floor
`PasswordFileStore.HashPassword` writes and the floor `Load` refuses to go below. Argon2id would be
preferable and there is no vetted implementation in the BCL, which is why this is PBKDF2 and why
the iteration count is checked rather than trusted.

`PasswordCredential` zeroes its payload after the exchange. A newline in either field is an
`ArgumentException` at construction: the wire form is line-delimited, and a newline in one field
would let a caller forge the other.

### `TlsClientCertAuthenticator`

For mutual TLS. There is no exchange at all — the identity comes from
`PeerIdentity.ClientCertificate`, which the TLS transport has already validated. The default mapper
takes the certificate's SAN DNS name, falling back to its CN, and refuses an attach whose `uname`
differs. `TlsClientCertAuthenticator(Func<X509Certificate2, Identity?>)` replaces the mapper when
your certificates say something else; returning null refuses the connection.

## Writing your own

Implement `IAuthenticator` and return an `IAuthSession` whose `Identity` becomes non-null when you
are satisfied. The core does the rest — the triple binding, the bounds, the timeout, the `EACCES`.

`examples/NineP.TodoFs/KeycloakAuthenticator.cs` is the worked example: the client writes an OIDC
access token to the afid; the server validates the signature against the realm's JWKS (RS256 or
ES256, cached with rotation, `alg=none` rejected outright), then `iss`, `aud`, `exp` and `nbf` with
60 s of leeway, and maps `preferred_username` — falling back to `sub` — to the user. The realm roles
in `realm_access.roles` become `Identity.Groups`, which is how `todofs` gates `users/ctl` on
`todofs-admin`. It is about 200 lines and none of them is protocol code.

## Getting a token in the first place

**No package here performs a login.** `BearerTokenCredential` takes a token the caller already
holds; the authenticators only ever validate one. Acquiring a token is the `ninep` cli's job, and it
offers three ways:

| Flag | Grant | When |
| --- | --- | --- |
| `--auth bearer:<token>` | none; you have one | `kcadm`, a browser session, a CI secret |
| `--auth oidc-device` | RFC 8628 device authorization | interactive login with no browser redirect back to a terminal |
| `--auth oidc-password` | resource-owner password | **development and test only**; Keycloak disables it by default and the cli says so |

The device grant prints the `verification_uri_complete` and the `user_code` to standard error, then
polls the token endpoint at the interval the server asked for, honouring `authorization_pending` and
`slow_down` until `expires_in`. The password grant reads the password from `$NINEP_PASSWORD` or the
terminal and **never** from the command line, which would put it in the process table and the
shell's history. Refresh tokens stay in memory for the life of the process and are never written to
disk.

Every grant is exercised against the in-repo fake issuer in `tests/NineP.TestSupport/FakeIssuer/`,
never against a live Keycloak. The fake issuer mints its own tokens by hand — including the
`alg=none` and corrupted-signature cases that a signing library would refuse to produce.

## What is out of scope

Plan 9's **`p9any` and `p9sk1`** are not implemented and will not be. `p9sk1` is DES-based; it is a
museum piece, not production security. A deployment that needs to interoperate with a Plan 9
auth server should terminate that in front of this one.

## Tokens and logs

A token is never logged, never stored, and never readable back. Untrusted strings that do reach a
log go through the packages' internal `UntrustedText.Sanitize`, which escapes control characters
and caps at 256 bytes. `ename` never carries the input verbatim.
