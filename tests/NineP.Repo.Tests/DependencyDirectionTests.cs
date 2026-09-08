using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace NineP.Repo.Tests;

/// <summary>The <c>examples → client|server → protocol</c> layering, on both representations.</summary>
public sealed class DependencyDirectionTests
{
    private const string Protocol = "NineP.Protocol";
    private const string Client = "NineP.Client";
    private const string Server = "NineP.Server";

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> GraphLazy =
        new(ReadProjectGraph);

    /// <summary>The protocol package references no other package of this repository.</summary>
    [Fact]
    public void ProtocolReferencesNoOtherNinePProject()
    {
        Assert.Empty(ProjectReferencesOf(Protocol));
        Assert.Empty(AssemblyReferencesOf(Protocol));
    }

    /// <summary>The client declares the protocol as its only edge and links nothing else.</summary>
    [Fact]
    public void ClientReferencesOnlyTheProtocol()
    {
        Assert.Equal([Protocol], ProjectReferencesOf(Client));
        AssertLinksOnly(Client, Protocol);
    }

    /// <summary>The server declares the protocol as its only edge and links nothing else.</summary>
    [Fact]
    public void ServerReferencesOnlyTheProtocol()
    {
        Assert.Equal([Protocol], ProjectReferencesOf(Server));
        AssertLinksOnly(Server, Protocol);
    }

    /// <summary>The client and the server never reference each other, in either direction.</summary>
    [Fact]
    public void ClientAndServerNeverReferenceEachOther()
    {
        Assert.DoesNotContain(Server, ProjectReferencesOf(Client));
        Assert.DoesNotContain(Client, ProjectReferencesOf(Server));
        Assert.DoesNotContain(Server, AssemblyReferencesOf(Client));
        Assert.DoesNotContain(Client, AssemblyReferencesOf(Server));
    }

    /// <summary>No package under <c>src/</c> references an example or a test project.</summary>
    [Fact]
    public void NoPackageReferencesAnExampleOrATestProject()
    {
        foreach (string package in new[] { Protocol, Client, Server })
        {
            foreach (string referenced in ProjectReferencesOf(package))
            {
                Assert.Contains(referenced, new[] { Protocol, Client, Server });
            }
        }
    }

    /// <summary>Each example references exactly the one package the project map gives it.</summary>
    [Fact]
    public void ExamplesReferenceOnlyTheirOwnPackage()
    {
        Assert.Equal([Server], ProjectReferencesOf("NineP.JsonFs"));
        Assert.Equal([Server], ProjectReferencesOf("NineP.TodoFs"));
        Assert.Equal([Client], ProjectReferencesOf("NineP.Cli"));
    }

    /// <summary>The project graph the solution declares covers all fifteen projects.</summary>
    [Fact]
    public void EveryProjectOfTheMapIsInTheGraph()
    {
        Assert.Equal(15, GraphLazy.Value.Count);
    }

    /// <summary>The compiled assembly links no NineP package outside the allowed set. A reference
    /// the compiler elided because nothing used it is not a violation; a reference that is there
    /// and is not allowed is.</summary>
    private static void AssertLinksOnly(string package, params string[] allowed)
    {
        foreach (string linked in AssemblyReferencesOf(package))
        {
            Assert.Contains(linked, allowed);
        }
    }

    private static IReadOnlyList<string> ProjectReferencesOf(string project) =>
        GraphLazy.Value.TryGetValue(project, out IReadOnlyList<string>? edges)
            ? edges
            : throw new InvalidOperationException("no project named " + project);

    private static IReadOnlyList<string> AssemblyReferencesOf(string package)
    {
        string path = BuiltAssembly(package);
        AssemblyName[] referenced = Assembly.LoadFrom(path).GetReferencedAssemblies();

        return [.. referenced
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("NineP.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)];
    }

    private static string BuiltAssembly(string package)
    {
        string root = RepoLayout.Path("src", package, "bin");
        string? newest = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, package + ".dll", SearchOption.AllDirectories)
                .Where(p => p.Contains($"{Path.DirectorySeparatorChar}net10.0{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        return newest ?? throw new InvalidOperationException(
            string.Format(CultureInfo.InvariantCulture, "{0}.dll is not built under {1}", package, root));
    }

    private static Dictionary<string, IReadOnlyList<string>> ReadProjectGraph()
    {
        Dictionary<string, IReadOnlyList<string>> graph = [];

        foreach (string layer in new[] { "src", "examples", "tests" })
        {
            string root = RepoLayout.Path(layer);
            foreach (string file in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
            {
                graph[Path.GetFileNameWithoutExtension(file)] = ReferencesOf(file);
            }
        }

        return graph;
    }

    private static IReadOnlyList<string> ReferencesOf(string csproj) =>
        [.. XDocument.Load(csproj)
            .Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .Select(Path.GetFileNameWithoutExtension)
            .Select(n => n ?? string.Empty)
            .OrderBy(n => n, StringComparer.Ordinal)];
}
