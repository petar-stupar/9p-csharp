using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using Xunit;

namespace NineP.Repo.Tests;

/// <summary>
/// Exit criterion 1 and AC-csharp-1: the three packages build from this checkout with the
/// ecosystem's standard command, and what they produce installs into a project that has never seen
/// this repository and runs the README's example out of it. Both tests share one
/// <c>dotnet pack</c>: it takes the better part of a minute and there is no reason to do it twice.
/// </summary>
[Collection(PackagingCollection.Name)]
public sealed class PackagingTests
{
    private const string Version = "0.1.0";

    private static readonly string[] Packages = ["NineP.Protocol", "NineP.Client", "NineP.Server"];

    private static readonly Lazy<Task<string>> Artifacts = new(PackAsync);

    /// <summary>The six package files exist, and each carries what a consumer needs.</summary>
    [Fact]
    public async Task ThreePackagesPack()
    {
        string artifacts = await Artifacts.Value;

        foreach (string package in Packages)
        {
            string nupkg = Path.Combine(artifacts, $"{package}.{Version}.nupkg");
            string snupkg = Path.Combine(artifacts, $"{package}.{Version}.snupkg");

            Assert.True(File.Exists(nupkg), nupkg + " was not produced");
            Assert.True(File.Exists(snupkg), snupkg + " was not produced");

            using ZipArchive archive = await ZipFile.OpenReadAsync(nupkg, TestContext.Current.CancellationToken);
            HashSet<string> entries = [.. archive.Entries.Select(entry => entry.FullName)];

            Assert.Contains("README.md", entries);
            Assert.Contains("LICENSE", entries);

            foreach (string framework in new[] { "net8.0", "net10.0" })
            {
                Assert.Contains($"lib/{framework}/{package}.dll", entries);
                Assert.Contains($"lib/{framework}/{package}.xml", entries);
            }

            // SourceLink without the SourceLink package: the SDK writes the repository element
            // from PublishRepositoryUrl plus the RepositoryUrl this repository states (§3.4).
            using StreamReader nuspec = new(
                await archive.GetEntry($"{package}.nuspec")!.OpenAsync(TestContext.Current.CancellationToken));
            string manifest = await nuspec.ReadToEndAsync(TestContext.Current.CancellationToken);

            Assert.Contains("<repository ", manifest, StringComparison.Ordinal);
            Assert.Contains("url=\"https://github.com/petar-stupar/9p-csharp.git\"", manifest, StringComparison.Ordinal);
            Assert.Contains("<license type=\"expression\">MIT</license>", manifest, StringComparison.Ordinal);
            Assert.Contains($"<version>{Version}</version>", manifest, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// AC-csharp-1: a throwaway console project restores the packed packages from a local feed,
    /// runs the program the README carries between its <c>ci:snippet</c> markers, and produces the
    /// output the README says it produces. This is CI step 8, run here. The restore gets a packages
    /// folder of its own: NuGet serves any version already extracted under <c>~/.nuget/packages</c>
    /// without consulting a source, so with the global folder a stale <c>0.1.0</c> from an earlier
    /// run would stand in for what was just packed and the README would be compiled against the
    /// wrong surface (IR-4 found exactly that).
    /// </summary>
    [Fact]
    public async Task ScratchInstallRunsReadmeExamples()
    {
        string artifacts = await Artifacts.Value;
        string readme = (await File.ReadAllTextAsync(
                RepoLayout.Path("README.md"), TestContext.Current.CancellationToken))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        string program = Block(readme, "ci:snippet", "csharp");
        string expected = Block(readme, "ci:expected", "text");

        string work = Path.Combine(Path.GetTempPath(), "ninep-scratch-" + Guid.NewGuid().ToString("N"));
        string packages = Path.Combine(work, "packages");
        Directory.CreateDirectory(packages);

        try
        {
            await RunAsync("dotnet", ["new", "console", "-o", work, "-f", "net10.0"], work, packages);
            await File.WriteAllTextAsync(
                Path.Combine(work, "Program.cs"), program, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(work, "NuGet.config"), Feed(artifacts), TestContext.Current.CancellationToken);

            foreach (string package in new[] { "NineP.Client", "NineP.Server" })
            {
                await RunAsync("dotnet", ["add", "package", package, "--version", Version], work, packages);
            }

            string output = await RunAsync("dotnet", ["run"], work, packages);

            Assert.Equal(expected, output.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>A feed with nothing but the local artifacts, so the run proves the packages.</summary>
    private static string Feed(string artifacts) =>
        string.Format(
            CultureInfo.InvariantCulture,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="ninep-local" value="{0}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """,
            artifacts);

    /// <summary>
    /// Packs the three publishable projects. The demonstrating command of §14.1 is
    /// <c>dotnet pack -c Release -o artifacts</c> over the solution; the three projects are packed
    /// one at a time here because packing the solution builds the test projects too, and rewriting
    /// a test assembly's output while its test host is running it is a race this suite does not
    /// need. The graph each of them builds is the same one the solution-wide command builds.
    /// </summary>
    /// <returns>The directory the six package files were written to.</returns>
    private static async Task<string> PackAsync()
    {
        string artifacts = Path.Combine(Path.GetTempPath(), "ninep-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifacts);

        foreach (string package in Packages)
        {
            await RunAsync(
                "dotnet",
                ["pack", Path.Combine("src", package), "-c", "Release", "-o", artifacts],
                RepoLayout.Root);
        }

        return artifacts;
    }

    private static async Task<string> RunAsync(
        string program, string[] arguments, string workingDirectory, string? packagesFolder = null)
    {
        ProcessStartInfo info = new(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };

        if (packagesFolder is not null)
        {
            info.Environment["NUGET_PACKAGES"] = packagesFolder;
        }

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException(program + " did not start");

        Task<string> errors = process.StandardError.ReadToEndAsync();
        string output = await process.StandardOutput.ReadToEndAsync();
        string complaints = await errors;
        await process.WaitForExitAsync();

        Assert.True(
            process.ExitCode == 0,
            $"{program} {string.Join(' ', arguments)} exited {process.ExitCode}{Environment.NewLine}{output}{complaints}");

        return output;
    }

    /// <summary>The fenced block that follows a marker comment in the README, without its fences.</summary>
    private static string Block(string readme, string marker, string language)
    {
        int at = readme.IndexOf($"<!-- {marker} -->", StringComparison.Ordinal);
        Assert.True(at >= 0, $"README.md has no <!-- {marker} --> marker");

        int open = readme.IndexOf("```" + language + "\n", at, StringComparison.Ordinal);
        Assert.True(open >= 0, $"no ```{language} block follows <!-- {marker} -->");

        int start = open + language.Length + 4;
        int close = readme.IndexOf("\n```", start, StringComparison.Ordinal);
        Assert.True(close >= 0, $"the ```{language} block after <!-- {marker} --> is not closed");

        return readme[start..(close + 1)];
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the run is not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }
}

/// <summary>
/// Packing and installing owns the machine while it lasts: it builds the whole solution in Release
/// and then restores and builds a second project on top of it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PackagingCollection
{
    /// <summary>The collection name the packaging suite names.</summary>
    public const string Name = "packaging";
}
