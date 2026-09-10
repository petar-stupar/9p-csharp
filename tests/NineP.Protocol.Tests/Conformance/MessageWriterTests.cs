using System.Buffers;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

/// <summary>The encoder's own guards, which are about the caller rather than about the wire.</summary>
[Trait("Category", "Conformance")]
public sealed class MessageWriterTests
{
    /// <summary>The predicted size is the size that is written, for every shape of message.</summary>
    [Fact]
    public void PredictedSizeIsTheWrittenSize()
    {
        AssertSizeMatches(new Rflush(1));
        AssertSizeMatches(new Tversion(Constants.NOTAG, 8192, Constants.Version9P2000));
        AssertSizeMatches(new Rread(2, new byte[] { 1, 2, 3 }));
        AssertSizeMatches(new Rread(2, ReadOnlyMemory<byte>.Empty));
        AssertSizeMatches(new Twalk(3, 0, 1, ["users", "alice"]));
        AssertSizeMatches(new Twalk(3, 0, 1, []));
        AssertSizeMatches(new Rstat(4, new StatRecord { Name = "notes", Uid = "alice" }));
    }

    /// <summary>
    /// A message that carries more than MAXWELEM elements is refused by the encoder, so this side
    /// cannot put on the wire what the other side is required to reject.
    /// </summary>
    [Fact]
    public void WalkBeyondMaxWelemIsRefused()
    {
        string[] tooMany = [.. Enumerable.Range(0, Constants.MAXWELEM + 1).Select(i => "d")];
        Qid[] tooManyQids = new Qid[Constants.MAXWELEM + 1];

        Assert.Throws<ArgumentException>(() => Encode(new Twalk(1, 0, 1, tooMany)));
        Assert.Throws<ArgumentException>(() => Encode(new Rwalk(1, tooManyQids)));
    }

    /// <summary>A walk whose element list was never set is refused rather than sent as empty.</summary>
    [Fact]
    public void WalkWithoutItsElementListIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Encode(default(Twalk)));
        Assert.Throws<ArgumentException>(() => Encode(default(Rwalk)));
    }

    /// <summary>
    /// errno is signed in the API and unsigned on the wire (workspace architecture §12 rule 2). A
    /// negative value is not an errno, and the encoder refuses it wherever it would go on the
    /// wire: an <c>Rlerror</c>, and an <c>Rerror</c> in .u. A 9P2000 <c>Rerror</c> has no errno
    /// field, so there is nothing to refuse.
    /// </summary>
    [Fact]
    public void NegativeErrnoIsRefused()
    {
        Rlerror linux = new(1, -1);
        Rerror unix = new(1, "i/o error", -1);

        Assert.Throws<ArgumentException>(() => MessageCodec.Encode(new ArrayBufferWriter<byte>(), in linux, Dialect.P9_2000_L));
        Assert.Throws<ArgumentException>(() => MessageCodec.Encode(new ArrayBufferWriter<byte>(), in unix, Dialect.P9_2000_u));
        Assert.Equal(MessageType.Rerror, MessageCodec.PeekType(Encode(unix)));
    }

    /// <summary>Encoding needs somewhere to write.</summary>
    [Fact]
    public void EncodeRejectsANullWriter()
    {
        Rflush message = new(1);
        Assert.Throws<ArgumentNullException>(() => MessageCodec.Encode(null!, in message, Dialect.P9_2000));
    }

    /// <summary>
    /// The size field holds what was actually written, because it is reserved first and patched
    /// after the body rather than predicted and trusted.
    /// </summary>
    [Fact]
    public void SizeFieldHoldsTheWrittenLength()
    {
        byte[] frame = Encode(new Tcreate(7, 1, "label", 420, 1, null));

        Assert.Equal((uint)frame.Length, MessageCodec.PeekSize(frame));
        Assert.Equal(MessageType.Tcreate, MessageCodec.PeekType(frame));
        Assert.Equal(7, MessageCodec.PeekTag(frame));
    }

    /// <summary>Anything the encoder writes, the decoder reads back to the same value.</summary>
    [Fact]
    public void EncodedMessagesDecodeBackToThemselves()
    {
        Ropen open = new(9, new Qid(QidType.QTFILE, 3, 42), 8168);
        byte[] frame = Encode(open);

        Assert.Equal(open, MessageCodec.Decode<Ropen>(frame, Dialect.P9_2000));
    }

    private static void AssertSizeMatches<TMessage>(TMessage message)
        where TMessage : struct, IMessage
    {
        ArrayBufferWriter<byte> buffer = new();
        int written = MessageCodec.Encode(buffer, in message, Dialect.P9_2000);

        Assert.Equal(MessageCodec.GetEncodedSize(in message, Dialect.P9_2000), written);
        Assert.Equal(written, buffer.WrittenCount);
        Assert.Equal((uint)written, MessageCodec.PeekSize(buffer.WrittenSpan));
    }

    private static byte[] Encode<TMessage>(TMessage message)
        where TMessage : struct, IMessage
    {
        ArrayBufferWriter<byte> buffer = new();
        MessageCodec.Encode(buffer, in message, Dialect.P9_2000);
        return buffer.WrittenSpan.ToArray();
    }
}
