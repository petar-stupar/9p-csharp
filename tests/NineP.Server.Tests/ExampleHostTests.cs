#if NET10_0_OR_GREATER
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using NineP.JsonFs;
using NineP.Protocol.Transports;
using NineP.TestSupport;
using NineP.TodoFs;
using Xunit;

namespace NineP.Server.Tests;

/// <summary>
/// What the two example servers do at startup, through the code the shipped binaries run rather
/// than a harness that assembles the same parts differently.
/// </summary>
public sealed class ExampleHostTests
{
    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// A listener that cannot bind is an error the operator is told about, not a spin. Both hosts
    /// used to wait for <c>Endpoints.Count</c> to reach the number of configured addresses; when a
    /// bind failed that count never rose, the server task faulted with nobody awaiting it, and the
    /// process burned a core announcing an address it never had. Awaiting
    /// <see cref="NinePServer.ListeningAsync"/> is what carries the bind failure out.
    /// </summary>
    [Fact]
    public async Task ABindFailureIsReportedRatherThanSpunOn()
    {
        // A socket of the test's own holds the port the hosts are then told to bind.
        using Socket taken = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        taken.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        taken.Listen(1);

        string address = string.Format(
            CultureInfo.InvariantCulture,
            "tcp://127.0.0.1:{0}",
            ((IPEndPoint)taken.LocalEndPoint!).Port);

        string directory = Path.Combine(Path.GetTempPath(), "bind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string document = Path.Combine(directory, "doc.json");
            await File.WriteAllTextAsync(document, "{}", Ct);

            JsonFsOptions json = JsonFsOptions.Parse(["--listen", address, "--file", document]);
            await AssertReportedAsync(json.Listen, server => JsonFsHost.AnnounceAsync(server, json));

            TodoFsOptions todo = TodoFsOptions.Parse(
            [
                "--listen", address,
                "--db", Path.Combine(directory, "todo.sqlite"),
                "--oidc-issuer", "https://example.invalid/realms/nine",
                "--oidc-audience", "ninep",
            ]);

            await AssertReportedAsync(todo.Listen, server => TodoFsHost.AnnounceAsync(server, todo));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A flag that cannot apply to any configured listener is said out loud at startup. A
    /// certificate that is loaded and never presented, or an <c>Origin</c> allow-list that is
    /// never consulted, reads as a server that is protected when it is not; the servers still
    /// start, because the listeners they were told to bind are the ones they bind.
    /// <b>Mutation:</b> delete the <c>Warn</c> call from <c>JsonFsHost.Transports</c> and the
    /// first two assertions below fail.
    /// </summary>
    [Fact]
    public void UnusedTransportFlagsAreWarnedAbout()
    {
        string directory = Path.Combine(Path.GetTempPath(), "flags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string document = Path.Combine(directory, "doc.json");
            File.WriteAllText(document, "{}");

            (string certificate, string key) = WriteCertificate(directory);

            RecordingLogger plain = new();
            JsonFsHost.Transports(
                JsonFsOptions.Parse(
                [
                    "--listen", "tcp://127.0.0.1:0",
                    "--file", document,
                    "--tls-cert", certificate,
                    "--tls-key", key,
                    "--ws-origin", "https://example.test",
                ]),
                plain);

            Assert.Contains(plain.Warnings, warning => warning.Contains("--tls-cert", StringComparison.Ordinal));
            Assert.Contains(plain.Warnings, warning => warning.Contains("--ws-origin", StringComparison.Ordinal));

            // A listener the flags do apply to warns about neither.
            RecordingLogger secured = new();
            JsonFsHost.Transports(
                JsonFsOptions.Parse(
                [
                    "--listen", "wss://127.0.0.1:0/9p",
                    "--file", document,
                    "--tls-cert", certificate,
                    "--tls-key", key,
                    "--ws-origin", "https://example.test",
                ]),
                secured);

            Assert.Empty(secured.Warnings);

            // todofs carries the same certificate flags and the same warning.
            RecordingLogger todo = new();
            TodoFsHost.Transports(
                TodoFsOptions.Parse(
                [
                    "--listen", "tcp://127.0.0.1:0",
                    "--db", Path.Combine(directory, "todo.sqlite"),
                    "--oidc-issuer", "https://example.invalid/realms/nine",
                    "--oidc-audience", "ninep",
                    "--tls-cert", certificate,
                    "--tls-key", key,
                ]),
                todo);

            Assert.Contains(todo.Warnings, warning => warning.Contains("--tls-cert", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// <c>--write-back</c> implies <c>--writable</c>: there is nothing to write back from a
    /// read-only server. The implication is deliberate and it is stated in the usage text, in the
    /// jsonfs README and in <c>docs/examples.md</c>, so that a flag that turns writing on does not
    /// do it silently.
    /// </summary>
    [Fact]
    public void WriteBackImpliesWritableAndSaysSo()
    {
        JsonFsOptions options = JsonFsOptions.Parse(["--listen", "tcp://127.0.0.1:0", "--file", "d.json", "--write-back"]);

        Assert.True(options.WriteBack);
        Assert.True(options.Writable);
        Assert.Contains("--write-back implies --writable", JsonFsOptions.Usage, StringComparison.Ordinal);
    }

    /// <summary>A self-signed certificate and its key, as the two PEM files the flags name.</summary>
    /// <param name="directory">Where to write them.</param>
    /// <returns>The certificate path and the key path.</returns>
    private static (string Certificate, string Key) WriteCertificate(string directory)
    {
        string certificatePath = Path.Combine(directory, "server.crt.pem");
        string keyPath = Path.Combine(directory, "server.key.pem");

        using X509Certificate2 certificate = CertificateFactory.CreateSelfSigned("localhost");
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(
            keyPath,
            certificate.GetRSAPrivateKey() is { } rsa
                ? rsa.ExportPkcs8PrivateKeyPem()
                : throw new InvalidOperationException("the generated certificate has no RSA key"));

        return (certificatePath, keyPath);
    }

    /// <summary>Starts a server on an address nothing can bind and watches the announce fail.</summary>
    /// <param name="listen">The addresses the host was configured with.</param>
    /// <param name="announce">The host's announce, which must surface the bind failure.</param>
    /// <returns>A task that completes when the failure has been observed.</returns>
    private static async Task AssertReportedAsync(
        IReadOnlyList<NinePAddress> listen, Func<NinePServer, Task> announce)
    {
        await using NinePServer server = new(new ServerOptions
        {
            Listen = listen,
            Transports = [new TcpTransport()],
        });

        Task serving = server.ServeAsync(new MemoryFilesystem(), CancellationToken.None);
        Task announcing = announce(server);

        // Bounded, because the defect this pins is an unbounded spin: before the fix the announce
        // never returns and this waits out the delay instead.
        Task finished = await Task.WhenAny(announcing, Task.Delay(TimeSpan.FromSeconds(15), Ct));

        Assert.Same(announcing, finished);
        await Assert.ThrowsAsync<SocketException>(async () => await announcing);
        await Assert.ThrowsAsync<SocketException>(async () => await serving);
    }
}
#endif
