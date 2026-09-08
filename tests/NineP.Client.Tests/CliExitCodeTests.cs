#if NET10_0_OR_GREATER
using Xunit;

namespace NineP.Client.Tests;

/// <summary>
/// The four exit codes of <c>docs/9p/fixtures/conformance.md</c>: 0 success, 1 protocol or
/// transport error, 2 server error, 3 usage. A script decides what to do next from these, so all
/// four are checked on the real process.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class CliExitCodeTests
{
    private const string Document = """{"name":"conformance"}""";

    /// <summary>0: the command ran and the server answered.</summary>
    [Fact]
    public async Task SuccessIsZero()
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document);
        (await harness.RunAsync(["cat", "/name"])).Expect(0);
    }

    /// <summary>1: nothing was ever negotiated, so there is no Rerror to report.</summary>
    [Fact]
    public async Task TransportFailureIsOne()
    {
        // Port 1 on the loopback interface has nothing listening on it, and a connect that is
        // refused is a transport failure rather than a server error.
        CliRun run = await CliHarness.RunRawAsync(["--addr", "tcp://127.0.0.1:1", "version"]);

        run.Expect(1);
        Assert.Empty(run.Stdout);
    }

    /// <summary>2: the server answered with an Rerror or an Rlerror.</summary>
    [Fact]
    public async Task ServerErrorIsTwo()
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document);
        CliRun run = await harness.RunAsync(["cat", "/missing"]);

        run.Expect(2);
        Assert.Contains("errno 2", run.Stderr, StringComparison.Ordinal);
    }

    /// <summary>3: the command line itself is wrong, so nothing was dialled at all.</summary>
    /// <param name="arguments">A command line the cli refuses.</param>
    /// <returns>A task that completes when the exit code has been checked.</returns>
    [Theory]
    [InlineData("--nonsense")]
    [InlineData("cat")]
    [InlineData("cat|/a|/b")]
    [InlineData("frobnicate|/a")]
    [InlineData("")]
    public async Task UsageErrorIsThree(string arguments)
    {
        CliRun run = await CliHarness.RunRawAsync(
            arguments.Split('|', StringSplitOptions.RemoveEmptyEntries));

        run.Expect(3);
        Assert.Contains("usage: ninep", run.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A TLS flag with an address that never performs a handshake is a usage error naming the flag
    /// and the scheme, not a certificate that is loaded and then dropped. Loading it and carrying
    /// on is how a caller ends up believing a plaintext connection was encrypted.
    /// <b>Mutation:</b> delete <c>RequireTlsFlagsMatchTheAddress</c> from <c>CliOptions.Parse</c>
    /// and every case below exits 1 or 2 instead of 3.
    /// </summary>
    /// <param name="flag">The TLS flag to pass.</param>
    /// <param name="address">An address whose scheme performs no TLS handshake.</param>
    /// <returns>A task that completes when the refusal has been checked.</returns>
    [Theory]
    [InlineData("--tls-ca", "tcp://127.0.0.1:5640")]
    [InlineData("--tls-cert", "tcp://127.0.0.1:5640")]
    [InlineData("--tls-key", "tcp://127.0.0.1:5640")]
    [InlineData("--tls-ca", "ws://127.0.0.1:5640/9p")]
    [InlineData("--tls-cert", "ws://127.0.0.1:5640/9p")]
    [InlineData("--tls-key", "ws://127.0.0.1:5640/9p")]
    public async Task TlsFlagWithAPlaintextAddressIsUsageError(string flag, string address)
    {
        CliRun run = await CliHarness.RunRawAsync(
            ["--addr", address, flag, "/nonexistent.pem", "version"]);

        run.Expect(3);
        Assert.Contains(flag, run.Stderr, StringComparison.Ordinal);
        Assert.Contains(address.Split(':')[0] + "://", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(run.Stdout);
    }

    /// <summary>
    /// A TLS flag with a <c>tls://</c> or <c>wss://</c> address is not refused by the check above,
    /// so the refusals are the scheme test doing its job and not the flags being rejected outright.
    /// </summary>
    /// <returns>A task that completes when the run has been checked.</returns>
    [Fact]
    public async Task TlsFlagWithATlsAddressIsAccepted()
    {
        await using CliHarness harness = await CliHarness.StartTlsAsync(Document);

        CliRun run = await CliHarness.RunRawAsync(
            ["--addr", harness.AddressAs("localhost"), "--tls-ca", harness.CertificatePath!, "version"]);

        run.Expect(0);
    }

    /// <summary>
    /// <c>-l</c> is a flag of <c>ls</c>. On any other command it used to be parsed, stored and
    /// never read, so <c>ninep cat -l /f</c> looked like it had been understood.
    /// <b>Mutation:</b> delete <c>RequireLongListingIsForLs</c> and the three cases below exit 1.
    /// </summary>
    /// <param name="command">The command <c>-l</c> was wrongly given to.</param>
    /// <returns>A task that completes when the refusal has been checked.</returns>
    [Theory]
    [InlineData("cat")]
    [InlineData("stat")]
    [InlineData("mkdir")]
    public async Task LongListingOnAnythingButLsIsUsageError(string command)
    {
        CliRun run = await CliHarness.RunRawAsync(
            ["--addr", "tcp://127.0.0.1:1", "-l", command, "/name"]);

        run.Expect(3);
        Assert.Contains("-l is a flag of ls", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(run.Stdout);
    }

    /// <summary><c>ls -l</c> itself is untouched: the check above refuses the other commands only.</summary>
    /// <returns>A task that completes when the listing has been checked.</returns>
    [Fact]
    public async Task LongListingOnLsStillRuns()
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document);

        (await harness.RunAsync(["ls", "-l", "/"])).Expect(0);
    }

    /// <summary>
    /// <c>--oidc-issuer</c> and <c>--oidc-client-id</c> configure the two grants and nothing else.
    /// With any other <c>--auth</c> they were read and never used, which reads as a login that was
    /// configured when none was performed.
    /// <b>Mutation:</b> delete <c>RequireOidcFlagsMatchTheGrant</c> and every case below exits 1.
    /// </summary>
    /// <param name="flag">The OIDC flag to pass.</param>
    /// <param name="auth">The <c>--auth</c> it was wrongly paired with.</param>
    /// <returns>A task that completes when the refusal has been checked.</returns>
    [Theory]
    [InlineData("--oidc-issuer", "none")]
    [InlineData("--oidc-client-id", "none")]
    [InlineData("--oidc-issuer", "token:s3cret")]
    [InlineData("--oidc-client-id", "bearer:abc")]
    public async Task OidcFlagWithoutAnOidcGrantIsUsageError(string flag, string auth)
    {
        CliRun run = await CliHarness.RunRawAsync(
            ["--addr", "tcp://127.0.0.1:1", "--auth", auth, flag, "x", "version"]);

        run.Expect(3);
        Assert.Contains(flag, run.Stderr, StringComparison.Ordinal);
        Assert.Contains("oidc-device", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(run.Stdout);
    }

    /// <summary>
    /// A TLS handshake the client ended is a transport failure: exit 1, one line, no frames. It
    /// used to escape <c>Main</c> as an <c>AuthenticationException</c>, so the runtime printed a
    /// dozen frames naming the absolute paths of the machine that built the binary and the process
    /// aborted with 134 — a code no caller of this cli is documented to expect.
    /// <b>Mutation:</b> drop <c>AuthenticationException</c> from <c>CliCommands.ExitCodeFor</c>
    /// and both cases below fail on the exit code and on the stack trace.
    /// </summary>
    /// <param name="trustTheRoot">
    /// False leaves the self-signed root untrusted; true trusts it and addresses the listener by a
    /// name the certificate was not issued to, so the name check fails instead.
    /// </param>
    /// <returns>A task that completes when the refusal has been checked.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TlsHandshakeFailureIsOne(bool trustTheRoot)
    {
        await using CliHarness harness = await CliHarness.StartTlsAsync(Document);

        // The certificate is issued to "localhost": trusting the root and then addressing
        // 127.0.0.1 leaves the name check as the only thing that can fail.
        string[] arguments = trustTheRoot
            ? ["--addr", harness.Address, "--tls-ca", harness.CertificatePath!, "version"]
            : ["--addr", harness.AddressAs("localhost"), "version"];

        CliRun run = await CliHarness.RunRawAsync(arguments);

        run.Expect(1);
        Assert.Empty(run.Stdout);
        AssertOneLineNoStackTrace(run);
    }

    /// <summary>The trusted, correctly named case still works, so the refusals above are the check
    /// doing its job and not TLS being broken.</summary>
    [Fact]
    public async Task TlsWithATrustedNameSucceeds()
    {
        await using CliHarness harness = await CliHarness.StartTlsAsync(Document);

        CliRun run = await CliHarness.RunRawAsync(
            ["--addr", harness.AddressAs("localhost"), "--tls-ca", harness.CertificatePath!, "cat", "/name"]);

        run.Expect(0);
        Assert.Equal("conformance", run.StdoutText);
    }

    /// <summary>A refused connect reports one line too, not a <c>SocketException</c> dump.</summary>
    [Fact]
    public async Task ConnectionRefusedReportsOneLine()
    {
        CliRun run = await CliHarness.RunRawAsync(["--addr", "tcp://127.0.0.1:1", "version"]);

        run.Expect(1);
        AssertOneLineNoStackTrace(run);
    }

    /// <summary>
    /// The <c>--auth-optional</c> fallback reaches the tree instead of throwing out of
    /// <c>Main</c>: the crash that finding H-1 recorded was an unhandled
    /// <c>InvalidOperationException</c> from a null attach root, and it exited 134. What it says
    /// is one line, on standard error, in the shape every other line of this cli has.
    /// </summary>
    [Fact]
    public async Task AuthOptionalFallbackIsNotACrash()
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document, "--auth", "none");

        CliRun run = await harness.RunAsync(["--auth", "token:x", "--auth-optional", "ls", "/"]);

        run.Expect(0);
        AssertOneLineNoStackTrace(run);
    }

    /// <summary>
    /// A credential that was presented and then dropped is announced. <c>--auth-optional</c> used
    /// to fall back to an anonymous attach in silence, so a caller who asked for a token and got a
    /// session with none had nothing to tell the two apart. The notice goes to standard error, and
    /// standard output stays byte-identical, because the conformance fixture diffs standard output.
    /// <b>Mutation:</b> drop the <c>Console.Error.WriteLineAsync</c> from
    /// <c>CliProgram.AttachAsync</c> and the stderr assertion below fails while the stdout one
    /// keeps passing.
    /// </summary>
    [Fact]
    public async Task AuthOptionalFallbackSaysItAttachedAnonymously()
    {
        await using CliHarness harness = await CliHarness.StartAsync(Document, "--auth", "none");

        CliRun anonymous = await harness.RunAsync(["--auth", "token:x", "--auth-optional", "cat", "/name"]);
        CliRun plain = await harness.RunAsync(["cat", "/name"]);

        anonymous.Expect(0);
        plain.Expect(0);

        Assert.Equal(
            "ninep: server requires no authentication; attached anonymously",
            anonymous.Stderr.TrimEnd('\n'));
        Assert.Empty(plain.Stderr);
        Assert.Equal(plain.Stdout, anonymous.Stdout);
    }

    /// <summary>
    /// What a user is allowed to see when the cli fails: one line, beginning with the program's
    /// own name or with the frozen <c>error:</c> prefix, and not one frame of a .NET stack trace.
    /// </summary>
    /// <param name="run">The finished run.</param>
    private static void AssertOneLineNoStackTrace(CliRun run)
    {
        string[] lines = run.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines);
        Assert.True(
            lines[0].StartsWith("ninep: ", StringComparison.Ordinal)
            || lines[0].StartsWith("error: ", StringComparison.Ordinal),
            "the failure line was: " + lines[0]);
        Assert.DoesNotContain("   at ", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", run.Stderr, StringComparison.Ordinal);
    }
}
#endif
