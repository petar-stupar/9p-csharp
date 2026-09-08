using System.Security.Cryptography.X509Certificates;

namespace NineP.Protocol.Transports;

/// <summary>
/// What the transport learned about the peer during its handshake: a TLS client certificate, the
/// headers of a WebSocket upgrade. It is evidence the transport gathered, never a claim the peer
/// made in a 9P message — the session's identity still comes from the authenticator.
/// </summary>
public sealed record PeerIdentity
{
    /// <summary>The peer's TLS client certificate when mutual TLS was used; null otherwise.</summary>
    public X509Certificate2? ClientCertificate { get; init; }

    /// <summary>The <c>Origin</c> header of a WebSocket upgrade; null for other transports.</summary>
    public string? Origin { get; init; }

    /// <summary>Selected headers of a WebSocket upgrade, with lower-cased keys; empty otherwise.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The peer's network address as text, for logging.</summary>
    public string? RemoteAddress { get; init; }
}
