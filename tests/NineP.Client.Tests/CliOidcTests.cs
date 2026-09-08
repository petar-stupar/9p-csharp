#if NET10_0_OR_GREATER
using NineP.TestSupport.FakeIssuer;
using Xunit;

namespace NineP.Client.Tests;

/// <summary>
/// Conformance Part C.4: the cli's three OIDC paths, end to end against the built <c>todofs</c>
/// and the in-process fake issuer. Nothing here touches the network or a real Keycloak.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class CliOidcTests
{
    private const string ClientId = "todofs-cli";

    /// <summary>
    /// <c>--auth oidc-device</c> completes the RFC 8628 grant — through the issuer's
    /// <c>authorization_pending</c> and its one <c>slow_down</c> — and lists exactly the
    /// authenticating user's directory.
    /// </summary>
    [Fact]
    public async Task DeviceGrantAttachesAndListsTheUsersOwnDirectory()
    {
        using FakeOidcIssuer issuer = FakeOidcIssuer.Start();
        await using TodoFsProcess server = await TodoFsProcess.StartAsync(issuer);

        CliRun run = await RunAsync(server, "--auth", "oidc-device", "--oidc-issuer", issuer.Issuer,
            "--oidc-client-id", ClientId, "ls", "/users");

        run.Expect(0);
        Assert.Equal("ctl\nglenda/\n", run.StdoutText);

        // The user code and its URL go to standard error, so a piped listing is not corrupted.
        Assert.Contains("WDJB-MJHT", run.Stderr, StringComparison.Ordinal);
    }

    /// <summary>The password grant works and warns, on standard error, on every use.</summary>
    [Fact]
    public async Task PasswordGrantWarnsAndAttaches()
    {
        using FakeOidcIssuer issuer = FakeOidcIssuer.Start();
        await using TodoFsProcess server = await TodoFsProcess.StartAsync(issuer);

        CliRun run = await RunAsync(
            server,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["NINEP_PASSWORD"] = issuer.Password },
            "--auth", "oidc-password", "--oidc-issuer", issuer.Issuer,
            "--oidc-client-id", ClientId, "ls", "/users");

        run.Expect(0);
        Assert.Contains(
            "warning: the password grant is for development and test only",
            run.Stderr,
            StringComparison.Ordinal);
    }

    /// <summary>A token the caller already holds attaches, and an expired one does not.</summary>
    [Fact]
    public async Task BearerTokenAttachesAndAnExpiredOneDoesNot()
    {
        using FakeOidcIssuer issuer = FakeOidcIssuer.Start();
        await using TodoFsProcess server = await TodoFsProcess.StartAsync(issuer);

        (await RunAsync(server, "--auth", "bearer:" + issuer.IssueToken(), "ls", "/users")).Expect(0);

        string expired = issuer.IssueToken(new FakeTokenOptions
        {
            NotBefore = TimeSpan.FromHours(-2),
            Lifetime = TimeSpan.FromMinutes(1),
        });

        (await RunAsync(server, "--auth", "bearer:" + expired, "ls", "/users")).Expect(2);
    }

    /// <summary>
    /// §8.3(a): a valid token cannot authenticate an attach that claims somebody else. The
    /// authenticator compares the <c>Tattach</c> uname with the identity the token proved.
    /// </summary>
    [Fact]
    public async Task AttachClaimingAnotherUserIsRefused()
    {
        using FakeOidcIssuer issuer = FakeOidcIssuer.Start();
        await using TodoFsProcess server = await TodoFsProcess.StartAsync(issuer);

        CliRun run = await CliHarness.RunRawAsync(
        [
            "--addr", server.Address, "--dialect", "9P2000.L", "--uname", "bootes",
            "--auth", "bearer:" + issuer.IssueToken(), "ls", "/users",
        ]);

        run.Expect(2);
    }

    /// <summary>Both grants need the issuer and the client id, and say so rather than dialling.</summary>
    /// <param name="grant">The grant to ask for.</param>
    /// <returns>A task that completes when the usage error has been checked.</returns>
    [Theory]
    [InlineData("oidc-device")]
    [InlineData("oidc-password")]
    public async Task GrantsNeedIssuerAndClientId(string grant)
    {
        CliRun run = await CliHarness.RunRawAsync(
            ["--addr", "tcp://127.0.0.1:1", "--auth", grant, "version"]);

        run.Expect(3);
        Assert.Contains("--oidc-issuer", run.Stderr, StringComparison.Ordinal);
    }

    private static Task<CliRun> RunAsync(TodoFsProcess server, params string[] arguments) =>
        RunAsync(server, null, arguments);

    private static Task<CliRun> RunAsync(
        TodoFsProcess server, IReadOnlyDictionary<string, string>? environment, params string[] arguments) =>
        CliHarness.RunRawAsync(
            [
                "--addr", server.Address, "--dialect", "9P2000.L", "--uname", "glenda", .. arguments,
            ],
            stdin: null,
            environment: environment);
}
#endif
