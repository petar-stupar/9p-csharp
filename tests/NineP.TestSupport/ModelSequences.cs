using System.Globalization;

namespace NineP.TestSupport;

/// <summary>Reproducible bounded command generation and deletion shrinking for async wire models.</summary>
public static class ModelSequences
{
    public static uint Seed(uint fallback) => uint.TryParse(Environment.GetEnvironmentVariable("NINEP_MODEL_SEED"),
        NumberStyles.Integer, CultureInfo.InvariantCulture, out uint seed) && seed != 0 ? seed : fallback;

    public static uint[] Generate(uint seed, int operations, IReadOnlyList<uint> prefix)
    {
        if (Environment.GetEnvironmentVariable("NINEP_MODEL_COMMANDS") is { Length: > 0 } replay)
        {
            return replay.Split(',').Select(value => uint.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        }
        int count = int.TryParse(Environment.GetEnvironmentVariable("NINEP_MODEL_STEPS"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int configured) ? Math.Clamp(configured, prefix.Count, 4096) : 96;
        List<uint> commands = [.. prefix];
        uint state = seed;
        while (commands.Count < count)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            commands.Add((state & 0xFFFFFF00) | (state % (uint)operations));
        }
        return [.. commands];
    }

    public static async Task VerifyAsync(uint seed, IReadOnlyList<uint> commands,
        Func<IReadOnlyList<uint>, Task> execute, CancellationToken cancellationToken)
    {
        Exception? failure = await AttemptAsync(commands, execute, cancellationToken);
        if (failure is null)
        {
            return;
        }
        List<uint> smallest = [.. commands];
        int attempts = 0;
        for (int size = Math.Max(1, smallest.Count / 2); size >= 1 && attempts < 64; size /= 2)
        {
            for (int at = 0; at + size <= smallest.Count && attempts < 64;)
            {
                List<uint> candidate = [.. smallest.Take(at), .. smallest.Skip(at + size)];
                attempts++;
                Exception? reduced = await AttemptAsync(candidate, execute, cancellationToken);
                if (reduced is null)
                {
                    at += size;
                }
                else
                {
                    smallest = candidate;
                    failure = reduced;
                }
            }
        }
        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
            "Model seed {0}; minimized commands [{1}] after {2} shrink attempts. Replay exactly with NINEP_MODEL_COMMANDS=<that list>; the seed is then unused.",
            seed, string.Join(",", smallest), attempts), failure);
    }

    private static async Task<Exception?> AttemptAsync(IReadOnlyList<uint> commands,
        Func<IReadOnlyList<uint>, Task> execute, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await execute(commands);
            return null;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return failure;
        }
    }
}
