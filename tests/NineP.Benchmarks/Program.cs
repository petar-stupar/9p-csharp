using System.Globalization;
using BenchmarkDotNet.Running;
using NineP.Benchmarks;
using NineP.Protocol;

if (args.Length > 0 && args[0] is "edge-scale" or "edge-serve")
{
    return await EdgeScale.RunAsync(args);
}

// The four benchmarks of docs/9p/ARCHITECTURE.md §9. Every mode prints the numbers that
// docs/benchmarks.md records, beside the command line that produced them.
if (args.Length == 0)
{
    await Console.Error.WriteLineAsync(
        """
        usage: dotnet run -c Release --project tests/NineP.Benchmarks -- <mode>

          codec                                    (d) Twalk and Rgetattr decode/encode, BenchmarkDotNet
          throughput [--dialect d] [--msize n]
                     [--bytes n] [--window n]      (a) sequential read and write, and (c) the server's peak RSS
          roundtrips [--dialect d] [--msize n]
                     [--count n]                   (b) walk + stat + clunk round trips
          serve [--msize n] [--length n]           the server half of (a); started by the driver
        """);
    return 2;
}

Dialect dialect = Dialect.P9_2000_L;
uint msize = 1024 * 1024;
ulong bytes = 1024UL * 1024 * 1024;
ulong length = 1024UL * 1024 * 1024;
int window = 4;
int count = 100_000;

for (int i = 1; i < args.Length - 1; i += 2)
{
    string value = args[i + 1];
    switch (args[i])
    {
        case "--dialect":
            dialect = NineP.Protocol.Negotiation.Negotiator.TryParseVersion(value, out Dialect parsed)
                ? parsed
                : throw new ArgumentException("not a 9P version string: " + value, nameof(args));
            break;
        case "--msize":
            msize = uint.Parse(value, CultureInfo.InvariantCulture);
            break;
        case "--bytes":
            bytes = ulong.Parse(value, CultureInfo.InvariantCulture);
            length = bytes;
            break;
        case "--length":
            length = ulong.Parse(value, CultureInfo.InvariantCulture);
            break;
        case "--window":
            window = int.Parse(value, CultureInfo.InvariantCulture);
            break;
        case "--count":
            count = int.Parse(value, CultureInfo.InvariantCulture);
            break;
        default:
            throw new ArgumentException("unknown option " + args[i], nameof(args));
    }
}

switch (args[0])
{
    case "codec":
        BenchmarkRunner.Run<CodecBenchmarks>();
        return 0;

    case "throughput":
        return await BenchmarkDriver.ThroughputAsync(dialect, msize, bytes, window);

    case "roundtrips":
        return await BenchmarkDriver.RoundTripsAsync(dialect, msize, count);

    case "serve":
        return await BenchmarkServer.RunAsync(msize, length);

    default:
        await Console.Error.WriteLineAsync("unknown mode " + args[0]);
        return 2;
}
