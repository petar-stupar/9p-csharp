#if NET10_0_OR_GREATER
using System.Diagnostics;
using System.Text;
using NineP.TestSupport.FakeIssuer;
using Xunit;
using NineP.TestSupport;

namespace NineP.Client.Tests;

/// <summary>
/// The built <c>todofs</c> as a process, pointed at an in-process fake issuer. Everything the OIDC
/// path does — discovery, the JWKS, the grants — happens over loopback HTTP inside this test run,
/// so nothing reaches the network and there is no Keycloak to start.
/// </summary>
internal sealed class TodoFsProcess : IAsyncDisposable
{
    private readonly Process _server;
    private readonly string _directory;

    private TodoFsProcess(Process server, string address, string directory, string startupErrors)
    {
        _server = server;
        Address = address;
        _directory = directory;
        StartupErrors = startupErrors;
    }

    /// <summary>The address the server bound.</summary>
    public string Address { get; }

    /// <summary>Everything the server wrote to standard error before it announced its address.</summary>
    public string StartupErrors { get; }

    /// <summary>Starts <c>todofs</c> against a fake issuer, on a fresh database.</summary>
    /// <param name="issuer">The issuer to validate tokens against.</param>
    /// <param name="extra">Extra todofs flags.</param>
    /// <returns>The running server.</returns>
    public static async Task<TodoFsProcess> StartAsync(FakeOidcIssuer issuer, params string[] extra)
    {
        ArgumentNullException.ThrowIfNull(issuer);

        string directory = Path.Combine(Path.GetTempPath(), "todofs-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        List<string> arguments =
        [
            "exec",
            CliHarness.BuiltExample("NineP.TodoFs", "todofs"),
            "--listen",
            "tcp://127.0.0.1:0",
            "--db",
            Path.Combine(directory, "todo.sqlite"),
            "--oidc-issuer",
            issuer.Issuer,
            "--oidc-audience",
            issuer.Audience,

            // The fake issuer serves plain HTTP on loopback, which is the one case where the
            // realm's documents are not fetched over HTTPS (E-4).
            "--allow-insecure-issuer",
        ];
        arguments.AddRange(extra);

        Process server = CliHarness.Start(arguments);

        // Standard error is drained in the background, because the startup warning is written
        // there before the address appears on standard output.
        StringBuilder errors = new();
        Task draining = Task.Run(async () =>
        {
            string? line;
            while ((line = await server.StandardError.ReadLineAsync()) is not null)
            {
                lock (errors)
                {
                    errors.AppendLine(line);
                }
            }
        });

        string? announced = await server.StandardOutput.ReadLineAsync(TestDeadlines.Wrap(TestContext.Current.CancellationToken));

        if (announced is null || !announced.StartsWith("listening ", StringComparison.Ordinal))
        {
            server.Kill(entireProcessTree: true);
            await draining;
            throw new InvalidOperationException("todofs did not announce an address: " + errors);
        }

        // Everything written before the address is what the operator sees at startup.
        string startup;
        lock (errors)
        {
            startup = errors.ToString();
        }

        return new TodoFsProcess(server, announced.Split(' ')[1], directory, startup);
    }

    /// <summary>Stops the server and removes the database.</summary>
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
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // The temp directory outlives the test rather than failing it.
        }
    }
}
#endif
