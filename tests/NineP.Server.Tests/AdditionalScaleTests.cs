#if NET10_0_OR_GREATER
using System.Diagnostics;
using NineP.Benchmarks;
using NineP.TestSupport;
using Xunit;

namespace NineP.Server.Tests;

[Collection(ConformanceCollection.Name)]
public sealed class AdditionalScaleTests
{
    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("create")]
    public async Task F10_F11_F12_BoundedCiWorkloads(string kind) => await RunAsync(kind, full: false);

    [Fact]
    public async Task F10_FullFileStreamsBeyondFourGiB()
    {
        if (Environment.GetEnvironmentVariable("NINEP_FULL_SCALE") != "1")
        {
            Assert.Skip("F10 full: local-only; set NINEP_FULL_SCALE=1 locally; the bounded version runs in ordinary CI.");
        }
        await RunAsync("file", full: true);
    }

    [Fact]
    public async Task F11_FullMillionEntryDirectory()
    {
        if (Environment.GetEnvironmentVariable("NINEP_FULL_SCALE") != "1")
        {
            Assert.Skip("F11 full: local-only; set NINEP_FULL_SCALE=1 locally; the bounded version runs in ordinary CI.");
        }
        await RunAsync("directory", full: true);
    }

    [Fact]
    public async Task F12_FullMillionMutableCreates()
    {
        if (Environment.GetEnvironmentVariable("NINEP_FULL_CREATE_SCALE") != "1")
        {
            Assert.Skip("F12 full retains one million real mutable nodes. Local opt-in: NINEP_FULL_CREATE_SCALE=1 NINEP_SCALE_MEMORY_MIB=1536 NINEP_SCALE_HEAP_LIMIT=0x40000000. CI runs 10,000 real creates with a 512 MiB process budget.");
        }
        await RunAsync("create", full: true);
    }

    private static bool IsEnabled(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { } value
        && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

    private static async Task RunAsync(string kind, bool full)
    {
        if (full && (IsEnabled("CI") || IsEnabled("GITHUB_ACTIONS")))
        {
            Assert.Skip("Full-scale workloads are local-only, even when their opt-in variables are set in CI.");
        }
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(full ? 12 : 3));
        ProcessStartInfo info = new("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        info.ArgumentList.Add(typeof(RUsage).Assembly.Location);
        info.ArgumentList.Add("edge-scale");
        info.ArgumentList.Add(kind);
        info.ArgumentList.Add(full ? "full" : "small");
        // Fix pool/thread proliferation independently of the host runner's CPU count.
        info.Environment["DOTNET_PROCESSOR_COUNT"] = "2";
        info.Environment["DOTNET_GCHeapHardLimit"] = Environment.GetEnvironmentVariable("NINEP_SCALE_HEAP_LIMIT") ?? "0x08000000";
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("scale process did not start");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            string output = await stdout;
            TestContext.Current.TestOutputHelper?.WriteLine(output);
            Assert.True(process.ExitCode == 0, output + await stderr);
            Assert.Contains("PASS " + kind, output, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
#endif
