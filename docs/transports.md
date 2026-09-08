# Transports

A transport is a dialer and a listener over a bidirectional byte stream. Both the client and the
server need one, which is why they live in `NineP.Protocol` and not in either of the packages
above it.

```csharp
public interface ITransport
{
    IReadOnlyCollection<NinePScheme> Schemes { get; }
    ValueTask<INinePConnection> ConnectAsync(NinePAddress address, CancellationToken cancellationToken = default);
    ValueTask<INinePListener> ListenAsync(NinePAddress address, CancellationToken cancellationToken = default);
}
```

`INinePConnection` is `ReadAsync` / `WriteAsync` / `CloseAsync(CloseReason)` / `DisposeAsync`, plus
`RemoteAddress` and one thing a 9P peer wants that a raw socket does not carry: `PeerIdentity`,
which is whatever the transport learned about the far end. A connection is a byte stream and
nothing more; there is no flag saying whether the transport preserves message boundaries, because
nothing reads one — the frame reader re-frames every transport alike from the size field
(workspace architecture §12 rule 6).

`WriteAsync` is always handed **one complete 9P frame**, `size[4]` included. A transport that has
message boundaries (WebSocket) may put it in one message; a transport that does not may split it
however it likes, because the frame reader on the other side reassembles from the size field.

## Addresses

```text
tcp://host:port
tls://host:port
ws://host:port/path
wss://host:port/path
memory://name
unix:///absolute/path
```

`NinePAddress` is a `readonly record struct` of `(Scheme, Host, Port, Path)` with `Parse`,
`TryParse` and a `ToString` that renders exactly what was parsed — an address it cannot render back
it does not accept. Port **0** is legal: it is how a listener asks the kernel for a free port, and
`INinePListener.LocalAddress` then carries the real one.

`unix://` is part of the grammar but no transport in this repository binds it; an address no
configured transport owns is an `ArgumentException` when serving starts rather than a silent no-op.

## TCP

```csharp
new TcpTransport(new TcpTransportOptions
{
    NoDelay = true,          // default; 9P is a request/reply protocol and Nagle costs a round trip
    KeepAlive = true,        // default
    Backlog = 128,           // default
    MaxConnections = 1024,   // default; beyond it the accept loop stops accepting, it does not reset
    ConnectTimeout = TimeSpan.FromSeconds(30),
});
```

The first read of an accepted connection is bounded by `Limits.Default.ReadHeaderTimeout` (30 s), so
a peer that connects and says nothing is dropped rather than left holding one of those 1024 slots.
A peer that says half a frame and then stops is closed by the frame reader on the same budget.

`PeerIdentity.RemoteAddress` is what TCP alone knows. Anything more needs TLS.

## TLS

```csharp
new TlsTransport(new TlsTransportOptions
{
    ServerCertificate = certificate,           // server side
    TrustedRoots = roots,                      // client side, when not the platform store
    RequireClientCertificate = true,           // mutual TLS
    TargetHost = "fileserver.example",         // when it differs from the address's host
    AdditionalPeerCheck = cert => cert.Thumbprint == pinned,
});
```

TLS **1.2 is the floor and 1.3 is preferred**, fixed in the library rather than left to the machine:
`TlsTransportTests.Tls11Refused` asserts it, and letting the operating system decide would make that
assertion depend on the host's configuration instead of on this code. The chain and the host name
are verified by default.

`AdditionalPeerCheck` may only **add** a check. It is consulted after the platform's validation has
already passed, so it can pin a thumbprint but it cannot rescue a certificate the platform rejected.
The one way to accept an untrusted certificate is `AllowInsecureCertificates`, which is explicit,
which is logged at `Warning` every time it is used, and which no deployment should set. It applies
to dialling only: a listener still verifies whatever a client presents, and one configured with the
flag logs that it is being ignored.

With `RequireClientCertificate`, the client certificate arrives as
`PeerIdentity.ClientCertificate`, which is what `TlsClientCertAuthenticator` turns into an
`Identity`.

## WebSocket

```csharp
new WebSocketTransport(new WebSocketTransportOptions
{
    Subprotocol = "9p",                        // default; null to omit the header
    AllowedOrigins = ["https://console.example"],
    MaxMessageSize = 1024 * 1024,              // default
    KeepAliveInterval = TimeSpan.FromSeconds(30),
    Tls = tlsOptions,                          // for wss://
});
```

**One 9P message per binary WebSocket message.** Fragmentation stays inside the WebSocket layer, so
a 9P frame never spans two WebSocket messages. A text message closes the socket 1002
(`ProtocolError`), and a message that grows past `MaxMessageSize` closes it 1009
(`MessageTooBig`) — checked **during** accumulation, not after the last fragment, which is the
difference between refusing a large message and buffering it first.

The server side is a raw `Socket` listener with a hand-written RFC 6455 handshake, not
`HttpListener`: an `https://` prefix starts on macOS but its handshake is reset by the peer, and
only the raw path exposes the request headers that the origin allow-list and `PeerIdentity` need.
The client side is `ClientWebSocket`.

`AllowedOrigins`, once configured, refuses a request that carries **no** `Origin` header. RFC 6455
leaves that open; an allow-list is a statement of who may connect, and a non-browser client that
sends no Origin is not exempt from it. `PeerIdentity` carries the `Origin` and the request headers.

## In memory

`MemoryTransport` is a pair of pipes. It is not a toy: every test in this repository that can run
over it does, which is what proves the seam — a bug in the session layer cannot hide behind a
socket. Each instance is isolated, two instances never share an endpoint name, and a close reason
given at one end is visible at the other, which no real transport but a WebSocket can do.

A `memory://` endpoint belongs to **one** `MemoryTransport` instance and is not routable by name, so
both `NinePClient.ConnectAsync(NinePAddress, …)` and `ServerOptions.Transports`' defaults refuse it:
hand over the instance that bound it.

```csharp
var transport = new MemoryTransport();
var address = NinePAddress.Parse("memory://test");

var server = new NinePServer(new ServerOptions { Listen = [address], Transports = [transport] });
_ = server.ServeAsync(tree);
await server.ListeningAsync();

await using var session = await NinePClient.ConnectAsync(transport, address, new ClientOptions());
```

`MemoryTransport.CreatePair()` gives two connected ends with no listener at all, for a test that
wants a wire and nothing else.

## Writing your own

Implement `ITransport`, `INinePListener` and `INinePConnection`. Three things are worth getting
right, and the in-memory transport is 200 lines that show all three:

1. **`ReadAsync` returns 0 only at a real end of stream.** A read that fails because *this* side
   closed is an orderly end of stream, not an exception: the TCP transport learned that the hard way
   on macOS, where closing a socket with a read in flight raises `SocketException`
   (`OperationAborted`) that then escapes six catch filters and comes out of `DisposeAsync`.
2. **`CloseAsync` records the reason even when the wire cannot carry it.** TCP cannot; the reason is
   still recorded for this side's log, and the socket is shut down in both directions.
3. **`Schemes` is what the server and client match an address against.** A transport that claims a
   scheme must bind and dial every address of it.

The server takes yours through `ServerOptions.Transports`; the client through the
`ConnectAsync(ITransport, NinePAddress, …)` overload. Nothing else changes.

## Defaults

`ServerOptions.Transports` defaults to `TcpTransport` plus `WebSocketTransport` (which owns both
`ws` and `wss`). `TlsTransport` is not in the default set because it cannot be default-constructed —
a TLS listener needs a certificate — and `MemoryTransport` is not, for the reason above. Configure
either by handing over the instance.

## Bounded handshake concurrency

TLS/WS/WSS handshakes are independent: a silent earlier peer does not delay a healthy connection
when a connection slot is available. `TcpTransportOptions.MaxConnections` bounds the total of
pending handshakes, ready connections awaiting `AcceptAsync`, and live delivered connections.
At capacity, raw accepts wait in the TCP backlog. Failed/timed-out handshakes release slots; listener
disposal cancels pending work and disposes ready connections. Cancelling one `AcceptAsync` cancels
that wait, not other clients' handshakes. The existing handshake timeout applies to each peer.

Custom TLS roots add trust anchors while retaining the peer's required application purpose: server
authentication when dialing, client authentication for a presented client certificate. Leaf and
intermediate EKU restrictions and hostname validation remain effective with custom roots.
