namespace NineP.Protocol.Messages;

/// <summary>
/// Commits a file to stable storage (reference §3.4). Encoders always emit datasync[4]; decoders
/// also accept the 11-byte form hugelgupf/p9 sends.
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The open fid to commit.</param>
/// <param name="Datasync">Non-zero to commit data only; 0 in a frame that omitted the
/// field.</param>
public readonly record struct Tfsync(ushort Tag, uint Fid, uint Datasync)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tfsync;
}
