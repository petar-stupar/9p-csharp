using System.Globalization;
using System.Reflection;
using Xunit;

namespace NineP.Repo.Tests;

/// <summary>Locates the repository this test assembly was built from.</summary>
internal static class RepoLayout
{
    private static readonly Lazy<string> RootLazy = new(FindRoot);

    /// <summary>The absolute path of the repository root.</summary>
    public static string Root => RootLazy.Value;

    /// <summary>Combines <see cref="Root"/> with repository-relative path segments.</summary>
    public static string Path(params string[] parts) =>
        System.IO.Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "docs", "9p", "protocol-reference.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "repository root not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>One parsed row of <c>docs/rule-index.md</c>.</summary>
internal sealed record RuleRow(int Number, string Source, string Rule, string Test, string Task);

/// <summary>Reads <c>docs/rule-index.md</c> as data.</summary>
internal static class RuleIndex
{
    private const string HeaderPrefix = "| # | Source | Rule | Test | Task |";

    /// <summary>The rows of the single table in the rule index, in file order.</summary>
    public static IReadOnlyList<RuleRow> Rows { get; } = Parse();

    private static List<RuleRow> Parse()
    {
        string[] lines = File.ReadAllLines(RepoLayout.Path("docs", "rule-index.md"));
        int header = Array.FindIndex(lines, l => l.StartsWith(HeaderPrefix, StringComparison.Ordinal));
        if (header < 0)
        {
            throw new InvalidOperationException("docs/rule-index.md has no rule table header");
        }

        List<RuleRow> rows = [];
        for (int i = header + 2; i < lines.Length && lines[i].StartsWith('|'); i++)
        {
            string[] cells = SplitRow(lines[i]);
            if (cells.Length != 5)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.InvariantCulture, "row {0} has {1} cells, expected 5", i + 1, cells.Length));
            }

            rows.Add(new RuleRow(
                int.Parse(cells[0], CultureInfo.InvariantCulture),
                cells[1], cells[2], cells[3], cells[4]));
        }

        return rows;
    }

    private static string[] SplitRow(string line)
    {
        string[] parts = line.Trim().Trim('|').Split('|');
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i].Trim();
        }

        return parts;
    }
}

/// <summary>Resolves <c>TypeName.MethodName</c> against the test assemblies that are built.</summary>
internal static class TestResolver
{
    private static readonly Lazy<IReadOnlyList<Assembly>> AssembliesLazy = new(Load);

    /// <summary>Every loadable <c>NineP.*</c> test assembly found under <c>tests/</c>.</summary>
    public static IReadOnlyList<Assembly> Assemblies => AssembliesLazy.Value;

    /// <summary>Finds the type by simple name, or null when no built assembly declares it.</summary>
    public static Type? FindType(string simpleName)
    {
        Type[] matches = Assemblies.SelectMany(SafeTypes)
            .Where(type => string.Equals(type.Name, simpleName, StringComparison.Ordinal)).ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidOperationException("Ambiguous indexed test type " + simpleName + ": "
                + string.Join(", ", matches.Select(type => type.FullName)));
        }
        return matches.SingleOrDefault();
    }

    internal static Assembly RequiredAssembly(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Build the indexed test assembly in the current configuration", path);
        }

        return Assembly.LoadFrom(path);
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }

    private static List<Assembly> Load()
    {
        List<Assembly> assemblies = [Assembly.GetExecutingAssembly()];
        string testsDir = RepoLayout.Path("tests");
        if (!Directory.Exists(testsDir))
        {
            return assemblies;
        }

        HashSet<string> seen = new(StringComparer.Ordinal)
        {
            Assembly.GetExecutingAssembly().GetName().Name!,
        };

        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        IEnumerable<string> candidates = Directory.GetDirectories(testsDir, "NineP.*.Tests")
            .Select(directory => System.IO.Path.Combine(directory, "bin", configuration, "net10.0",
                System.IO.Path.GetFileName(directory) + ".dll"));

        foreach (string path in candidates)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!seen.Add(name))
            {
                continue;
            }

            assemblies.Add(RequiredAssembly(path));
        }

        return assemblies;
    }
}

/// <summary>
/// Decides whether a method the rule index names is a test that actually runs. The attributes are
/// matched by name rather than by type so that a test assembly loaded from disk is judged by its
/// own copy of xunit, not by this assembly's.
/// </summary>
internal static class TestMethods
{
    private static readonly string[] TestAttributes = ["FactAttribute", "TheoryAttribute"];

    /// <summary>Null when the method is a running test; otherwise what is wrong with it.</summary>
    public static string? Verdict(MethodInfo method)
    {
        foreach (Attribute attribute in method.GetCustomAttributes())
        {
            if (!IsTestAttribute(attribute))
            {
                continue;
            }

            // Strict from task 13 (spec §9 task 1): a placeholder that is skipped does not pin
            // anything, so the rule index may not point at one.
            return attribute.GetType().GetProperty("Skip")?.GetValue(attribute) is string skip
                && !string.IsNullOrEmpty(skip)
                ? $"is skipped: {skip}"
                : null;
        }

        return "is not a [Fact] or [Theory]";
    }

    private static bool IsTestAttribute(Attribute attribute)
    {
        for (Type? type = attribute.GetType(); type is not null; type = type.BaseType)
        {
            if (Array.IndexOf(TestAttributes, type.Name) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Pins <c>docs/rule-index.md</c> to the tests and tasks it names.</summary>
public sealed class RuleIndexTests
{
    /// <summary>The table is present, well formed and numbered from 1 without gaps.</summary>
    [Fact]
    public void TableIsWellFormed()
    {
        IReadOnlyList<RuleRow> rows = RuleIndex.Rows;
        Assert.NotEmpty(rows);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(i + 1, rows[i].Number);
            Assert.NotEmpty(rows[i].Source);
            Assert.NotEmpty(rows[i].Rule);
            Assert.NotEmpty(rows[i].Test);
            Assert.NotEmpty(rows[i].Task);
        }
    }

    /// <summary>
    /// Every rule names a test; every test type that is built declares that method, and from task
    /// 13 onward the method must be a real test rather than a skipped placeholder.
    /// </summary>
    [Fact]
    public void EveryRuleHasATest()
    {
        IReadOnlyList<string> broken = RuleIndexValidator.Validate(RuleIndex.Rows, TestResolver.FindType);
        Assert.True(broken.Count == 0, string.Join(Environment.NewLine, broken));
    }

    /// <summary>No acceptance-criterion or exit-criterion row has an empty task cell.</summary>
    [Fact]
    public void EveryAcHasATask()
    {
        foreach (RuleRow row in RuleIndex.Rows)
        {
            if (!row.Source.StartsWith("AC", StringComparison.Ordinal) &&
                !row.Source.StartsWith("Exit", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string task in row.Task.Split(','))
            {
                Assert.True(
                    int.TryParse(task.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 45,
                    $"row {row.Number} ({row.Source}) has no task number");
            }
        }
    }

    /// <summary>Every numbered rule in the current reference §8 appears in the table.</summary>
    [Fact]
    public void EverySection8RuleIsMapped()
    {
        HashSet<string> mapped = RuleIndex.Rows
            .Select(r => r.Source)
            .Where(s => s.StartsWith("Ref §8.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        string reference = File.ReadAllText(RepoLayout.Path("docs", "9p", "protocol-reference.md"));
        string section = reference.Split("## 8.", StringSplitOptions.None)[1]
            .Split("## 9.", StringSplitOptions.None)[0];
        List<int> rules = [];
        foreach (string line in section.Split('\n'))
        {
            int dot = line.IndexOf('.', StringComparison.Ordinal);
            if (dot > 0 && int.TryParse(line[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out int rule))
            {
                rules.Add(rule);
                Assert.Contains(string.Format(CultureInfo.InvariantCulture, "Ref §8.{0}", rule), mapped);
            }
        }

        Assert.NotEmpty(rules);
        Assert.Equal(Enumerable.Range(1, rules.Count), rules);
    }
}
