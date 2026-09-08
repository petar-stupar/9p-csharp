using System.Reflection;
using System.Xml.Linq;
using NineP.Client;
using NineP.Protocol;
using NineP.Server;
using Xunit;

namespace NineP.Docs.Tests;

/// <summary>
/// Exit criterion 8 (ARCHITECTURE.md §10.8): every document on the list exists, is not a stub, and
/// is reachable from the README. A documentation set is a deliverable like any other, so it is
/// checked like one.
/// </summary>
public sealed class DocsTests
{
    /// <summary>
    /// The documents §10.8 requires: the path, the smallest size each may plausibly be, and the
    /// spec §9 task that delivers it. Every listed task has landed, so every document is mandatory.
    /// </summary>
    private static readonly (string Path, int MinimumBytes, int Task)[] Required =
    [
        ("README.md", 4000, 42),
        ("CHANGELOG.md", 2000, 42),
        ("docs/protocol.md", 2000, 42),
        ("docs/server.md", 2000, 32),
        ("docs/client.md", 2000, 42),
        ("docs/transports.md", 2000, 42),
        ("docs/auth.md", 2000, 42),
        ("docs/examples.md", 2000, 34),
        ("docs/benchmarks.md", 2000, 41),
        ("docs/security.md", 2000, 42),
        ("docs/interop.md", 500, 43),
        ("docs/api.md", 4000, 45),
    ];

    /// <summary>The last task whose documents must already be on disk.</summary>
    private const int DeliveredThrough = 45;

    /// <summary>Every document of the §10.8 list is present and has content.</summary>
    [Fact]
    public void EveryRequiredDocExists()
    {
        string root = ReadmeSnippetTests.RepositoryRoot();
        List<string> missing = [];

        foreach ((string path, int minimum, int task) in Required)
        {
            string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(full))
            {
                if (task <= DeliveredThrough)
                {
                    missing.Add(path + " does not exist");
                }

                continue;
            }

            long size = new FileInfo(full).Length;
            if (size < minimum)
            {
                missing.Add($"{path} is {size} bytes, which is below the {minimum} a real document needs");
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void F11c_DirectoryConvenienceMethodDocumentsMaterialization()
    {
        string client = File.ReadAllText(Path.Combine(ReadmeSnippetTests.RepositoryRoot(), "docs", "client.md"));
        Assert.Contains("NinePSession.ReadDirAsync", client, StringComparison.Ordinal);
        Assert.Contains("materializes every entry", client, StringComparison.Ordinal);
        Assert.Contains("NinePFid.ReadDirAsync", client, StringComparison.Ordinal);
        Assert.Contains("streams pages", client, StringComparison.Ordinal);
    }

    /// <summary>The generated API reference has been built and committed.</summary>
    [Fact]
    public void GeneratedApiReferenceIsPresent()
    {
        string generated = Path.Combine(ReadmeSnippetTests.RepositoryRoot(), "docs", "api");

        Assert.True(Directory.Exists(generated), "docs/api does not exist; run dotnet docfx build");

        string[] pages = Directory.GetFiles(generated, "*.yml");
        Assert.True(pages.Length > 100, $"docs/api holds {pages.Length} pages, which is too few to be the surface");

        foreach (string assembly in new[] { "NineP.Protocol", "NineP.Client", "NineP.Server" })
        {
            Assert.Contains(
                pages,
                page => Path.GetFileName(page).StartsWith(assembly + ".", StringComparison.Ordinal));
        }
    }

    /// <summary>Generated pages contain exactly today's exported types and namespaces.</summary>
    [Fact]
    public void GeneratedApiTypesMatchCurrentAssemblies()
    {
        Assembly[] assemblies = [typeof(Qid).Assembly, typeof(NinePClient).Assembly, typeof(IHandler).Assembly];
        HashSet<string> expected = assemblies.SelectMany(assembly => assembly.GetExportedTypes())
            .Select(type => "T:" + type.FullName!.Replace('+', '.')).ToHashSet(StringComparer.Ordinal);
        foreach (string space in assemblies.SelectMany(assembly => assembly.GetExportedTypes())
            .Select(type => type.Namespace!).Distinct(StringComparer.Ordinal))
        {
            expected.Add("N:" + space);
        }

        string generated = Path.Combine(ReadmeSnippetTests.RepositoryRoot(), "docs", "api");
        HashSet<string> actual = Directory.GetFiles(generated, "*.yml")
            .SelectMany(File.ReadLines)
            .Where(line => line.TrimStart().StartsWith("commentId: T:", StringComparison.Ordinal)
                || line.TrimStart().StartsWith("commentId: N:", StringComparison.Ordinal))
            .Select(line => line.Trim()["commentId: ".Length..]).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    /// <summary>The maintained dependency decision records the currently selected logging packages.</summary>
    [Fact]
    public void DocumentedLoggingDependenciesMatchCentralPins()
    {
        string root = ReadmeSnippetTests.RepositoryRoot();
        XDocument pins = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        string architecture = File.ReadAllText(Path.Combine(root, "ARCHITECTURE.md"));
        foreach (string name in new[] { "NLog", "NLog.Extensions.Logging", "Microsoft.Extensions.Logging.Abstractions" })
        {
            XElement pin = pins.Descendants("PackageVersion").Single(element => (string?)element.Attribute("Include") == name);
            string version = (string)pin.Attribute("Version")!;
            Assert.Contains(name, architecture, StringComparison.Ordinal);
            Assert.Contains(version, architecture, StringComparison.Ordinal);
        }
    }

    /// <summary>The README links every document of the list, so the set is navigable.</summary>
    [Fact]
    public void ReadmeLinksEveryDocument()
    {
        string readme = File.ReadAllText(
            Path.Combine(ReadmeSnippetTests.RepositoryRoot(), "README.md"));

        foreach ((string path, _, _) in Required)
        {
            if (path == "README.md")
            {
                continue;
            }

            Assert.Contains("(" + path + ")", readme, StringComparison.Ordinal);
        }
    }
}
