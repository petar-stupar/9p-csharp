using System.Globalization;
using NineP.Protocol;

namespace NineP.Conformance;

/// <summary>What one <c>ninep</c> invocation produced.</summary>
/// <param name="ExitCode">The exit code, frozen by the conformance fixture.</param>
/// <param name="Stdout">Standard output, as raw bytes.</param>
/// <param name="Stderr">Standard error, as text.</param>
internal readonly record struct CliResult(int ExitCode, byte[] Stdout, string Stderr)
{
    /// <summary>Standard output decoded as UTF-8.</summary>
    public string Text => ConformanceText.Utf8.GetString(Stdout);
}

/// <summary>One dialect, one transport, one running <c>jsonfs</c>, and a way to drive it.</summary>
internal abstract class ConformanceTarget : IAsyncDisposable
{
    /// <summary>Creates a target for one dialect.</summary>
    /// <param name="dialect">The dialect this target negotiates.</param>
    /// <param name="transport">The transport's scheme, for the report.</param>
    protected ConformanceTarget(Dialect dialect, string transport)
    {
        Dialect = dialect;
        Transport = transport;
    }

    /// <summary>The dialect this target negotiates.</summary>
    public Dialect Dialect { get; }

    /// <summary>The transport's scheme.</summary>
    public string Transport { get; }

    /// <summary>How this combination is named in the report.</summary>
    public string Name => string.Format(
        CultureInfo.InvariantCulture,
        "{0} over {1}",
        NineP.Protocol.Negotiation.Negotiator.VersionString(Dialect),
        Transport);

    /// <summary>Runs one cli command against this target.</summary>
    /// <param name="arguments">The command and its arguments, plus any cli flags.</param>
    /// <param name="stdin">Bytes the command reads from standard input.</param>
    /// <returns>The exit code and the two streams.</returns>
    public abstract Task<CliResult> RunAsync(IReadOnlyList<string> arguments, byte[]? stdin = null);

    /// <summary>Stops the server behind this target.</summary>
    /// <returns>A task that completes when it has stopped.</returns>
    public abstract ValueTask DisposeAsync();
}
