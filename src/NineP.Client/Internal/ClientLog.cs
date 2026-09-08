using Microsoft.Extensions.Logging;
using NineP.Protocol.Internal;

namespace NineP.Client.Internal;

/// <summary>
/// Every record <c>NineP.Client</c> emits, as source-generated <see cref="LoggerMessage"/> methods.
/// See <c>NineP.Protocol.Internal.ProtocolLog</c> for why the records are shaped this way; the
/// client's event ids are 2xxx.
/// </summary>
internal static partial class ClientLog
{
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "the 9P session terminated: {Reason}")]
    public static partial void SessionTerminated(this ILogger logger, string reason);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Debug,
        Message = "the fid of a failed walk could not be clunked: {Reason}")]
    public static partial void WalkFidNotClunked(this ILogger logger, string reason);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Warning,
        Message = "the server answered a Tflush with an error (errno {Errno}, \"{Ename}\") rather than Rflush; both tags are released as an Rflush would release them")]
    public static partial void FlushAnsweredWithError(this ILogger logger, int errno, string ename);
}
