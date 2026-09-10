using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace NineP.Repo.Tests;

/// <summary>Suite membership is a property of executable tests, not helper class names.</summary>
public sealed class TestSuiteHygieneTests
{
    private static readonly string[] Layers = ["Protocol", "Client", "Server"];
    private static readonly string[] Suites = ["Conformance", "Robustness", "Regression", "Chaos", "StateMachine", "Security", "Compat"];

    [Fact]
    public void EveryTestClassCarriesOneSuiteTrait()
    {
        foreach (Type type in TestClasses())
        {
            string suite = Assert.Single(Categories(type.CustomAttributes));
            Assert.Contains(suite, Suites);
            foreach (MethodInfo method in type.GetMethods())
            {
                Assert.Empty(Categories(method.CustomAttributes));
            }
        }
    }

    [Fact]
    public void SuiteFolderNamespaceAndTraitAgree()
    {
        foreach (Type type in TestClasses())
        {
            string suite = Assert.Single(Categories(type.CustomAttributes));
            string project = type.Assembly.GetName().Name!;
            Assert.Equal(project + "." + suite, type.Namespace);
            string path = Assert.Single(Directory.GetFiles(RepoLayout.Path("tests", project, suite), "*.cs"),
                file => Regex.IsMatch(File.ReadAllText(file), @"\bclass\s+" + Regex.Escape(type.Name) + @"(?=[\s:(])", RegexOptions.CultureInvariant));
            Assert.Contains("namespace " + type.Namespace + ";", File.ReadAllText(path), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoTestClassLivesOutsideASuiteFolder()
    {
        foreach (Type type in TestClasses())
        {
            Assert.False(File.Exists(RepoLayout.Path("tests", type.Assembly.GetName().Name!, type.Name + ".cs")), type.FullName);
        }
    }

    [Fact]
    public void EverySuiteHasAtLeastOneTestPerLayerItClaims()
    {
        // Protocol has no network-fault integration tests; those use the client/server layers.
        foreach (string layer in Layers)
        {
            IEnumerable<string> expected = Suites.Where(s => layer != "Protocol" || s != "Chaos");
            foreach (string suite in expected)
            {
                Assert.Contains(TestClasses(), t => t.Namespace == $"NineP.{layer}.Tests.{suite}");
            }
        }
    }

    private static IEnumerable<Type> TestClasses() => TestResolver.Assemblies
        .Where(a => Layers.Any(l => a.GetName().Name == $"NineP.{l}.Tests"))
        .SelectMany(a => a.GetTypes())
        .Where(t => t.GetMethods().Any(m => m.CustomAttributes.Any(a => a.AttributeType.Name is "FactAttribute" or "TheoryAttribute")));

    private static IEnumerable<string> Categories(IEnumerable<CustomAttributeData> attributes) => attributes
        .Where(a => a.AttributeType.Name == "TraitAttribute" && (string?)a.ConstructorArguments[0].Value == "Category")
        .Select(a => (string)a.ConstructorArguments[1].Value!);
}
