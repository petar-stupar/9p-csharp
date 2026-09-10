using NineP.Server.Tests;
using NineP.Server.Tests.Conformance;
#if NET10_0_OR_GREATER
using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using NineP.TestSupport.FakeIssuer;
using NineP.TodoFs;
using Xunit;

namespace NineP.Server.Tests.Security;

/// <summary>
/// The shipped wiring, not a harness that assembles the same parts differently: the command line
/// parses, <see cref="TodoFsHost"/> composes and a real client dials over TCP.
/// </summary>
[Trait("Category", "Security")]
public sealed class ShippedTodoFsTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// An attach with <c>afid = NOFID</c> is refused: the bearer token over the afid is the only
    /// way in, and a claimed <c>uname</c> is not a credential.
    /// <b>Mutation:</b> return <c>false</c> from <c>KeycloakAuthenticator.IsRequired</c> and the
    /// attach below succeeds with nothing proved about it.
    /// </summary>
    [Fact]
    public async Task ShippedWiringRefusesAnUnauthenticatedAttach()
    {
        using FakeOidcIssuer issuer = FakeOidcIssuer.Start();
        await using ShippedTodoFs todofs = await ShippedTodoFs.StartAsync(issuer);

        NinePException refusal = await Assert.ThrowsAsync<NinePException>(
            async () => await todofs.ConnectAsync("glenda"));

        Assert.Equal(Errno.EACCES, refusal.Error.Errno);

        // And the afid exchange works on the same server, so the refusal is the authenticator and
        // not a broken server.
        await using NinePSession proven = await todofs.ConnectAsync(
            "glenda", new BearerTokenCredential(issuer.IssueToken()));

        Assert.Contains(
            "glenda",
            (await proven.ReadDirAsync("users", Ct)).Select(entry => entry.Name),
            StringComparer.Ordinal);
    }
}

/// <summary>
/// <c>todofs</c> as an operator gets it: a command line parsed by <see cref="TodoFsOptions"/> and
/// composed by <see cref="TodoFsHost"/>, bound to a loopback port the kernel chose. Nothing here
/// substitutes a part, which is the point — <see cref="TodoFsHarness"/> is convenient and it is
/// not what ships.
/// </summary>
internal sealed class ShippedTodoFs : IAsyncDisposable
{
    private readonly TodoFsHost _host;
    private readonly Task _serving;
    private readonly string _directory;
    private readonly NinePAddress _address;

    private ShippedTodoFs(TodoFsHost host, Task serving, string directory, NinePAddress address)
    {
        _host = host;
        _serving = serving;
        _directory = directory;
        _address = address;
    }

    /// <summary>Parses a real command line, composes it and starts serving.</summary>
    /// <param name="issuer">The realm the server validates against.</param>
    /// <returns>The running server.</returns>
    public static async Task<ShippedTodoFs> StartAsync(FakeOidcIssuer issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);

        string directory = Path.Combine(Path.GetTempPath(), "todofs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string[] arguments =
        [
            "--listen", "tcp://127.0.0.1:0",
            "--db", Path.Combine(directory, "todo.sqlite"),
            "--oidc-issuer", issuer.Issuer,
            "--oidc-audience", issuer.Audience,

            // The fake issuer serves plain HTTP on loopback, which is the one case the flag exists
            // for; without it the discovery fetch is refused before a token is ever read.
            "--allow-insecure-issuer",
        ];

        TodoFsHost host = await TodoFsHost.CreateAsync(
            TodoFsOptions.Parse(arguments), cancellationToken: TestDeadlines.Wrap(TestContext.Current.CancellationToken));

        Task serving = host.Server.ServeAsync(host.Filesystem, CancellationToken.None);
        await host.Server.Listening;

        return new ShippedTodoFs(host, serving, directory, host.Server.Endpoints[0]);
    }

    /// <summary>Dials the bound port with the shipped client and attaches.</summary>
    /// <param name="uname">The user name to claim.</param>
    /// <param name="credential">The credential to run over an afid; null attaches with NOFID.</param>
    /// <returns>The attached session; the caller disposes it.</returns>
    public async Task<NinePSession> ConnectAsync(string uname, ICredential? credential = null)
    {
        ClientOptions options = new()
        {
            Dialects = [Dialect.P9_2000_L],
            Uname = uname,
            Credential = credential,
        };

        NinePSession session = await NinePClient.ConnectAsync(
            new TcpTransport(),
            _address,
            options,
            TestDeadlines.Wrap(TestContext.Current.CancellationToken));

        try
        {
            await session.AttachAsync(TestDeadlines.Wrap(TestContext.Current.CancellationToken));
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>Stops the server and removes the database.</summary>
    /// <returns>A task that completes when everything is gone.</returns>
    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();

        try
        {
            await _serving;
        }
        catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException)
        {
            // The accept loop was stopped on purpose.
        }

        // No SqliteConnection.ClearAllPools() here: it is process-wide and would close pooled
        // connections belonging to whatever else is running beside this test.
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
