using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;

namespace NineP.TestSupport;

/// <summary>Locates the repository a test assembly was built from.</summary>
public static class RepositoryPaths
{
    private static readonly Lazy<string> RootLazy = new(FindRoot);

    /// <summary>The absolute path of the repository root.</summary>
    public static string Root => RootLazy.Value;

    /// <summary>Combines <see cref="Root"/> with repository-relative path segments.</summary>
    /// <param name="parts">The segments, in order.</param>
    /// <returns>The absolute path.</returns>
    public static string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "docs", "9p", "protocol-reference.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// One golden frame from <c>docs/9p/fixtures/wire-vectors.json</c>: the bytes, the dialect they
/// belong to, and the field values the generator recorded alongside them.
/// </summary>
public sealed class WireVector
{
    private readonly byte[] _bytes;

    internal WireVector(JsonElement element)
    {
        Name = element.GetProperty("name").GetString()!;
        DialectName = element.GetProperty("dialect").GetString()!;
        Type = (MessageType)element.GetProperty("type").GetByte();
        Size = element.GetProperty("size").GetInt32();
        // Clone detaches the element from the JsonDocument, which is disposed as soon as the
        // fixture has been read; without it every field access would fail later.
        Fields = element.GetProperty("fields").Clone();
        _bytes = Convert.FromHexString(element.GetProperty("hex").GetString()!);
    }

    /// <summary>The vector's name, which is the message name and sometimes a qualifier.</summary>
    public string Name { get; }

    /// <summary>The dialect string the generator recorded; "unknown" for the refused Rversion.</summary>
    public string DialectName { get; }

    /// <summary>The type number the frame carries.</summary>
    public MessageType Type { get; }

    /// <summary>The size the frame claims, which equals its length.</summary>
    public int Size { get; }

    /// <summary>The field values the generator recorded, as JSON.</summary>
    public JsonElement Fields { get; }

    /// <summary>
    /// The dialect a decoder should be given for this frame. A vector recorded as "unknown" is an
    /// <c>Rversion</c> refusing negotiation, and its frame is 9P2000-shaped.
    /// </summary>
    public Dialect Dialect => DialectName switch
    {
        Constants.Version9P2000u => Protocol.Dialect.P9_2000_u,
        Constants.Version9P2000L => Protocol.Dialect.P9_2000_L,
        _ => Protocol.Dialect.P9_2000,
    };

    /// <summary>The vector's frame as memory, ready for the codec.</summary>
    public ReadOnlyMemory<byte> Frame => _bytes;

    /// <summary>A fresh copy of the frame's bytes, which a mutation test may edit.</summary>
    /// <returns>The bytes.</returns>
    public byte[] ToBytes() => [.. _bytes];

    /// <summary>A string field.</summary>
    /// <param name="name">The field's name in the fixture.</param>
    /// <returns>The recorded value.</returns>
    public string Str(string name) => Fields.GetProperty(name).GetString()!;

    /// <summary>
    /// A numeric field. The generator writes 64-bit values as decimal strings so that no JSON
    /// parser can round them, so both forms are accepted.
    /// </summary>
    /// <param name="name">The field's name in the fixture.</param>
    /// <returns>The recorded value.</returns>
    public ulong Num(string name) => AsNumber(Fields.GetProperty(name));

    /// <summary>A qid field, whose three parts the generator records as an object.</summary>
    /// <param name="name">The field's name in the fixture.</param>
    /// <returns>The recorded qid.</returns>
    public Qid QidOf(string name) => QidFrom(Fields.GetProperty(name));

    /// <summary>Reads a number that the fixture may have written as a string.</summary>
    /// <param name="element">The JSON value.</param>
    /// <returns>The number.</returns>
    public static ulong AsNumber(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? ulong.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : element.GetUInt64();

    /// <summary>Reads a qid the fixture recorded as an object.</summary>
    /// <param name="element">The JSON value.</param>
    /// <returns>The qid.</returns>
    public static Qid QidFrom(JsonElement element) => new(
        (QidType)(byte)AsNumber(element.GetProperty("type")),
        (uint)AsNumber(element.GetProperty("version")),
        AsNumber(element.GetProperty("path")));
}

/// <summary>
/// The 88 golden frames of reference §9, read from the vendored fixture. They are the shared
/// truth of every implementation in the workspace, so they are read, never regenerated.
/// </summary>
public static class WireVectors
{
    private static readonly Lazy<IReadOnlyList<WireVector>> AllLazy = new(Load);

    /// <summary>Every vector in the fixture, in file order.</summary>
    public static IReadOnlyList<WireVector> All => AllLazy.Value;

    /// <summary>The vectors whose frames a session of that dialect would carry.</summary>
    /// <param name="dialect">The dialect to filter by.</param>
    /// <returns>The matching vectors, in file order.</returns>
    public static IEnumerable<WireVector> ForDialect(Dialect dialect) =>
        All.Where(v => v.Dialect == dialect);

    private static List<WireVector> Load()
    {
        using FileStream stream = File.OpenRead(
            RepositoryPaths.Combine("docs", "9p", "fixtures", "wire-vectors.json"));
        using JsonDocument document = JsonDocument.Parse(stream);

        List<WireVector> vectors = [];
        foreach (JsonElement element in document.RootElement.GetProperty("vectors").EnumerateArray())
        {
            vectors.Add(new WireVector(element));
        }

        return vectors;
    }
}

/// <summary>
/// Decodes and re-encodes a golden frame in the dialect the fixture recorded it under. It lives
/// here rather than in one test suite because task 12's harness, the mutation matrix and the fuzz
/// corpus all need the same mapping from a type number to its record.
/// </summary>
public static class VectorCodec
{
    /// <summary>Decodes the vector into its record and writes it back out.</summary>
    /// <param name="vector">The golden frame.</param>
    /// <returns>The bytes the encoder produced.</returns>
    /// <exception cref="ArgumentNullException">The vector is null.</exception>
    /// <exception cref="InvalidOperationException">The type number has no record.</exception>
    public static byte[] ReEncode(WireVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        ArrayBufferWriter<byte> buffer = new();
        Write(vector, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The exact length the encoder predicts for the vector's record.</summary>
    /// <param name="vector">The golden frame.</param>
    /// <returns>The predicted frame length.</returns>
    /// <exception cref="ArgumentNullException">The vector is null.</exception>
    public static int PredictedSize(WireVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        return Sizer(vector.Type)(vector.Frame, vector.Dialect);
    }

    private static void Write(WireVector vector, IBufferWriter<byte> destination) =>
        Writer(vector.Type)(vector.Frame, vector.Dialect, destination);

    private static Func<ReadOnlyMemory<byte>, Dialect, IBufferWriter<byte>, int> Writer(MessageType type) =>
        Dispatch<Func<ReadOnlyMemory<byte>, Dialect, IBufferWriter<byte>, int>>(
            type, typeof(VectorCodec).GetMethod(nameof(RoundTrip), BindingFlags.NonPublic | BindingFlags.Static)!);

    private static Func<ReadOnlyMemory<byte>, Dialect, int> Sizer(MessageType type) =>
        Dispatch<Func<ReadOnlyMemory<byte>, Dialect, int>>(
            type, typeof(VectorCodec).GetMethod(nameof(Measure), BindingFlags.NonPublic | BindingFlags.Static)!);

    private static TDelegate Dispatch<TDelegate>(MessageType type, MethodInfo definition)
        where TDelegate : Delegate =>
        definition.MakeGenericMethod(MessageRecords.RecordOf(type)).CreateDelegate<TDelegate>();

    private static int RoundTrip<TMessage>(
        ReadOnlyMemory<byte> frame, Dialect dialect, IBufferWriter<byte> destination)
        where TMessage : struct, IMessage
    {
        TMessage message = MessageCodec.Decode<TMessage>(frame, dialect);
        return MessageCodec.Encode(destination, in message, dialect);
    }

    private static int Measure<TMessage>(ReadOnlyMemory<byte> frame, Dialect dialect)
        where TMessage : struct, IMessage
    {
        TMessage message = MessageCodec.Decode<TMessage>(frame, dialect);
        return MessageCodec.GetEncodedSize(in message, dialect);
    }
}

/// <summary>Maps a type number to the record that encodes it, by reflection over the messages assembly.</summary>
public static class MessageRecords
{
    private static readonly Lazy<IReadOnlyDictionary<MessageType, Type>> ByTypeLazy = new(Build);

    /// <summary>Every wire-legal type number and the record it decodes into.</summary>
    public static IReadOnlyDictionary<MessageType, Type> ByType => ByTypeLazy.Value;

    /// <summary>The record for a type number.</summary>
    /// <param name="type">The type number peeked out of a frame.</param>
    /// <returns>The record type.</returns>
    /// <exception cref="InvalidOperationException">No record declares that type number.</exception>
    public static Type RecordOf(MessageType type) =>
        ByType.TryGetValue(type, out Type? record)
            ? record
            : throw new InvalidOperationException("no record for " + type);

    private static Dictionary<MessageType, Type> Build()
    {
        Dictionary<MessageType, Type> records = [];
        foreach (Type candidate in typeof(IMessage).Assembly.GetTypes())
        {
            if (!candidate.IsValueType || !typeof(IMessage).IsAssignableFrom(candidate))
            {
                continue;
            }

            // IMessage.Type is a static abstract member, so the value comes from the interface map
            // rather than from a property on the record itself.
            MethodInfo getter = candidate.GetInterfaceMap(typeof(IMessage))
                .TargetMethods
                .First(m => m.Name.EndsWith("get_Type", StringComparison.Ordinal));
            records[(MessageType)getter.Invoke(null, null)!] = candidate;
        }

        return records;
    }
}
