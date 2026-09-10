using System.Reflection;
using Xunit;

namespace NineP.Repo.Tests;

/// <summary>Validates every executable obligation and explicitly identified workflow gate.</summary>
internal static class RuleIndexValidator
{
    public static IReadOnlyList<string> Validate(IEnumerable<RuleRow> rows, Func<string, Type?> resolve)
    {
        List<string> failures = [];
        foreach (RuleRow row in rows)
        {
            string? workflow = row.Source switch
            {
                "AC-f" or "Exit 7" => "workflow:review",
                "AC-csharp-2" or "Exit 9" => "workflow:ci",
                _ => null,
            };
            if (workflow is not null && row.Test == workflow)
            {
                continue;
            }

            string prefix = $"row {row.Number} ({row.Source}, task {row.Task}): ";
            string[] parts = row.Test.Trim('`').Split('.');
            if (parts.Length != 2)
            {
                failures.Add(prefix + "must name an executable Type.Method test");
                continue;
            }

            Type? type = resolve(parts[0]);
            MethodInfo? method = type?.GetMethod(parts[1], BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            if (method is null)
            {
                failures.Add(prefix + row.Test + " does not resolve in the current build");
                continue;
            }

            if (TestMethods.Verdict(method) is { } complaint)
            {
                failures.Add(prefix + row.Test + " " + complaint);
            }
        }

        return failures;
    }
}

/// <summary>Missing obligations must fail even while all other rows resolve.</summary>
public sealed class RuleIndexValidatorTests
{
    [Fact]
    public void AmbiguityFailsOnlyWhenTheIndexReferencesTheName()
    {
        Assert.NotNull(TestResolver.FindType(nameof(RuleIndexTests)));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => TestResolver.FindType("TargetFrameworkTests"));
        Assert.Contains("Ambiguous indexed test type", error.Message, StringComparison.Ordinal);
        Assert.Empty(RuleIndexValidator.Validate(RuleIndex.Rows, TestResolver.FindType));
    }

    [Theory]
    [InlineData("MissingType.Present")]
    [InlineData("Fixture.MissingMethod")]
    [InlineData("Fixture.Skipped")]
    [InlineData("Fixture.NotATest")]
    [InlineData("—")]
    [InlineData("workflow:ci")]
    public void MissingOrNonExecutableMandatoryTestsFail(string test)
    {
        RuleRow[] rows = [new(1, "Ref §8.1", "valid", "Fixture.Present", "1"), new(2, "Ref §8.2", "mandatory", test, "2")];
        IReadOnlyList<string> failures = RuleIndexValidator.Validate(rows, Resolve);
        Assert.Single(failures);
        Assert.Contains("row 2", failures[0], StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteIndexAndExplicitWorkflowGatesPass()
    {
        RuleRow[] rows = [new(1, "Ref §8.1", "valid", "Fixture.Present", "1"), new(2, "Exit 9", "CI", "workflow:ci", "3")];
        Assert.Empty(RuleIndexValidator.Validate(rows, Resolve));
    }

    [Fact]
    public void MissingCurrentAssemblyCannotFallBackToAnotherBuild() =>
        Assert.Throws<FileNotFoundException>(() => TestResolver.RequiredAssembly(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll")));

    private static Type? Resolve(string name) => name == "Fixture" ? typeof(Fixture) : null;

    private static class Fixture
    {
        [FixtureAttributes.Fact]
        public static void Present() { }

        [FixtureAttributes.Fact(Skip = "fixture")]
        public static void Skipped() { }

        public static void NotATest() { }
    }

    private static class FixtureAttributes
    {
        [AttributeUsage(AttributeTargets.Method)]
        public sealed class FactAttribute : Attribute
        {
            public string? Skip { get; set; }
        }
    }
}
