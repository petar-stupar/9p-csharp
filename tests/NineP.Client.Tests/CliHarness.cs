#if NET10_0_OR_GREATER
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Xunit;
using NineP.TestSupport;

namespace NineP.Client.Tests;

/// <summary>
/// The cli suites share one collection so that they run one at a time. Each test spawns two
/// processes and waits on their pipes; running twenty of those at once makes the suite measure
/// the machine's scheduler rather than the cli.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliCollection
{
    /// <summary>The collection name the cli suites name.</summary>
    public const string Name = "cli processes";
}

/// <summary>What one <c>ninep</c> invocation produced.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="Stdout">Standard output, as raw bytes; the formats are byte-compared.</param>
/// <param name="Stderr">Standard error, as text.</param>
internal readonly record struct CliRun(int ExitCode, byte[] Stdout, string Stderr)
{
    /// <summary>Standard output decoded as UTF-8, for the assertions that read as text.</summary>
    public string StdoutText => new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(Stdout);

    /// <summary>
    /// Asserts the exit code and reports what the process said when it disagrees. An exit code on
    /// its own says nothing about why, and these tests run a process rather than a method.
    /// </summary>
    /// <param name="exitCode">The code the run must have produced.</param>
    /// <returns>The run, so an assertion can continue from it.</returns>
    public CliRun Expect(int exitCode)
    {
        Assert.True(
            ExitCode == exitCode,
            string.Format(
                CultureInfo.InvariantCulture,
                "expected exit {0}, got {1}; stderr: {2}",
                exitCode,
                ExitCode,
                Stderr));

        return this;
    }
}

/// <summary>
/// Runs the built <c>jsonfs</c> and the built <c>ninep</c> as processes. The cli's output formats
/// are frozen by <c>docs/9p/fixtures/conformance.md</c>, and a format is only frozen if it is
/// checked where a user would see it — on the process's own standard output.
/// </summary>
internal sealed class CliHarness : IAsyncDisposable
{
    private readonly Process _server;

    private CliHarness(Process server, string address, string documentPath, string? certificatePath)
    {
        _server = server;
        Address = address;
        DocumentPath = documentPath;
        CertificatePath = certificatePath;
    }

    /// <summary>The address the server bound, with the port the kernel chose.</summary>
    public string Address { get; }

    /// <summary>The document the server is serving; a copy, so a test may mutate it.</summary>
    public string DocumentPath { get; }

    /// <summary>The PEM holding the server's certificate, for <c>--tls-ca</c>; null over TCP.</summary>
    public string? CertificatePath { get; }

    /// <summary>
    /// The same endpoint addressed by another host name. A TLS certificate is issued to a name,
    /// so the two ways of naming one loopback listener are what separate "this root is not
    /// trusted" from "this certificate is not for this name".
    /// </summary>
    /// <param name="host">The host name to address the listener by.</param>
    /// <returns>The address with its host replaced.</returns>
    public string AddressAs(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        Uri bound = new(Address);
        return string.Format(
            CultureInfo.InvariantCulture, "{0}://{1}:{2}", bound.Scheme, host, bound.Port);
    }

    /// <summary>The repository root, found by walking up from the test assembly.</summary>
    public static string RepoRoot { get; } = FindRoot();

    /// <summary>Starts <c>jsonfs</c> over a private copy of a document and waits for its address.</summary>
    /// <param name="document">The JSON text to serve.</param>
    /// <param name="extra">Extra flags, such as <c>--writable</c>.</param>
    /// <returns>The running harness.</returns>
    public static async Task<CliHarness> StartAsync(string document, params string[] extra)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ninep-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "doc.json");
        await File.WriteAllTextAsync(path, document, TestDeadlines.Wrap(TestContext.Current.CancellationToken));

        List<string> arguments =
            ["exec", Built("NineP.JsonFs", "jsonfs"), "--listen", "tcp://127.0.0.1:0", "--file", path];
        arguments.AddRange(extra);

        Process server = Start(arguments);
        string? line = await server.StandardOutput.ReadLineAsync(TestDeadlines.Wrap(TestContext.Current.CancellationToken));

        if (line is null || !line.StartsWith("listening ", StringComparison.Ordinal))
        {
            server.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                "jsonfs did not announce an address: " + await server.StandardError.ReadToEndAsync());
        }

        return new CliHarness(server, line.Split(' ')[1], path, certificatePath: null);
    }

    /// <summary>
    /// Starts <c>jsonfs</c> behind TLS with a self-signed certificate issued to <c>localhost</c>,
    /// and returns the certificate's PEM alongside the address so that a test can choose whether
    /// to trust it and which name to address it by.
    /// </summary>
    /// <param name="document">The JSON text to serve.</param>
    /// <returns>The running harness.</returns>
    public static async Task<CliHarness> StartTlsAsync(string document)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ninep-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "doc.json");
        await File.WriteAllTextAsync(path, document, TestDeadlines.Wrap(TestContext.Current.CancellationToken));

        string certificatePath = Path.Combine(directory, "server.crt.pem");
        string keyPath = Path.Combine(directory, "server.key.pem");

        using (X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("localhost"))
        {
            await File.WriteAllTextAsync(
                certificatePath,
                certificate.ExportCertificatePem(),
                TestDeadlines.Wrap(TestContext.Current.CancellationToken));
            await File.WriteAllTextAsync(
                keyPath,
                certificate.GetRSAPrivateKey() is { } rsa
                    ? rsa.ExportPkcs8PrivateKeyPem()
                    : throw new InvalidOperationException("the generated certificate has no RSA key"),
                TestDeadlines.Wrap(TestContext.Current.CancellationToken));
        }

        Process server = Start(
        [
            "exec", Built("NineP.JsonFs", "jsonfs"),
            "--listen", "tls://127.0.0.1:0",
            "--file", path,
            "--tls-cert", certificatePath,
            "--tls-key", keyPath,
        ]);

        string? line = await server.StandardOutput.ReadLineAsync(
            TestDeadlines.Wrap(TestContext.Current.CancellationToken));

        if (line is null || !line.StartsWith("listening ", StringComparison.Ordinal))
        {
            server.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                "jsonfs did not announce an address: " + await server.StandardError.ReadToEndAsync());
        }

        return new CliHarness(server, line.Split(' ')[1], path, certificatePath);
    }

    /// <summary>Runs <c>ninep</c> against this server and captures everything it produced.</summary>
    /// <param name="arguments">The cli's arguments, after <c>--addr</c> and <c>--dialect</c>.</param>
    /// <param name="dialect">The dialect to ask for.</param>
    /// <param name="stdin">Bytes to feed the process on standard input.</param>
    /// <returns>The exit code and the two streams.</returns>
    public Task<CliRun> RunAsync(string[] arguments, string dialect = "9P2000.L", byte[]? stdin = null) =>
        RunRawAsync([.. new[] { "--addr", Address, "--dialect", dialect }, .. arguments], stdin);

    /// <summary>Runs <c>ninep</c> with exactly these arguments, for the usage-error cases.</summary>
    /// <param name="arguments">Every argument, including any flags.</param>
    /// <param name="stdin">Bytes to feed the process on standard input.</param>
    /// <param name="environment">Extra environment variables for the process.</param>
    /// <returns>The exit code and the two streams.</returns>
    public static async Task<CliRun> RunRawAsync(
        string[] arguments,
        byte[]? stdin = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using Process cli = Start(
            [.. new[] { "exec", Built("NineP.Cli", "ninep") }, .. arguments], environment);

        await using (Stream input = cli.StandardInput.BaseStream)
        {
            if (stdin is not null)
            {
                await input.WriteAsync(stdin, TestDeadlines.Wrap(TestContext.Current.CancellationToken));
            }
        }

        // Both pipes are drained before the wait: a process that fills one while the test reads
        // the other would deadlock.
        using MemoryStream stdout = new();
        Task<string> stderr = cli.StandardError.ReadToEndAsync(TestDeadlines.Wrap(TestContext.Current.CancellationToken));
        await cli.StandardOutput.BaseStream.CopyToAsync(stdout, TestDeadlines.Wrap(TestContext.Current.CancellationToken));
        string errors = await stderr;
        await cli.WaitForExitAsync(TestDeadlines.Wrap(TestContext.Current.CancellationToken));

        return new CliRun(cli.ExitCode, stdout.ToArray(), errors);
    }

    /// <summary>Stops the server and removes the document it was serving.</summary>
    /// <returns>A task that completes when everything is gone.</returns>
    public async ValueTask DisposeAsync()
    {
        if (!_server.HasExited)
        {
            _server.Kill(entireProcessTree: true);
        }

        await _server.WaitForExitAsync();
        _server.Dispose();

        try
        {
            Directory.Delete(Path.GetDirectoryName(DocumentPath)!, recursive: true);
        }
        catch (IOException)
        {
            // The temp directory outlives the test rather than failing it.
        }
    }

    /// <summary>The path of a built example's assembly, in this run's own configuration.</summary>
    /// <param name="project">The project directory under <c>examples/</c>.</param>
    /// <param name="assembly">The assembly name the project produces.</param>
    /// <returns>The absolute path of the built assembly.</returns>
    private static string Built(string project, string assembly)
    {
        string path = Path.Combine(
            RepoRoot, "examples", project, "bin", Configuration(), "net10.0", assembly + ".dll");

        return File.Exists(path)
            ? path
            : throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} is not built; run dotnet build before these tests ({1})",
                assembly,
                path));
    }

    /// <summary>The build configuration this test assembly was built in.</summary>
    /// <returns>"Debug" or "Release".</returns>
    private static string Configuration() =>
        AppContext.BaseDirectory.Contains(
            Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? "Release"
            : "Debug";

    /// <summary>Spawns one of the built programs under <c>dotnet exec</c>.</summary>
    /// <param name="arguments">The arguments, starting with "exec" and the assembly path.</param>
    /// <param name="environment">Extra environment variables for the process.</param>
    /// <returns>The started process, with all three pipes redirected.</returns>
    internal static Process Start(
        IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        ProcessStartInfo info = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot,
        };

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach ((string name, string value) in environment ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            info.Environment[name] = value;
        }

        return Process.Start(info) ?? throw new InvalidOperationException("dotnet did not start");
    }

    /// <summary>The path of a built example's assembly, for the suites that spawn another one.</summary>
    /// <param name="project">The project directory under <c>examples/</c>.</param>
    /// <param name="assembly">The assembly name the project produces.</param>
    /// <returns>The absolute path of the built assembly.</returns>
    internal static string BuiltExample(string project, string assembly) => Built(project, assembly);

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "docs", "9p", "protocol-reference.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }
}
#endif
