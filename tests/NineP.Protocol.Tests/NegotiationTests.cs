using System.Globalization;
using System.Text;
using NineP.Protocol;
using NineP.Protocol.Negotiation;
using NineP.TestSupport;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>
/// Reference §5.1 in full: the 56-row oracle of seven configured dialect sets by eight client
/// version strings, plus the four rules that are easy to get wrong — every step is gated on the
/// configured set, an "unknown" reply echoes the client's msize, an msize below the floor is
/// refused with "unknown" rather than with Rerror or with the floor, and a period-separated
/// suffix strips to the base version. The oracle is regenerated from the shipped Negotiator and
/// byte-compared with <c>docs/negotiation.json</c>, so the committed file cannot drift (S-10).
/// </summary>
public sealed class NegotiationTests
{
    private const uint ClientMsize = 8192;
    private const int OracleRows = 56;

    private static readonly Limits Bounds = Limits.Default;

    /// <summary>Every (configured set, client version) pair, with the answer each must produce.</summary>
    /// <returns>One row per pair, 56 in all.</returns>
    public static TheoryData<string, string, string, string?> Rows()
    {
        TheoryData<string, string, string, string?> data = [];
        foreach (OracleRow row in Oracle.Rows)
        {
            data.Add(string.Join('+', row.Configured), row.ClientVersion, row.Version, row.DialectName);
        }

        return data;
    }

    /// <summary>All 56 rows: version, msize and dialect are what reference §5.1 requires.</summary>
    /// <param name="configured">The configured set, joined for the test name.</param>
    /// <param name="clientVersion">The version string the client sent.</param>
    /// <param name="version">The version string the server must answer with.</param>
    /// <param name="dialectName">The dialect the answer selects, or null.</param>
    [Theory]
    [MemberData(nameof(Rows))]
    public void EveryStepIsGatedOnTheConfiguredSet(
        string configured, string clientVersion, string version, string? dialectName)
    {
        IReadOnlySet<Dialect> set = Oracle.Parse(configured);
        NegotiationResult result = Negotiator.Negotiate(set, clientVersion, ClientMsize, Bounds);

        Assert.Equal(version, result.Version);
        Assert.Equal(ClientMsize, result.Msize);
        Assert.Equal(dialectName, result.Dialect is null ? null : Negotiator.VersionString(result.Dialect.Value));
        Assert.Equal(dialectName is null, result.IsUnknown);

        // A dialect outside the configured set is never answered with, however the client asks.
        if (result.Dialect is Dialect agreed)
        {
            Assert.Contains(agreed, set);
        }
    }

    /// <summary>An "unknown" reply echoes the client's msize, never the server's maximum.</summary>
    [Fact]
    public void UnknownEchoesClientMsize()
    {
        IReadOnlySet<Dialect> onlyDotL = Oracle.Parse(Constants.Version9P2000L);

        NegotiationResult refused = Negotiator.Negotiate(onlyDotL, Constants.Version9P2000, 12345, Bounds);

        Assert.True(refused.IsUnknown);
        Assert.Equal(Constants.VersionUnknown, refused.Version);
        Assert.Equal(12345u, refused.Msize);
        Assert.Null(refused.Dialect);
        Assert.True(refused.Msize < Bounds.MaxMsize);

        // The golden Rversion "unknown" vector carries 8192, the msize its client offered.
        WireVector vector = WireVectors.All.First(v => v.Str("version") == Constants.VersionUnknown);
        Assert.Equal(ClientMsize, (uint)vector.Num("msize"));
    }

    /// <summary>A server never answers with an msize larger than the client asked for.</summary>
    [Fact]
    public void MsizeIsClampedToTheClientAndToTheMaximum()
    {
        IReadOnlySet<Dialect> all = Oracle.Parse("9P2000+9P2000.u+9P2000.L");

        Assert.Equal(
            ClientMsize,
            Negotiator.Negotiate(all, Constants.Version9P2000L, ClientMsize, Bounds).Msize);
        Assert.Equal(
            Bounds.MaxMsize,
            Negotiator.Negotiate(all, Constants.Version9P2000L, Bounds.MaxMsize * 2, Bounds).Msize);
    }

    /// <summary>An msize below the floor is refused with "unknown", not with the floor.</summary>
    [Fact]
    public void MsizeBelowFloorIsUnknown()
    {
        IReadOnlySet<Dialect> all = Oracle.Parse("9P2000+9P2000.u+9P2000.L");

        NegotiationResult refused = Negotiator.Negotiate(
            all, Constants.Version9P2000L, Bounds.MinMsize - 1, Bounds);

        Assert.True(refused.IsUnknown);
        Assert.Equal(Bounds.MinMsize - 1, refused.Msize);
        Assert.Null(refused.Dialect);

        // The floor itself is served, so the rule is "below the floor", not "at or below it".
        Assert.False(Negotiator.Negotiate(all, Constants.Version9P2000L, Bounds.MinMsize, Bounds).IsUnknown);
    }

    /// <summary>A version string with a suffix strips to 9P2000 when 9P2000 is configured.</summary>
    [Fact]
    public void VersionSuffixIsStripped()
    {
        IReadOnlySet<Dialect> onlyBase = Oracle.Parse(Constants.Version9P2000);

        foreach (string version in new[] { "9P2000.x", "9P2000.L", "9P2000.u", "9P2000.anything" })
        {
            NegotiationResult result = Negotiator.Negotiate(onlyBase, version, ClientMsize, Bounds);
            Assert.Equal(Constants.Version9P2000, result.Version);
            Assert.Equal(Dialect.P9_2000, result.Dialect);
        }

        // The strip only reaches versions that start with 9P2000: 9P1000 and 9P3000 do not.
        foreach (string version in new[] { "9P1000", "9P3000", "XYZ", "" })
        {
            Assert.True(Negotiator.Negotiate(onlyBase, version, ClientMsize, Bounds).IsUnknown);
        }
    }

    /// <summary>The wire strings and their parser are each other's inverse; "unknown" is neither.</summary>
    [Fact]
    public void VersionStringsRoundTrip()
    {
        foreach (Dialect dialect in Enum.GetValues<Dialect>())
        {
            Assert.True(Negotiator.TryParseVersion(Negotiator.VersionString(dialect), out Dialect parsed));
            Assert.Equal(dialect, parsed);
        }

        Assert.False(Negotiator.TryParseVersion(Constants.VersionUnknown, out _));
        Assert.False(Negotiator.TryParseVersion("9P2000.x", out _));
    }

    /// <summary>
    /// The committed oracle is exactly what the shipped negotiator produces. The file is written
    /// only when it is absent — the first run generates it, and every run after that compares.
    /// </summary>
    [Fact]
    public void OracleMatchesCommittedFile()
    {
        string path = RepositoryPaths.Combine("docs", "negotiation.json");
        byte[] generated = new UTF8Encoding(false).GetBytes(Oracle.Render());

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, generated);
        }

        Assert.Equal(generated, File.ReadAllBytes(path));
        Assert.Equal(OracleRows, Oracle.Rows.Count);
    }
}

/// <summary>One row of the negotiation oracle: an input and the answer reference §5.1 demands.</summary>
/// <param name="Configured">The configured dialect strings, in canonical order.</param>
/// <param name="ClientVersion">The version string the client sent.</param>
/// <param name="ClientMsize">The msize the client sent.</param>
/// <param name="Version">The version string the server answers with.</param>
/// <param name="Msize">The msize the server answers with.</param>
/// <param name="DialectName">The dialect the answer selects, or null when none was agreed.</param>
internal sealed record OracleRow(
    IReadOnlyList<string> Configured,
    string ClientVersion,
    uint ClientMsize,
    string Version,
    uint Msize,
    string? DialectName);

/// <summary>
/// Builds the 56 rows of the oracle from the shipped negotiator and renders them as the JSON that
/// <c>docs/negotiation.json</c> holds. The rendering is done by hand rather than by a serialiser so
/// that the bytes are identical on every framework and platform.
/// </summary>
internal static class Oracle
{
    private const uint ClientMsize = 8192;

    private static readonly Dialect[] Order = [Dialect.P9_2000, Dialect.P9_2000_u, Dialect.P9_2000_L];

    private static readonly string[] ClientVersions =
    [
        "9P2000", "9P2000.u", "9P2000.L", "9P2000.x", "9P1000", "9P3000", "XYZ", string.Empty,
    ];

    private static readonly Lazy<IReadOnlyList<OracleRow>> RowsLazy = new(Build);

    /// <summary>Seven configured sets by eight client version strings, in a fixed order.</summary>
    public static IReadOnlyList<OracleRow> Rows => RowsLazy.Value;

    /// <summary>Parses a "+"-joined list of dialect strings back into a set.</summary>
    /// <param name="configured">The joined names, as the theory rows carry them.</param>
    /// <returns>The set.</returns>
    public static IReadOnlySet<Dialect> Parse(string configured)
    {
        HashSet<Dialect> set = [];
        foreach (string name in configured.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            set.Add(Negotiator.TryParseVersion(name, out Dialect dialect)
                ? dialect
                : throw new InvalidOperationException("not a dialect: " + name));
        }

        return set;
    }

    /// <summary>Renders the oracle as the committed JSON, with LF newlines and a final newline.</summary>
    /// <returns>The file's text.</returns>
    public static string Render()
    {
        StringBuilder json = new();
        json.Append("{\n");
        Append(json, "  \"generated_by\": \"tests/NineP.Protocol.Tests/NegotiationTests.cs\",\n");
        Append(json, "  \"reference\": \"docs/9p/protocol-reference.md section 5.1\",\n");
        Append(json, "  \"note\": \"Seven configured dialect sets by eight client version strings. ");
        Append(json, "Generated from NineP.Protocol.Negotiation.Negotiator and byte-compared by ");
        Append(json, "NegotiationTests.OracleMatchesCommittedFile.\",\n");
        Append(json, "  \"rows\": [\n");

        for (int i = 0; i < Rows.Count; i++)
        {
            Append(json, "    ");
            AppendRow(json, Rows[i]);
            Append(json, i == Rows.Count - 1 ? "\n" : ",\n");
        }

        Append(json, "  ]\n");
        Append(json, "}\n");
        return json.ToString();
    }

    private static void AppendRow(StringBuilder json, OracleRow row)
    {
        Append(json, "{ \"configured\": [");
        for (int i = 0; i < row.Configured.Count; i++)
        {
            Append(json, i == 0 ? "\"" : ", \"");
            Append(json, row.Configured[i]);
            Append(json, "\"");
        }

        Append(json, "], \"clientVersion\": \"");
        Append(json, row.ClientVersion);
        Append(json, "\", \"clientMsize\": ");
        Append(json, Number(row.ClientMsize));
        Append(json, ", \"version\": \"");
        Append(json, row.Version);
        Append(json, "\", \"msize\": ");
        Append(json, Number(row.Msize));
        Append(json, ", \"dialect\": ");
        Append(json, row.DialectName is null ? "null" : "\"" + row.DialectName + "\"");
        Append(json, " }");
    }

    private static void Append(StringBuilder json, string text) => json.Append(text);

    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);

    private static List<OracleRow> Build()
    {
        List<OracleRow> rows = [];

        foreach (IReadOnlyList<Dialect> set in Sets())
        {
            HashSet<Dialect> configured = [.. set];
            foreach (string clientVersion in ClientVersions)
            {
                NegotiationResult result =
                    Negotiator.Negotiate(configured, clientVersion, ClientMsize, Limits.Default);

                rows.Add(new OracleRow(
                    [.. set.Select(Negotiator.VersionString)],
                    clientVersion,
                    ClientMsize,
                    result.Version,
                    result.Msize,
                    result.Dialect is Dialect agreed ? Negotiator.VersionString(agreed) : null));
            }
        }

        return rows;
    }

    private static IEnumerable<IReadOnlyList<Dialect>> Sets()
    {
        // The three singletons, the three pairs and the whole set — the table reference §5.1 names.
        foreach (Dialect dialect in Order)
        {
            yield return [dialect];
        }

        for (int i = 0; i < Order.Length; i++)
        {
            for (int j = i + 1; j < Order.Length; j++)
            {
                yield return [Order[i], Order[j]];
            }
        }

        yield return Order;
    }
}
