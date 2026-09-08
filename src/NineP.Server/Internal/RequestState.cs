using System.Collections.Concurrent;
using NineP.Protocol;
using NineP.Protocol.Auth;

namespace NineP.Server.Internal;

/// <summary>Where one in-flight request is in its life (§6.5).</summary>
internal enum RequestState
{
    /// <summary>The handler is running and its reply has not been chosen.</summary>
    Running = 0,

    /// <summary>A <c>Tflush</c> claimed it; its reply must never be sent.</summary>
    Flushed = 1,

    /// <summary>Its reply has been handed to the writer.</summary>
    Completed = 2,
}
