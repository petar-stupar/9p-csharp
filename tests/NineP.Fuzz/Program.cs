using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using NineP.Protocol;
using NineP.Protocol.Codec;
using NineP.Protocol.Messages;
using SharpFuzz;

namespace NineP.Fuzz;

/// <summary>
/// The decoder's fuzz target. Under libFuzzer (<c>--libfuzzer</c>, driven by
/// <c>libfuzzer-dotnet</c>) every input goes through <see cref="FuzzTarget.Run"/>; without it the
/// same target is driven by a deterministic mutation loop over the golden corpus, so the CI step
/// is meaningful on a machine where the libFuzzer driver cannot be built (RK-74). Either way the
/// contract is the same: nothing but <see cref="NinePProtocolException"/> may escape the codec.
/// </summary>
internal static class Program
{
    private const string LibFuzzerFlag = "--libfuzzer";
    private const string SeedCorpusFlag = "--seed-corpus";
    private const string CorpusFlag = "--corpus";

    private static int Main(string[] args)
    {
        string corpus = Option(args, CorpusFlag) ?? CorpusDirectory.Locate();

        if (Option(args, SeedCorpusFlag) is string destination)
        {
            int written = CorpusDirectory.Materialise(corpus, destination);
            Console.Out.WriteLine(Text("wrote {0} corpus files to {1}", written, destination));
            return 0;
        }

        if (Array.IndexOf(args, LibFuzzerFlag) >= 0)
        {
            Fuzzer.LibFuzzer.Run(FuzzTarget.Run);
            return 0;
        }

        return MutationLoop.Run(corpus, args, Console.Out);
    }

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static string Text(string format, params object[] arguments) =>
        string.Format(CultureInfo.InvariantCulture, format, arguments);
}

/// <summary>Decodes one input as every record its type byte could name, in all three dialects.</summary>
internal static class FuzzTarget
{
    private static readonly Dialect[] Dialects =
        [Dialect.P9_2000, Dialect.P9_2000_u, Dialect.P9_2000_L];

    private static readonly Func<ReadOnlyMemory<byte>, Dialect, bool>?[] Decoders = BuildDecoders();

    /// <summary>Runs the target over one input; a protocol failure is the expected outcome.</summary>
    /// <param name="input">The bytes the fuzzer produced.</param>
    public static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < Constants.HDRSZ)
        {
            return;
        }

        ReadOnlyMemory<byte> frame = input.ToArray();
        Func<ReadOnlyMemory<byte>, Dialect, bool>? decode = Decoders[frame.Span[4]];
        if (decode is null)
        {
            return;
        }

        foreach (Dialect dialect in Dialects)
        {
            decode(frame, dialect);
        }
    }

    private static Func<ReadOnlyMemory<byte>, Dialect, bool>?[] BuildDecoders()
    {
        MethodInfo definition = typeof(FuzzTarget)
            .GetMethod(nameof(TryDecode), BindingFlags.NonPublic | BindingFlags.Static)!;

        Func<ReadOnlyMemory<byte>, Dialect, bool>?[] decoders = new Func<ReadOnlyMemory<byte>, Dialect, bool>?[256];
        foreach (Type candidate in typeof(IMessage).Assembly.GetTypes())
        {
            if (!candidate.IsValueType || !typeof(IMessage).IsAssignableFrom(candidate))
            {
                continue;
            }

            MethodInfo getter = candidate.GetInterfaceMap(typeof(IMessage))
                .TargetMethods
                .First(m => m.Name.EndsWith("get_Type", StringComparison.Ordinal));
            byte number = (byte)(MessageType)getter.Invoke(null, null)!;
            decoders[number] = definition
                .MakeGenericMethod(candidate)
                .CreateDelegate<Func<ReadOnlyMemory<byte>, Dialect, bool>>();
        }

        return decoders;
    }

    private static bool TryDecode<TMessage>(ReadOnlyMemory<byte> frame, Dialect dialect)
        where TMessage : struct, IMessage =>
        MessageCodec.TryDecode(frame, dialect, out TMessage _, out _);
}

/// <summary>Finds and materialises the hex-encoded corpus that ships with the repository.</summary>
internal static class CorpusDirectory
{
    /// <summary>The repository's <c>tests/NineP.Fuzz/corpus</c> directory.</summary>
    /// <returns>The absolute path.</returns>
    /// <exception cref="DirectoryNotFoundException">The repository layout was not recognised.</exception>
    public static string Locate()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "NineP.Fuzz", "corpus");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("no corpus directory above " + AppContext.BaseDirectory);
    }

    /// <summary>Every corpus entry, decoded from its hex form, in file-name order.</summary>
    /// <param name="corpus">The corpus directory.</param>
    /// <returns>The seed inputs.</returns>
    public static IReadOnlyList<byte[]> Read(string corpus) =>
        [.. Files(corpus).Select(f => Convert.FromHexString(File.ReadAllText(f).Trim()))];

    /// <summary>
    /// Writes the corpus out as raw binary files, which is the form libFuzzer wants. The committed
    /// form is hex text because the repository forbids control bytes in tracked files.
    /// </summary>
    /// <param name="corpus">The hex corpus directory.</param>
    /// <param name="destination">Where to write the binary files.</param>
    /// <returns>How many files were written.</returns>
    public static int Materialise(string corpus, string destination)
    {
        Directory.CreateDirectory(destination);
        int written = 0;

        foreach (string file in Files(corpus))
        {
            byte[] bytes = Convert.FromHexString(File.ReadAllText(file).Trim());
            File.WriteAllBytes(Path.Combine(destination, Path.GetFileNameWithoutExtension(file) + ".bin"), bytes);
            written++;
        }

        return written;
    }

    private static IEnumerable<string> Files(string corpus) =>
        Directory.EnumerateFiles(corpus, "*.hex").OrderBy(f => f, StringComparer.Ordinal);
}

/// <summary>
/// A deterministic mutation loop over the corpus. It is not a coverage-guided fuzzer and does not
/// pretend to be one; it is the part of the fuzz step that runs everywhere, with a fixed seed so
/// that a failure can be reproduced exactly.
/// </summary>
internal static class MutationLoop
{
    private const ulong Seed = 0x9E3779B97F4A7C15;
    private const int DefaultSeconds = 5;

    /// <summary>Runs the loop within the budget the arguments give.</summary>
    /// <param name="corpus">The corpus directory.</param>
    /// <param name="args">libFuzzer-style arguments; <c>-max_total_time</c> and <c>-runs</c> are honoured.</param>
    /// <param name="output">Where the summary goes.</param>
    /// <returns>0 when nothing but a protocol exception escaped, 1 otherwise.</returns>
    public static int Run(string corpus, string[] args, TextWriter output)
    {
        IReadOnlyList<byte[]> seeds = CorpusDirectory.Read(corpus);
        if (seeds.Count == 0)
        {
            output.WriteLine("corpus is empty: " + corpus);
            return 1;
        }

        foreach (string file in args.Where(File.Exists))
        {
            FuzzTarget.Run(File.ReadAllBytes(file));
        }

        long seconds = Number(args, "-max_total_time=") ?? DefaultSeconds;
        long runs = Number(args, "-runs=") ?? long.MaxValue;

        Stopwatch clock = Stopwatch.StartNew();
        Xorshift random = new(Seed);
        long executed = 0;

        while (executed < runs && clock.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            byte[] input = Mutate(seeds[(int)(random.Next() % (ulong)seeds.Count)], random);
            if (!Attempt(input, output))
            {
                return 1;
            }

            executed++;
        }

        output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "mutation loop: {0} inputs from {1} seeds in {2} ms, no unexpected exception",
            executed,
            seeds.Count,
            clock.ElapsedMilliseconds));
        return 0;
    }

    /// <summary>Applies one deterministic mutation to a copy of a seed.</summary>
    /// <param name="seed">The seed input.</param>
    /// <param name="random">The generator, whose state advances.</param>
    /// <returns>The mutated bytes.</returns>
    public static byte[] Mutate(byte[] seed, Xorshift random)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(random);

        byte[] input = seed;
        switch (random.Next() % 4)
        {
            case 0:
                input = seed.Length > 1 ? seed[..^(int)(1 + (random.Next() % (ulong)(seed.Length - 1)))] : seed;
                break;
            case 1:
                input = [.. seed, (byte)random.Next()];
                break;
            default:
                input = [.. seed];
                break;
        }

        if (input.Length == 0)
        {
            return input;
        }

        int edits = 1 + (int)(random.Next() % 4);
        for (int i = 0; i < edits; i++)
        {
            input[(int)(random.Next() % (ulong)input.Length)] = (byte)random.Next();
        }

        return input;
    }

    private static bool Attempt(byte[] input, TextWriter output)
    {
        try
        {
            FuzzTarget.Run(input);
            return true;
        }
        catch (NinePProtocolException)
        {
            return true;
        }
#pragma warning disable CA1031 // The whole point of the target is to catch whatever escaped.
        catch (Exception e)
#pragma warning restore CA1031
        {
            output.WriteLine("unexpected " + e.GetType().FullName + " on " + Convert.ToHexString(input));
            output.WriteLine(e.ToString());
            return false;
        }
    }

    private static long? Number(string[] args, string prefix)
    {
        foreach (string argument in args)
        {
            if (argument.StartsWith(prefix, StringComparison.Ordinal)
                && long.TryParse(argument[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>A 64-bit xorshift generator, so that a run is reproducible from its seed alone.</summary>
internal sealed class Xorshift
{
    private ulong _state;

    /// <summary>Creates a generator.</summary>
    /// <param name="seed">The seed; it must not be zero.</param>
    public Xorshift(ulong seed) => _state = seed == 0 ? 1 : seed;

    /// <summary>The next value in the sequence.</summary>
    /// <returns>A 64-bit value.</returns>
    public ulong Next()
    {
        _state ^= _state << 13;
        _state ^= _state >> 7;
        _state ^= _state << 17;
        return _state;
    }
}
