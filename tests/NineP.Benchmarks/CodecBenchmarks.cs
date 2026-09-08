using System.Buffers;
using BenchmarkDotNet.Attributes;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;

namespace NineP.Benchmarks;

/// <summary>
/// Benchmark (d) of architecture §9: decode and encode ns/op for <c>Twalk</c> and <c>Rgetattr</c>.
/// They are the two the workspace picked because they bracket the codec — <c>Twalk</c> is a
/// counted list of strings and allocates, <c>Rgetattr</c> is 160 fixed bytes and does not.
/// </summary>
[MemoryDiagnoser]
public class CodecBenchmarks
{
    private static readonly Twalk Walk = new(1, 2, 3, ["usr", "glenda", "lib", "profile"]);

    private static readonly Rgetattr GetAttr = new(
        1,
        GetAttrMask.All,
        new Qid(QidType.QTFILE, 7, 0x1122334455667788),
        0x81A4,
        1000,
        1000,
        1,
        0,
        4096,
        4096,
        8,
        new TimeSpec(1_700_000_000, 123),
        new TimeSpec(1_700_000_001, 456),
        new TimeSpec(1_700_000_002, 789),
        new TimeSpec(1_700_000_003, 0),
        0,
        0);

    private readonly byte[] _walkFrame = Encoded(Walk, Dialect.P9_2000);
    private readonly byte[] _getAttrFrame = Encoded(GetAttr, Dialect.P9_2000_L);
    private readonly FixedWriter _sink = new(new byte[512]);

    /// <summary>Decodes a four-element <c>Twalk</c>.</summary>
    /// <returns>The decoded message, so nothing is optimised away.</returns>
    [Benchmark(Description = "Twalk decode")]
    public Twalk DecodeTwalk() => MessageCodec.Decode<Twalk>(_walkFrame, Dialect.P9_2000);

    /// <summary>Encodes a four-element <c>Twalk</c> into a reused buffer.</summary>
    /// <returns>The number of bytes written.</returns>
    [Benchmark(Description = "Twalk encode")]
    public int EncodeTwalk()
    {
        _sink.Reset();
        return MessageCodec.Encode(_sink, in Walk, Dialect.P9_2000);
    }

    /// <summary>Decodes a full <c>Rgetattr</c>.</summary>
    /// <returns>The decoded message, so nothing is optimised away.</returns>
    [Benchmark(Description = "Rgetattr decode")]
    public Rgetattr DecodeRgetattr() => MessageCodec.Decode<Rgetattr>(_getAttrFrame, Dialect.P9_2000_L);

    /// <summary>Encodes a full <c>Rgetattr</c> into a reused buffer.</summary>
    /// <returns>The number of bytes written.</returns>
    [Benchmark(Description = "Rgetattr encode")]
    public int EncodeRgetattr()
    {
        _sink.Reset();
        return MessageCodec.Encode(_sink, in GetAttr, Dialect.P9_2000_L);
    }

    private static byte[] Encoded<TMessage>(TMessage message, Dialect dialect)
        where TMessage : struct, IMessage
    {
        ArrayBufferWriter<byte> writer = new();
        MessageCodec.Encode(writer, in message, dialect);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>A writer over one array, rewound between iterations so the encode is measured.</summary>
    private sealed class FixedWriter(byte[] buffer) : IBufferWriter<byte>
    {
        private int _written;

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0) => buffer.AsMemory(_written);

        public Span<byte> GetSpan(int sizeHint = 0) => buffer.AsSpan(_written);

        public void Reset() => _written = 0;
    }
}
