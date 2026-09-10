using System.Buffers;
using System.Text;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using Xunit;

namespace NineP.Protocol.Tests.Conformance;

[Trait("Category", "Conformance")]
public sealed class EmptyFieldTests
{
    [Fact]
    public void AnEmptyEnameIsEio() => Assert.Equal(Errno.EIO, NinePError.FromEname("").Errno);

    [Fact]
    public void TversionWithAnEmptyVersionDecodes()
    {
        Tversion original = new(Constants.NOTAG, uint.MaxValue, "");
        ArrayBufferWriter<byte> bytes = new();
        MessageCodec.Encode(bytes, in original, Dialect.P9_2000);
        Assert.Equal(original, MessageCodec.Decode<Tversion>(bytes.WrittenMemory, Dialect.P9_2000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheStringLimitCountsUtf8Bytes(bool multibyte)
    {
        string text = multibyte ? new string('é', 32767) + "x" : new string('x', 65535);
        Assert.Equal(65535, Encoding.UTF8.GetByteCount(text));
        Tversion message = new(Constants.NOTAG, 100000, text);
        ArrayBufferWriter<byte> bytes = new();
        MessageCodec.Encode(bytes, in message, Dialect.P9_2000);
        Assert.Equal(text, MessageCodec.Decode<Tversion>(bytes.WrittenMemory, Dialect.P9_2000).Version);
        Tversion tooLong = message with { Version = text + "x" };
        Assert.Throws<ArgumentOutOfRangeException>(() => MessageCodec.Encode(new ArrayBufferWriter<byte>(), in tooLong, Dialect.P9_2000));
    }
}
