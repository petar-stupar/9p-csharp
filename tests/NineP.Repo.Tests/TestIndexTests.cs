using System.Reflection;
using System.Text.Json;
using Xunit;

namespace NineP.Repo.Tests;

/// <summary>One obligation from the shared <c>docs/9p/fixtures/test-index.json</c>.</summary>
internal sealed record IndexedTest(string Id, string Area, string Layer, string Tier);

/// <summary>Reads the shared index and this port's map of it.</summary>
internal static class TestIndex
{
    private static readonly Lazy<IReadOnlyList<IndexedTest>> IndexLazy = new(LoadIndex);
    private static readonly Lazy<JsonElement> MapLazy = new(LoadMap);

    /// <summary>Every obligation the workspace index states.</summary>
    public static IReadOnlyList<IndexedTest> Index => IndexLazy.Value;

    /// <summary>This port's <c>docs/test-map.json</c>, as read.</summary>
    public static JsonElement Map => MapLazy.Value;

    /// <summary>The ids this port maps onto its own tests, and the method each names.</summary>
    public static IReadOnlyDictionary<string, string> Mapped =>
        Map.GetProperty("map").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);

    /// <summary>The ids this port records as deliberately not implemented, with the reason.</summary>
    public static IReadOnlyDictionary<string, string> Skipped =>
        Map.GetProperty("skipped").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);

    /// <summary>The test methods that are this port's own and owe nothing to the index.</summary>
    public static IReadOnlySet<string> Local =>
        Map.GetProperty("local").EnumerateArray()
            .Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every discovered test method of the three shared layers, index-shaped.</summary>
    public static IReadOnlySet<string> Discovered
    {
        get
        {
            HashSet<string> found = new(StringComparer.Ordinal);
            foreach (Assembly assembly in TestResolver.Assemblies)
            {
                string? name = assembly.GetName().Name;
                if (name is not ("NineP.Protocol.Tests" or "NineP.Client.Tests" or "NineP.Server.Tests"))
                {
                    continue;
                }

                foreach (Type type in assembly.GetTypes())
                {
                    foreach (MethodInfo method in type.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        if (!method.GetCustomAttributesData().Any(a =>
                            a.AttributeType.Name is "FactAttribute" or "TheoryAttribute" or "PropertyAttribute"))
                        {
                            continue;
                        }

                        found.Add(Shape(type.FullName!) + "." + method.Name);
                    }
                }
            }

            return found;
        }
    }

    /// <summary>The index's spelling of a type: <c>Layer.Suite.Class</c>.</summary>
    private static string Shape(string fullName)
    {
        string trimmed = fullName.StartsWith("NineP.", StringComparison.Ordinal) ? fullName[6..] : fullName;
        int tests = trimmed.IndexOf(".Tests.", StringComparison.Ordinal);
        return tests < 0 ? trimmed : trimmed[..tests] + trimmed[(tests + 6)..];
    }

    private static IReadOnlyList<IndexedTest> LoadIndex()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(RepoLayout.Path("docs", "9p", "fixtures", "test-index.json")));
        return [.. document.RootElement.GetProperty("tests").EnumerateArray().Select(e => new IndexedTest(
            e.GetProperty("id").GetString()!,
            e.GetProperty("area").GetString()!,
            e.GetProperty("layer").GetString()!,
            e.GetProperty("tier").GetString()!))];
    }

    private static JsonElement LoadMap()
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(RepoLayout.Path("docs", "test-map.json")));
        return document.RootElement.Clone();
    }
}

/// <summary>
/// Pins this port to the shared obligation list of
/// <c>docs/9p/fixtures/test-index.json</c>. The workspace architecture makes the C# port the
/// reference implementation, and its suite the second authority after the specification itself,
/// so an obligation that exists here must exist in every port and an obligation stated there must
/// be discharged here. These cases are what stop the two drifting apart.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class TestIndexTests
{
    private static readonly string[] Tiers = ["required", "recommended"];

    /// <summary>Ids are well formed, unique, and carry a known tier.</summary>
    [Fact]
    public void IndexIsWellFormed()
    {
        IReadOnlyList<IndexedTest> index = TestIndex.Index;
        Assert.NotEmpty(index);

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> broken = [];
        foreach (IndexedTest test in index)
        {
            if (!seen.Add(test.Id))
            {
                broken.Add($"{test.Id}: duplicate id");
            }

            if (!test.Id.StartsWith(test.Area + "/", StringComparison.Ordinal))
            {
                broken.Add($"{test.Id}: id does not open with its area '{test.Area}'");
            }

            if (test.Id.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not ('-' or '/')))
            {
                broken.Add($"{test.Id}: an id is lower-case ascii, digits, '-' and one '/'");
            }

            if (!Tiers.Contains(test.Tier, StringComparer.Ordinal))
            {
                broken.Add($"{test.Id}: unknown tier '{test.Tier}'");
            }

            if (test.Layer is not ("protocol" or "client" or "server"))
            {
                broken.Add($"{test.Id}: unknown layer '{test.Layer}'");
            }
        }

        Assert.True(broken.Count == 0, string.Join(Environment.NewLine, broken));
    }

    /// <summary>
    /// Every obligation is discharged here or recorded as skipped with a reason. A `required`
    /// obligation may not be skipped at all: that is what the tier means.
    /// </summary>
    [Fact]
    public void EveryObligationIsDischargedOrRecorded()
    {
        IReadOnlyDictionary<string, string> mapped = TestIndex.Mapped;
        IReadOnlyDictionary<string, string> skipped = TestIndex.Skipped;
        List<string> missing = [];

        foreach (IndexedTest test in TestIndex.Index)
        {
            if (mapped.ContainsKey(test.Id))
            {
                continue;
            }

            if (test.Tier == "required")
            {
                missing.Add($"{test.Id}: required, and this port maps no test to it");
            }
            else if (!skipped.TryGetValue(test.Id, out string? why) || string.IsNullOrWhiteSpace(why))
            {
                missing.Add($"{test.Id}: recommended and unmapped, with no reason in \"skipped\"");
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    /// <summary>An id may not be both mapped and skipped, and a skip must name a real obligation.</summary>
    [Fact]
    public void SkipsNameRealUnmappedObligations()
    {
        HashSet<string> ids = [.. TestIndex.Index.Select(t => t.Id)];
        IReadOnlyDictionary<string, string> mapped = TestIndex.Mapped;
        List<string> broken = [];

        foreach ((string id, string _) in TestIndex.Skipped)
        {
            if (!ids.Contains(id))
            {
                broken.Add($"{id}: skipped, but the index has no such obligation");
            }

            if (mapped.ContainsKey(id))
            {
                broken.Add($"{id}: both mapped and skipped");
            }
        }

        Assert.True(broken.Count == 0, string.Join(Environment.NewLine, broken));
    }

    /// <summary>Every mapped id names a test method that really is built here.</summary>
    [Fact]
    public void EveryMappedTestExists()
    {
        IReadOnlySet<string> discovered = TestIndex.Discovered;
        HashSet<string> ids = [.. TestIndex.Index.Select(t => t.Id)];
        List<string> broken = [];

        foreach ((string id, string method) in TestIndex.Mapped)
        {
            if (!ids.Contains(id))
            {
                broken.Add($"{id}: mapped here, but the shared index has no such obligation");
            }

            if (!discovered.Contains(method))
            {
                broken.Add($"{id}: names {method}, which no built test assembly declares");
            }
        }

        Assert.True(broken.Count == 0, string.Join(Environment.NewLine, broken));
    }

    /// <summary>
    /// The other direction, and the one that makes the index grow: a test written here is either
    /// an obligation every port owes or is deliberately recorded as this port's own. A new test
    /// fails this case until someone decides which it is.
    /// </summary>
    [Fact]
    public void EveryTestIsAnObligationOrDeclaredLocal()
    {
        HashSet<string> accounted = [.. TestIndex.Mapped.Values, .. TestIndex.Local];
        List<string> loose = [.. TestIndex.Discovered.Where(m => !accounted.Contains(m)).Order(StringComparer.Ordinal)];

        Assert.True(
            loose.Count == 0,
            "these tests are neither mapped to a shared obligation nor declared local in "
            + "docs/test-map.json; add an entry to docs/9p/fixtures/test-index.json and map it, "
            + "or record it as local:" + Environment.NewLine + string.Join(Environment.NewLine, loose));
    }

    /// <summary>A method declared local is really here, and is not also mapped.</summary>
    [Fact]
    public void LocalTestsExistAndAreNotAlsoMapped()
    {
        IReadOnlySet<string> discovered = TestIndex.Discovered;
        HashSet<string> mapped = [.. TestIndex.Mapped.Values];
        List<string> broken = [];

        foreach (string method in TestIndex.Local)
        {
            if (!discovered.Contains(method))
            {
                broken.Add($"{method}: declared local, but no built test assembly declares it");
            }

            if (mapped.Contains(method))
            {
                broken.Add($"{method}: declared local and also mapped to an obligation");
            }
        }

        Assert.True(broken.Count == 0, string.Join(Environment.NewLine, broken));
    }
}
