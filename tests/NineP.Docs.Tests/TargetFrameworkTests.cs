using System.Reflection;
using System.Runtime.Versioning;
using Xunit;

namespace NineP.Docs.Tests;

/// <summary>Pins the framework the documentation suite runs under (spec §3.2).</summary>
public sealed class TargetFrameworkTests
{
    /// <summary>The documentation suite is net10.0 only; it drives the examples as shipped.</summary>
    [Fact]
    public void RunsOnNet10()
    {
        string framework = typeof(TargetFrameworkTests).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;

        Assert.Equal(".NETCoreApp,Version=v10.0", framework);
    }
}
