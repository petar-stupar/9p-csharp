using System.Globalization;

namespace NineP.Conformance;

/// <summary>Locates the repository and the example assemblies this run drives.</summary>
internal static class RepoLayout
{
    private static readonly Lazy<string> RootLazy = new(FindRoot);

    /// <summary>The absolute path of the repository root.</summary>
    public static string Root => RootLazy.Value;

    /// <summary>Combines <see cref="Root"/> with repository-relative segments.</summary>
    /// <param name="parts">The segments.</param>
    /// <returns>The absolute path.</returns>
    public static string Path(params string[] parts) =>
        System.IO.Path.Combine([Root, .. parts]);

    /// <summary>The built assembly of one example, in this run's own configuration.</summary>
    /// <param name="project">The project directory under <c>examples/</c>.</param>
    /// <param name="assembly">The assembly name the project produces.</param>
    /// <returns>The absolute path of the built assembly.</returns>
    public static string BuiltExample(string project, string assembly)
    {
        string path = Path("examples", project, "bin", Configuration, "net10.0", assembly + ".dll");

        return File.Exists(path)
            ? path
            : throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} is not built; run dotnet build first ({1})",
                assembly,
                path));
    }

    private static string Configuration =>
        AppContext.BaseDirectory.Contains(
            System.IO.Path.DirectorySeparatorChar + "Release" + System.IO.Path.DirectorySeparatorChar,
            StringComparison.Ordinal)
            ? "Release"
            : "Debug";

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "docs", "9p", "protocol-reference.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }
}
