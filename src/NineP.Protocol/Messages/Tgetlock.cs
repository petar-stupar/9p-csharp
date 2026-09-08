namespace NineP.Protocol.Messages;

/// <summary>
/// Asks which lock, if any, conflicts with a range (reference §4.8).
/// </summary>
/// <param name="Tag">The message tag, which pairs this message with its partner.</param>
/// <param name="Fid">The open fid to query.</param>
/// <param name="Type">The lock type the client would like to take.</param>
/// <param name="Start">The first byte of the range.</param>
/// <param name="Length">The length of the range, or 0 for "to the end of the file".</param>
/// <param name="ProcId">The client's process identifier.</param>
/// <param name="ClientId">The client's identifier.</param>
public readonly record struct Tgetlock(
    ushort Tag, uint Fid, LockType Type, ulong Start, ulong Length, uint ProcId, string ClientId)
    : IMessage
{
    static MessageType IMessage.Type => MessageType.Tgetlock;
}
