using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using NineP.Protocol;
using NineP.TestSupport;

namespace NineP.Conformance;

/// <summary>
/// The real thing: the built <c>jsonfs</c> in one process and the built <c>ninep</c> in another,
/// which is what conformance is actually about — two programs on a socket, not two objects.
/// </summary>
internal sealed class ProcessTarget : ConformanceTarget
{
    private readonly Process _server;
    private readonly string _address;
    private readonly string[] _clientFlags;
    private readonly string _workspace;
    private readonly bool _ownsDocument;

    private ProcessTarget(
        Dialect dialect,
        string transport,
        Process server,
        string address,
        string[] clientFlags,
        string workspace,
        bool ownsDocument)
        : base(dialect, transport)
    {
        _server = server;
        _address = address;
        _clientFlags = clientFlags;
        _workspace = workspace;
        _ownsDocument = ownsDocument;
    }

    /// <summary>The address <c>jsonfs</c> is listening on, as it announced it.</summary>
    public string Address => _address;

    /// <summary>Starts <c>jsonfs</c> over a private copy of a document and waits for its address.</summary>
    /// <param name="dialect">The dialect to serve and to ask for.</param>
    /// <param name="transport">One of <c>tcp</c>, <c>tls</c> or <c>ws</c>.</param>
    /// <param name="document">The JSON text to serve.</param>
    /// <param name="serverFlags">Extra jsonfs flags, such as <c>--writable</c>.</param>
    /// <returns>The running target.</returns>
    public static async Task<ProcessTarget> StartAsync(
        Dialect dialect, string transport, string document, params string[] serverFlags)
    {
        string workspace = Path.Combine(
            Path.GetTempPath(), "ninep-conformance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        string documentPath = Path.Combine(workspace, "doc.json");
        await File.WriteAllTextAsync(documentPath, document);

        return await StartAtAsync(dialect, transport, documentPath, ownsDocument: true, serverFlags);
    }

    /// <summary>
    /// Starts <c>jsonfs</c> over a document that already exists and that this target does not own,
    /// which is how Part B step 8 restarts a server over what <c>--write-back</c> left behind.
    /// </summary>
    /// <param name="dialect">The dialect to serve and to ask for.</param>
    /// <param name="transport">One of <c>tcp</c>, <c>tls</c> or <c>ws</c>.</param>
    /// <param name="documentPath">The document to serve.</param>
    /// <param name="ownsDocument">True to delete the document's directory on dispose.</param>
    /// <param name="serverFlags">Extra jsonfs flags.</param>
    /// <returns>The running target.</returns>
    public static async Task<ProcessTarget> StartAtAsync(
        Dialect dialect,
        string transport,
        string documentPath,
        bool ownsDocument,
        params string[] serverFlags)
    {
        string workspace = Path.GetDirectoryName(Path.GetFullPath(documentPath))
            ?? throw new ArgumentException("the document has no directory", nameof(documentPath));

        // TLS needs a certificate, and a fixture certificate in a repository is key material that
        // rots on its expiry date; this one is made here and thrown away with the run.
        (string[] serverTls, string[] clientTls) = transport == "tls"
            ? Certificates(workspace)
            : ([], []);

        string host = transport == "tls" ? "localhost" : "127.0.0.1";
        List<string> arguments =
        [
            "exec",
            RepoLayout.BuiltExample("NineP.JsonFs", "jsonfs"),
            "--listen",
            string.Format(CultureInfo.InvariantCulture, "{0}://{1}:0", transport, host),
            "--file",
            documentPath,
            "--dialects",
            NineP.Protocol.Negotiation.Negotiator.VersionString(dialect),
        ];
        arguments.AddRange(serverTls);
        arguments.AddRange(serverFlags);

        Process server = Spawn(arguments);
        string? line = await server.StandardOutput.ReadLineAsync();

        if (line is null || !line.StartsWith("listening ", StringComparison.Ordinal))
        {
            server.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                "jsonfs did not announce an address: " + await server.StandardError.ReadToEndAsync());
        }

        return new ProcessTarget(
            dialect, transport, server, line.Split(' ')[1], clientTls, workspace, ownsDocument);
    }

    /// <summary>Runs one cli command against this server.</summary>
    /// <param name="arguments">The command and its arguments, plus any cli flags.</param>
    /// <param name="stdin">Bytes the command reads from standard input.</param>
    /// <returns>The exit code and the two streams.</returns>
    public override async Task<CliResult> RunAsync(IReadOnlyList<string> arguments, byte[]? stdin = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        List<string> full =
        [
            "exec",
            RepoLayout.BuiltExample("NineP.Cli", "ninep"),
            "--addr",
            _address,
            "--dialect",
            NineP.Protocol.Negotiation.Negotiator.VersionString(Dialect),
        ];
        full.AddRange(_clientFlags);
        full.AddRange(arguments);

        using Process cli = Spawn(full);

        await using (Stream input = cli.StandardInput.BaseStream)
        {
            if (stdin is not null)
            {
                await input.WriteAsync(stdin);
            }
        }

        // Both pipes are drained before the wait: a process that fills one while the driver reads
        // the other would deadlock.
        using MemoryStream stdout = new();
        Task<string> stderr = cli.StandardError.ReadToEndAsync();
        await cli.StandardOutput.BaseStream.CopyToAsync(stdout);
        string errors = await stderr;
        await cli.WaitForExitAsync();

        return new CliResult(cli.ExitCode, stdout.ToArray(), errors);
    }

    /// <summary>Stops the server and removes the workspace it was serving from.</summary>
    /// <returns>A task that completes when everything is gone.</returns>
    public override async ValueTask DisposeAsync()
    {
        if (!_server.HasExited)
        {
            _server.Kill(entireProcessTree: true);
        }

        await _server.WaitForExitAsync();
        _server.Dispose();

        if (!_ownsDocument)
        {
            return;
        }

        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // The temp directory outlives the run rather than failing it.
        }
    }

    private static (string[] Server, string[] Client) Certificates(string workspace)
    {
        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("localhost");

        string certificatePath = Path.Combine(workspace, "server.crt.pem");
        string keyPath = Path.Combine(workspace, "server.key.pem");

        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, PrivateKeyPem(certificate));

        return (
            ["--tls-cert", certificatePath, "--tls-key", keyPath],
            ["--tls-ca", certificatePath]);
    }

    private static string PrivateKeyPem(X509Certificate2 certificate) =>
        certificate.GetRSAPrivateKey() is { } rsa
            ? rsa.ExportPkcs8PrivateKeyPem()
            : throw new InvalidOperationException("the generated certificate has no RSA private key");

    private static Process Spawn(IReadOnlyList<string> arguments)
    {
        ProcessStartInfo info = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoLayout.Root,
        };

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new InvalidOperationException("dotnet did not start");
    }
}
