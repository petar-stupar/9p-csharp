using NineP.Protocol;
using NineP.Protocol.Auth;

namespace NineP.Server;

/// <summary>
/// Receives one entry per completed request. An implementation must not throw and must not block:
/// it is called from the connection's own loop, and a slow sink is backpressure on the session.
/// </summary>
public interface IRequestLogSink
{
    /// <summary>Records one completed request.</summary>
    /// <param name="entry">What happened.</param>
    void Record(in RequestLogEntry entry);
}
