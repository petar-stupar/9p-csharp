namespace NineP.Protocol.Messages;

/// <summary>
/// Implemented by every wire-legal message record, tying a record to its type number. The type is
/// a static abstract member so that the codec can be generic over the record without boxing it.
/// </summary>
public interface IMessage
{
    /// <summary>The type number this record encodes to.</summary>
    static abstract MessageType Type { get; }

    /// <summary>The message tag; <c>NOTAG</c> for <c>Tversion</c> and <c>Rversion</c>.</summary>
    ushort Tag { get; }
}
