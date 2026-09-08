using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace NineP.Protocol.Transports;

/// <summary>TCP transport tuning (workspace architecture §3).</summary>
public sealed record TcpTransportOptions
{
    /// <summary>
    /// Disable Nagle. Default true: 9P is a request/response protocol whose messages are small and
    /// whose latency is the thing being measured, and the ticket's pitfall list requires it.
    /// </summary>
    public bool NoDelay { get; init; } = true;

    /// <summary>Enable TCP keep-alive. Default true.</summary>
    public bool KeepAlive { get; init; } = true;

    /// <summary>The listen backlog. Default 128.</summary>
    public int Backlog { get; init; } = 128;

    /// <summary>
    /// Accepted connections held at once; further accepts wait. Beyond the cap the listener stops
    /// accepting — pending connections stay in the kernel's backlog rather than being accepted and
    /// reset. Default 1024.
    /// </summary>
    public int MaxConnections { get; init; } = 1024;

    /// <summary>The dial timeout. Default 30 s.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Where a transient accept failure is reported; <see cref="NullLogger.Instance"/> when null. An
    /// accept that fails and is retried is the one thing this transport has to say, and a
    /// listener that says nothing about it hides a machine running out of descriptors.
    /// </summary>
    public ILogger? Logger { get; init; }
}
