using System.Buffers;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>
/// <c>Rgetattr</c> has one length and only one: reference §3.4 states it as an exact figure, and a
/// server sends every field whether or not it marked it valid.
/// </summary>
public sealed class RgetattrTests
{
    private const int RgetattrFrameSize = 160;

    /// <summary>The golden frame is 160 bytes, and so is anything the encoder produces.</summary>
    [Fact]
    public void Is160Bytes()
    {
        WireVector vector = WireVectors.All.First(v => v.Type == MessageType.Rgetattr);
        Assert.Equal(RgetattrFrameSize, vector.Size);

        Rgetattr message = MessageCodec.Decode<Rgetattr>(vector.Frame, Dialect.P9_2000_L);
        Assert.Equal(RgetattrFrameSize, MessageCodec.GetEncodedSize(in message, Dialect.P9_2000_L));

        // Nothing about the frame's length depends on what the server marked valid.
        Rgetattr empty = message with { Valid = GetAttrMask.None };
        ArrayBufferWriter<byte> buffer = new();
        Assert.Equal(RgetattrFrameSize, MessageCodec.Encode(buffer, in empty, Dialect.P9_2000_L));
    }

    /// <summary>A frame one byte short of 160 is malformed, not a shorter reply.</summary>
    [Fact]
    public void ShortFrameIsRejected()
    {
        WireVector vector = WireVectors.All.First(v => v.Type == MessageType.Rgetattr);
        byte[] truncated = vector.ToBytes()[..(RgetattrFrameSize - 1)];
        truncated[0] = (byte)(RgetattrFrameSize - 1);

        Assert.False(MessageCodec.TryDecode(
            truncated, Dialect.P9_2000_L, out Rgetattr _, out ProtocolErrorKind failure));
        Assert.Equal(ProtocolErrorKind.Bounds, failure);
    }

    /// <summary>The mask bits are the reference's, so BASIC stops at BLOCKS and ALL covers them all.</summary>
    [Fact]
    public void ValidMaskIsTheReferenceBasicSet()
    {
        WireVector vector = WireVectors.All.First(v => v.Type == MessageType.Rgetattr);
        Rgetattr message = MessageCodec.Decode<Rgetattr>(vector.Frame, Dialect.P9_2000_L);

        Assert.Equal(GetAttrMask.Basic, message.Valid);
        Assert.True(message.Valid.HasFlag(GetAttrMask.Blocks));
        Assert.False(message.Valid.HasFlag(GetAttrMask.BTime));
    }
}
