using System.Buffers;
using System.Globalization;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests.StateMachine;

/// <summary>
/// The codec's laws, checked over generated inputs rather than over chosen ones: decoding what was
/// encoded gives the record back, encoding what was decoded gives the bytes back, and no input at
/// all makes the decoder throw anything but <see cref="NinePProtocolException"/>. FsCheck drives
/// the generators; the deterministic mutation loop below is the part that runs identically
/// everywhere, including where the libFuzzer driver cannot be built (RK-74).
/// </summary>
[Trait("Category", "StateMachine")]
public sealed class CodecProperties
{
    private const int CorpusSize = 88;
    private const ulong LoopSeed = 0x9E3779B97F4A7C15;

    private static readonly Dialect[] Dialects =
        [Dialect.P9_2000, Dialect.P9_2000_u, Dialect.P9_2000_L];

    // Whole graphemes, not chars: an alphabet of chars would generate lone surrogates, which are
    // not text at all and which the strict encoder rightly refuses to write.
    private static readonly string[] Alphabet =
        ["a", "b", "c", "X", "Y", "Z", "0", "1", "9", ".", "_", "-", " ", "é", "ü", "中", "😀"];

    /// <summary>A Tversion survives encode then decode with every field intact.</summary>
    [Fact]
    public void TversionRoundTrips() =>
        Prop.ForAll(Tags(), Sizes(), Texts(), (tag, msize, version) =>
        {
            Tversion message = new(tag, msize, version);
            return RoundTrips(in message, Dialect.P9_2000);
        }).QuickCheckThrowOnFailure();

    /// <summary>A Twalk with up to MAXWELEM names survives encode then decode.</summary>
    [Fact]
    public void TwalkRoundTrips() =>
        Prop.ForAll(Tags(), Sizes(), Names(), (tag, fid, names) =>
        {
            Twalk message = new(tag, fid, fid + 1, names);
            return RoundTrips(in message, Dialect.P9_2000)
                && MessageCodec.Decode<Twalk>(Encode(in message, Dialect.P9_2000), Dialect.P9_2000)
                    .Wnames.SequenceEqual(names);
        }).QuickCheckThrowOnFailure();

    /// <summary>A Twrite's payload comes back byte for byte, whatever its length.</summary>
    [Fact]
    public void TwritePayloadRoundTrips() =>
        Prop.ForAll(Tags(), Offsets(), Payloads(), (tag, offset, data) =>
        {
            Twrite message = new(tag, 3, offset, data);
            bool valid = MessageCodec.TryDecode(Encode(in message, Dialect.P9_2000), Dialect.P9_2000,
                out Twrite decoded, out ProtocolErrorKind failure);
            return offset > ulong.MaxValue - (ulong)data.Length
                ? !valid && failure == ProtocolErrorKind.Overflow
                : valid && decoded.Offset == offset && decoded.Data.Span.SequenceEqual(data);
        }).QuickCheckThrowOnFailure();

    /// <summary>The .u Rerror carries its errno, and the 9P2000 one does not have the field at all.</summary>
    [Fact]
    public void RerrorRoundTripsInBothDialects() =>
        Prop.ForAll(Tags(), Sizes(), Texts(), (tag, errno, ename) =>
        {
            Rerror unix = new(tag, ename, (int)(errno & 0x7FFFFFFF));
            Rerror plan9 = new(tag, ename, 0);
            return RoundTrips(in unix, Dialect.P9_2000_u) && RoundTrips(in plan9, Dialect.P9_2000);
        }).QuickCheckThrowOnFailure();

    /// <summary>An Rgetattr survives the round trip and stays 160 bytes whatever it carries.</summary>
    [Fact]
    public void RgetattrRoundTripsAtAFixedLength() =>
        Prop.ForAll(Tags(), Offsets(), Offsets(), (tag, size, gen) =>
        {
            Rgetattr message = new(
                tag, GetAttrMask.All, new Qid(QidType.QTFILE, 1, gen), 0x81A4, 1000, 100, 1, 0,
                size, 4096, 8, new TimeSpec(1, 2), new TimeSpec(3, 4), new TimeSpec(5, 6),
                default, gen, size);

            return RoundTrips(in message, Dialect.P9_2000_L)
                && Encode(in message, Dialect.P9_2000_L).Length == 160;
        }).QuickCheckThrowOnFailure();

    /// <summary>Encoding what was decoded reproduces the frame exactly, for every golden vector.</summary>
    [Fact]
    public void EncodeAfterDecodeIsByteIdentical() =>
        Prop.ForAll(VectorIndexes(), index =>
        {
            WireVector vector = WireVectors.All[index];

            // The 11-byte Tfsync is the one frame the encoder deliberately lengthens (S-19).
            return VectorCodec.ReEncode(vector).SequenceEqual(vector.Name == "Tfsync (no datasync)"
                ? WireVectors.All.First(v => v.Name == "Tfsync").ToBytes() : vector.ToBytes());
        }).QuickCheckThrowOnFailure();

    /// <summary>No sequence of bytes at all makes the decoder throw something untyped.</summary>
    [Fact]
    public void ArbitraryBytesNeverThrowAnythingElse() =>
        Prop.ForAll(Types(), Payloads(), (type, body) =>
        {
            byte[] frame = Frame(type, body);
            foreach (Dialect dialect in Dialects)
            {
                Decode(frame, dialect);
            }

            return true;
        }).QuickCheckThrowOnFailure();

    /// <summary>Nor does any single-byte corruption of a golden frame.</summary>
    [Fact]
    public void CorruptedGoldenFramesNeverThrowAnythingElse() =>
        Prop.ForAll(VectorIndexes(), Offsets(), Bytes(), (index, at, value) =>
        {
            byte[] frame = WireVectors.All[index].ToBytes();
            frame[(int)(at % (ulong)frame.Length)] = value;

            foreach (Dialect dialect in Dialects)
            {
                Decode(frame, dialect);
            }

            return true;
        }).QuickCheckThrowOnFailure();

    /// <summary>
    /// The same target the fuzz harness drives, run deterministically from a fixed seed so that a
    /// failure here reproduces exactly. This is the part of the fuzz step that runs on every
    /// platform, whether or not libFuzzer can be built there.
    /// </summary>
    [Fact]
    public void DeterministicMutationLoopFindsNothingUntyped()
    {
        ulong state = LoopSeed;
        IReadOnlyList<byte[]> seeds = [.. WireVectors.All.Select(v => v.ToBytes())];

        for (int i = 0; i < 20_000; i++)
        {
            byte[] input = [.. seeds[(int)(Next(ref state) % (ulong)seeds.Count)]];
            int edits = 1 + (int)(Next(ref state) % 4);
            for (int edit = 0; edit < edits; edit++)
            {
                input[(int)(Next(ref state) % (ulong)input.Length)] = (byte)Next(ref state);
            }

            foreach (Dialect dialect in Dialects)
            {
                Decode(input, dialect);
            }
        }
    }

    /// <summary>The committed fuzz corpus is exactly the golden vectors, in hex so it stays text.</summary>
    [Fact]
    public void FuzzCorpusIsSeededFromTheVectors()
    {
        string corpus = RepositoryPaths.Combine("tests", "NineP.Fuzz", "corpus");
        string[] files = [.. Directory.EnumerateFiles(corpus, "*.hex").OrderBy(f => f, StringComparer.Ordinal)];

        Assert.Equal(CorpusSize, files.Length);
        Assert.Equal(WireVectors.All.Count, files.Length);

        HashSet<string> seeded = [.. files.Select(f => File.ReadAllText(f).Trim().ToUpperInvariant())];
        foreach (WireVector vector in WireVectors.All)
        {
            Assert.Contains(Convert.ToHexString(vector.ToBytes()), seeded);
        }
    }

    private static void Decode(byte[] frame, Dialect dialect)
    {
        if (frame.Length < Constants.HDRSZ)
        {
            return;
        }

        // TryDecode reports rather than throws; the property is that it never does anything else,
        // so simply running it to completion over every generated input is the assertion.
        VectorProbe.TryDecode((MessageType)frame[4], frame, dialect);
    }

    private static byte[] Frame(MessageType type, byte[] body)
    {
        byte[] frame = new byte[Constants.HDRSZ + body.Length];
        frame[0] = (byte)(frame.Length & 0xFF);
        frame[1] = (byte)((frame.Length >> 8) & 0xFF);
        frame[4] = (byte)type;
        body.CopyTo(frame.AsSpan(Constants.HDRSZ));
        return frame;
    }

    private static bool RoundTrips<TMessage>(in TMessage message, Dialect dialect)
        where TMessage : struct, IMessage
    {
        byte[] encoded = Encode(in message, dialect);
        TMessage decoded = MessageCodec.Decode<TMessage>(encoded, dialect);
        return Encode(in decoded, dialect).SequenceEqual(encoded);
    }

    private static byte[] Encode<TMessage>(in TMessage message, Dialect dialect)
        where TMessage : struct, IMessage
    {
        ArrayBufferWriter<byte> buffer = new();
        MessageCodec.Encode(buffer, in message, dialect);
        return buffer.WrittenSpan.ToArray();
    }

    private static ulong Next(ref ulong state)
    {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        return state;
    }

    private static Arbitrary<ushort> Tags() =>
        Arb.From(Gen.Choose(0, ushort.MaxValue).Select(i => (ushort)i));

    private static Arbitrary<uint> Sizes() =>
        Arb.From(Gen.OneOf(Gen.Choose(0, int.MaxValue).Select(i => (uint)i), Gen.Elements(0u, 1u, uint.MaxValue - 1, uint.MaxValue)));

    private static Arbitrary<ulong> Offsets() =>
        Arb.From(Gen.OneOf(Gen.Choose(0, int.MaxValue).Select(i => (ulong)i), Gen.Elements(0ul, 1ul, (ulong)uint.MaxValue, ulong.MaxValue - 1, ulong.MaxValue)));

    private static Arbitrary<byte> Bytes() =>
        Arb.From(Gen.Choose(0, byte.MaxValue).Select(i => (byte)i));

    private static Arbitrary<int> VectorIndexes() =>
        Arb.From(Gen.Choose(0, WireVectors.All.Count - 1));

    private static Arbitrary<MessageType> Types() =>
        Arb.From(Gen.Elements([.. MessageRecords.ByType.Keys]));

    private static Arbitrary<byte[]> Payloads() =>
        Arb.From(Gen.ArrayOf(Gen.Choose(0, byte.MaxValue).Select(i => (byte)i)));

    private static Arbitrary<string> Texts() =>
        Arb.From(Gen.ArrayOf(Gen.Elements(Alphabet)).Select(parts => Sanitise(string.Concat(parts))));

    private static Arbitrary<IReadOnlyList<string>> Names() =>
        Arb.From(Gen.Choose(0, Constants.MAXWELEM)
            .Select(n => (IReadOnlyList<string>)[.. Enumerable.Range(0, n).Select(i =>
                string.Format(CultureInfo.InvariantCulture, "n{0}", i))]));

    private static string Sanitise(string value)
    {
        // A generated string still has to be a legal 9P string: no NUL, and at most 65535 bytes.
        StringBuilder text = new(value.Length);
        foreach (char c in value)
        {
            if (c != '\0')
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }
}

/// <summary>Decodes a frame as the record its type byte names, without knowing that type at compile time.</summary>
internal static class VectorProbe
{
    /// <summary>Attempts the decode and reports whether it succeeded.</summary>
    /// <param name="type">The type byte peeked out of the frame.</param>
    /// <param name="frame">The frame.</param>
    /// <param name="dialect">The dialect to read it in.</param>
    /// <returns>True when the frame decoded cleanly.</returns>
    public static bool TryDecode(MessageType type, ReadOnlyMemory<byte> frame, Dialect dialect) =>
        Decoders.TryGetValue(type, out Func<ReadOnlyMemory<byte>, Dialect, bool>? decode)
        && decode(frame, dialect);

    private static readonly Dictionary<MessageType, Func<ReadOnlyMemory<byte>, Dialect, bool>> Decoders = Build();

    private static Dictionary<MessageType, Func<ReadOnlyMemory<byte>, Dialect, bool>> Build()
    {
        System.Reflection.MethodInfo definition = typeof(VectorProbe).GetMethod(
            nameof(One),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        Dictionary<MessageType, Func<ReadOnlyMemory<byte>, Dialect, bool>> decoders = [];
        foreach ((MessageType type, Type record) in MessageRecords.ByType)
        {
            decoders[type] = definition
                .MakeGenericMethod(record)
                .CreateDelegate<Func<ReadOnlyMemory<byte>, Dialect, bool>>();
        }

        return decoders;
    }

    private static bool One<TMessage>(ReadOnlyMemory<byte> frame, Dialect dialect)
        where TMessage : struct, IMessage =>
        MessageCodec.TryDecode(frame, dialect, out TMessage _, out _);
}
