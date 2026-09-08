using System.Reflection;
using System.Runtime.Versioning;
using Xunit;

namespace NineP.Protocol.Tests;

/// <summary>Pins that this suite really runs on both target frameworks (spec §3.2, RK-67).</summary>
public sealed class TargetFrameworkTests
{
    private static readonly string[] Configured =
        [".NETCoreApp,Version=v8.0", ".NETCoreApp,Version=v10.0"];

    /// <summary>The suite ran under one of the two frameworks the project targets.</summary>
    [Fact]
    public void RunsOnAConfiguredTargetFramework()
    {
        string framework = typeof(TargetFrameworkTests).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;

        Assert.Contains(framework, Configured);
    }
}
