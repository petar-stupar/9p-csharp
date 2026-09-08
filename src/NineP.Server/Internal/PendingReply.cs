using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Channels;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Negotiation;
using NineP.Protocol.Transports;

namespace NineP.Server.Internal;

/// <summary>
/// One reply, encoded and waiting for the connection's single writer. The buffer is rented, so the
/// writer returns it once the bytes are on the wire and a session's reply memory stays bounded.
/// </summary>
/// <param name="Buffer">The rented buffer holding the frame.</param>
/// <param name="Length">How many of its bytes are the frame.</param>
internal readonly record struct PendingReply(byte[] Buffer, int Length);
