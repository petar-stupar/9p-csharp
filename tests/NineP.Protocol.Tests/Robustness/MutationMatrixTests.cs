using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Codec.Internal;
using NineP.Protocol.Internal;
using NineP.Protocol.Messages;
using NineP.Protocol.Tests;
using NineP.Protocol.Tests.Conformance;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests.Robustness;

/// <summary>
/// Reference §9's mutation matrix over every golden frame: truncate by one byte, bump the size,
/// claim seventeen walk elements, plant a NUL in a string, break the stat record's inner size,
/// claim more payload than the frame holds, append trailing bytes, break
/// <c>Twrite.count == size - 23</c> and wrap <c>offset + count</c>. Each mutation must yield the
/// one typed kind that names it, nothing but <see cref="NinePProtocolException"/> may escape, and
/// a frame that merely claims a huge length must not make the decoder allocate one.
/// </summary>
[Trait("Category", "Robustness")]
public sealed class MutationMatrixTests
{
    private const int BodyOffset = Constants.HDRSZ;

    /// <summary>Every (vector, mutation) pair the matrix defines.</summary>
    /// <returns>One row per applicable mutation.</returns>
    public static TheoryData<string, int, string> Matrix()
    {
        TheoryData<string, int, string> data = [];
        for (int i = 0; i < WireVectors.All.Count; i++)
        {
            foreach (string mutation in Mutations.ApplicableTo(WireVectors.All[i]))
            {
                data.Add(WireVectors.All[i].Name, i, mutation);
            }
        }

        return data;
    }

    /// <summary>Every mutation of every vector fails with the kind that names it.</summary>
    /// <param name="name">The vector's name, so a failure says which one broke.</param>
    /// <param name="index">The vector's position in the fixture.</param>
    /// <param name="mutation">Which mutation to apply.</param>
    [Theory]
    [MemberData(nameof(Matrix))]
    public void EveryMutationYieldsATypedError(string name, int index, string mutation)
    {
        WireVector vector = WireVectors.All[index];
        byte[] frame = Mutations.Apply(vector, mutation);

        ProtocolErrorKind expected = Mutations.ExpectedKind(vector, mutation);
        ProtocolErrorKind actual = Attempt(vector, frame);

        Assert.True(
            expected == actual,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} + {1}: expected {2}, got {3}",
                name,
                mutation,
                expected,
                actual));
    }

    /// <summary>
    /// A <c>Twrite</c> whose count does not equal <c>size - 23</c> is malformed either way: too
    /// large overruns the frame, too small leaves bytes over (reference §8 rule 4).
    /// </summary>
    [Fact]
    public void TwriteCountDisagreesWithSize()
    {
        WireVector vector = WireVectors.All.First(v => v.Type == MessageType.Twrite);
        int countOffset = BodyOffset + 4 + 8;
        uint legal = (uint)(vector.Size - Constants.TwriteHeaderSize);

        byte[] tooLarge = vector.ToBytes();
        BinaryPrimitives.WriteUInt32LittleEndian(tooLarge.AsSpan(countOffset), legal + 1);
        Assert.Equal(ProtocolErrorKind.Bounds, Attempt(vector, tooLarge));

        byte[] tooSmall = vector.ToBytes();
        BinaryPrimitives.WriteUInt32LittleEndian(tooSmall.AsSpan(countOffset), legal - 1);
        Assert.Equal(ProtocolErrorKind.Trailing, Attempt(vector, tooSmall));

        // The legal value still decodes, so the rule rejects disagreement and not the message.
        Assert.Equal(legal, (uint)MessageCodec.Decode<Twrite>(vector.Frame, vector.Dialect).Data.Length);
    }

    /// <summary>
    /// A frame that claims a length it does not carry allocates nothing in proportion to the claim
    /// (reference §9, RK-13): every counted field is checked against the bytes that are there.
    /// </summary>
    [Fact]
    public void AClaimedLengthNeverAllocates()
    {
        foreach (Func<bool> attempt in ClaimAttempts())
        {
            // Warm up so that the measurement sees the decode and not the JIT. Each attempt
            // returns true when the decoder refused the frame, which is the only outcome here.
            for (int i = 0; i < 8; i++)
            {
                Assert.True(attempt());
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 64; i++)
            {
                Assert.True(attempt());
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 4096, $"decoding a claimed length allocated {allocated} bytes");
        }
    }

    /// <summary>A NUL inside a dirent name is rejected where dirents are read.</summary>
    [Fact]
    public void ANulInADirentNameIsRejected()
    {
        WireVector vector = WireVectors.All.First(v => v.Type == MessageType.Rreaddir);
        byte[] frame = Mutations.Apply(vector, Mutations.NulInString);

        Rreaddir message = MessageCodec.Decode<Rreaddir>(frame, Dialect.P9_2000_L);

        Assert.False(DirEntryCodec.TryReadAll(
            message.Data, out IReadOnlyList<DirEntry> entries, out ProtocolErrorKind failure));
        Assert.Empty(entries);
        Assert.Equal(ProtocolErrorKind.Nul, failure);
    }

    /// <summary>Nothing but a typed protocol exception escapes the codec, whatever the input.</summary>
    /// <param name="name">The vector's name, so a failure says which one broke.</param>
    /// <param name="index">The vector's position in the fixture.</param>
    /// <param name="mutation">Which mutation to apply.</param>
    [Theory]
    [MemberData(nameof(Matrix))]
    public void OnlyNinePProtocolExceptionEscapes(string name, int index, string mutation)
    {
        WireVector vector = WireVectors.All[index];
        byte[] frame = Mutations.Apply(vector, mutation);

        NinePProtocolException thrown = Assert.Throws<NinePProtocolException>(
            () => Throwing(vector, frame));

        Assert.Equal(Mutations.ExpectedKind(vector, mutation), thrown.Kind);
        Assert.Equal(name.Split(' ')[0], MessageTypes.GetName(vector.Type));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AnOuterStatCountMustMatchTheRecord(int count)
    {
        foreach (WireVector vector in WireVectors.All.Where(v => v.Type is MessageType.Rstat or MessageType.Twstat))
        {
            byte[] frame = vector.ToBytes();
            int at = vector.Type == MessageType.Rstat ? 7 : 11;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(at), (ushort)count);
            Assert.Equal(ProtocolErrorKind.Stat, Attempt(vector, frame));
        }
    }

    [Theory]
    [InlineData(MessageType.Rread)]
    [InlineData(MessageType.Twrite)]
    [InlineData(MessageType.Rreaddir)]
    public void MaximumPayloadCountIsBoundsWithoutAllocatingTheClaim(MessageType type)
    {
        WireVector vector = WireVectors.All.First(v => v.Type == type);
        byte[] frame = vector.ToBytes();
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(type == MessageType.Twrite ? 19 : 7), uint.MaxValue);
        Assert.Equal(ProtocolErrorKind.Bounds, Attempt(vector, frame));
    }

    private static IEnumerable<Func<bool>> ClaimAttempts()
    {
        byte[] walk = Claimed(MessageType.Twalk, 8, ushort.MaxValue);
        byte[] rwalk = Claimed(MessageType.Rwalk, 0, ushort.MaxValue);
        byte[] rread = Payload(MessageType.Rread, uint.MaxValue);
        byte[] rreaddir = Payload(MessageType.Rreaddir, uint.MaxValue);

        byte[] write = WireVectors.All.First(v => v.Type == MessageType.Twrite).ToBytes();
        BinaryPrimitives.WriteUInt32LittleEndian(write.AsSpan(19), uint.MaxValue);
        yield return () => !MessageCodec.TryDecode(write, Dialect.P9_2000, out Twrite _, out _);
        yield return () => !MessageCodec.TryDecode(walk, Dialect.P9_2000, out Twalk _, out _);
        yield return () => !MessageCodec.TryDecode(rwalk, Dialect.P9_2000, out Rwalk _, out _);
        yield return () => !MessageCodec.TryDecode(rread, Dialect.P9_2000, out Rread _, out _);
        yield return () => !MessageCodec.TryDecode(rreaddir, Dialect.P9_2000_L, out Rreaddir _, out _);
    }

    private static byte[] Claimed(MessageType type, int leading, ushort count)
    {
        byte[] frame = new byte[BodyOffset + leading + 2];
        Header(frame, type);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(BodyOffset + leading), count);
        return frame;
    }

    private static byte[] Payload(MessageType type, uint count)
    {
        byte[] frame = new byte[BodyOffset + 4];
        Header(frame, type);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(BodyOffset), (uint)count);
        return frame;
    }

    private static void Header(byte[] frame, MessageType type)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frame.Length);
        frame[4] = (byte)type;
    }

    private static ProtocolErrorKind Attempt(WireVector vector, byte[] frame) =>
        (ProtocolErrorKind)Generic(nameof(TryOne), vector.Type)
            .Invoke(null, [(ReadOnlyMemory<byte>)frame, vector.Dialect])!;

    private static void Throwing(WireVector vector, byte[] frame) =>
        Invoke(Generic(nameof(DecodeOne), vector.Type), frame, vector.Dialect);

    private static void Invoke(MethodInfo method, byte[] frame, Dialect dialect)
    {
        try
        {
            method.Invoke(null, [(ReadOnlyMemory<byte>)frame, dialect]);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            // Reflection wraps whatever the codec threw; the assertion is about what is inside.
            throw e.InnerException;
        }
    }

    private static MethodInfo Generic(string name, MessageType type) =>
        typeof(MutationMatrixTests)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(MessageRecords.RecordOf(type));

    private static ProtocolErrorKind TryOne<TMessage>(ReadOnlyMemory<byte> frame, Dialect dialect)
        where TMessage : struct, IMessage
    {
        Assert.False(MessageCodec.TryDecode(frame, dialect, out TMessage _, out ProtocolErrorKind failure));
        return failure;
    }

    private static void DecodeOne<TMessage>(ReadOnlyMemory<byte> frame, Dialect dialect)
        where TMessage : struct, IMessage =>
        MessageCodec.Decode<TMessage>(frame, dialect);
}

/// <summary>The nine mutations of reference §9, and which frames each one applies to.</summary>
internal static class Mutations
{
    public const string TruncateByOne = "truncate-by-one";
    public const string BumpSize = "bump-size";
    public const string AppendTrailing = "append-trailing";
    public const string NulInString = "nul-in-string";
    public const string WalkCountSeventeen = "walk-count-17";
    public const string StatInnerSize = "stat-inner-size";
    public const string CountOverrunsFrame = "count-overruns-frame";
    public const string OffsetOverflow = "offset-overflow";

    private const int BodyOffset = Constants.HDRSZ;
    private const string ShortFsync = "Tfsync (no datasync)";

    /// <summary>Which mutations make sense for this frame.</summary>
    /// <param name="vector">The golden frame.</param>
    /// <returns>The mutation names, in a stable order.</returns>
    public static IEnumerable<string> ApplicableTo(WireVector vector)
    {
        yield return TruncateByOne;
        yield return BumpSize;
        yield return AppendTrailing;

        // Rreaddir's payload is an opaque blob to the message decoder: the strings inside it are
        // dirent names, and DirEntryCodec is what rejects a NUL in one (see the dedicated test).
        if (vector.Type != MessageType.Rreaddir && FindString(vector) >= 0)
        {
            yield return NulInString;
        }

        if (vector.Type is MessageType.Twalk or MessageType.Rwalk)
        {
            yield return WalkCountSeventeen;
        }

        if (vector.Type is MessageType.Rstat or MessageType.Twstat)
        {
            yield return StatInnerSize;
        }

        if (vector.Type is MessageType.Rread or MessageType.Rreaddir or MessageType.Twrite)
        {
            yield return CountOverrunsFrame;
        }

        // Tread is not in the list: reference §8 rule 5 guards Twrite, Tsetattr and Tlock, and a
        // read past the end of the address space is a read that returns nothing.
        if (vector.Type is MessageType.Twrite && vector.Size > Constants.TwriteHeaderSize)
        {
            yield return OffsetOverflow;
        }
    }

    /// <summary>The kind the decoder must report for this mutation of this frame.</summary>
    /// <param name="vector">The golden frame.</param>
    /// <param name="mutation">The mutation being applied.</param>
    /// <returns>The expected failure kind.</returns>
    public static ProtocolErrorKind ExpectedKind(WireVector vector, string mutation) => mutation switch
    {
        TruncateByOne or BumpSize => ProtocolErrorKind.Size,

        // The 11-byte Tfsync is the one frame with an optional trailing field: one byte more makes
        // the datasync[4] read run off the end rather than leave a byte over.
        AppendTrailing => vector.Name == ShortFsync ? ProtocolErrorKind.Bounds : ProtocolErrorKind.Trailing,
        NulInString => ProtocolErrorKind.Nul,
        WalkCountSeventeen => ProtocolErrorKind.NWName,
        StatInnerSize => ProtocolErrorKind.Stat,
        CountOverrunsFrame => ProtocolErrorKind.Bounds,
        OffsetOverflow => ProtocolErrorKind.Overflow,
        _ => throw new InvalidOperationException("unknown mutation " + mutation),
    };

    /// <summary>Applies one mutation to a copy of the frame.</summary>
    /// <param name="vector">The golden frame.</param>
    /// <param name="mutation">The mutation to apply.</param>
    /// <returns>The mutated bytes.</returns>
    public static byte[] Apply(WireVector vector, string mutation)
    {
        byte[] frame = vector.ToBytes();

        switch (mutation)
        {
            case TruncateByOne:
                return frame[..^1];
            case BumpSize:
                BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frame.Length + 1);
                return frame;
            case AppendTrailing:
                return Append(frame);
            case NulInString:
                frame[FindString(vector)] = 0;
                return frame;
            case WalkCountSeventeen:
                BinaryPrimitives.WriteUInt16LittleEndian(
                    frame.AsSpan(vector.Type == MessageType.Twalk ? BodyOffset + 8 : BodyOffset),
                    Constants.MAXWELEM + 1);
                return frame;
            case StatInnerSize:
                return CorruptStat(vector, frame);
            case CountOverrunsFrame:
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(CountOffset(vector)), uint.MaxValue - 8);
                return frame;
            case OffsetOverflow:
                BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(BodyOffset + 4), ulong.MaxValue);
                return frame;
            default:
                throw new InvalidOperationException("unknown mutation " + mutation);
        }
    }

    private static byte[] Append(byte[] frame)
    {
        byte[] longer = [.. frame, (byte)0xAA];
        BinaryPrimitives.WriteUInt32LittleEndian(longer, (uint)longer.Length);
        return longer;
    }

    private static byte[] CorruptStat(WireVector vector, byte[] frame)
    {
        // stat[n] is n[2] followed by a record whose own first field is size[2] = n - 2
        // (reference §3.3). Twstat carries a fid[4] before it, Rstat does not.
        int inner = BodyOffset + (vector.Type == MessageType.Twstat ? 4 : 0) + 2;
        ushort declared = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(inner));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(inner), (ushort)(declared - 1));
        return frame;
    }

    private static int CountOffset(WireVector vector) =>
        vector.Type == MessageType.Twrite ? BodyOffset + 4 + 8 : BodyOffset;

    /// <summary>
    /// The offset of the first byte of the first non-empty string on the wire, found by its
    /// length prefix so that a value cannot be matched inside some other field.
    /// </summary>
    /// <param name="vector">The golden frame.</param>
    /// <returns>The offset, or -1 when the frame carries no non-empty string.</returns>
    public static int FindString(WireVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        foreach (string candidate in Strings(vector.Fields))
        {
            byte[] bytes = NinePText.Utf8.GetBytes(candidate);
            byte[] needle = [(byte)(bytes.Length & 0xFF), (byte)(bytes.Length >> 8), .. bytes];
            int at = vector.Frame.Span.IndexOf(needle);
            if (at >= 0)
            {
                return at + 2;
            }
        }

        return -1;
    }

    private static IEnumerable<string> Strings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                string value = element.GetString()!;
                if (value.Length > 0)
                {
                    yield return value;
                }

                break;
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    foreach (string nested in Strings(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    foreach (string nested in Strings(item))
                    {
                        yield return nested;
                    }
                }

                break;
            default:
                break;
        }
    }
}
