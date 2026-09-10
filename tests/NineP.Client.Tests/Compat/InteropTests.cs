using NineP.Client.Tests;
using NineP.Client.Tests.Conformance;
#if NET10_0_OR_GREATER
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NineP.TestSupport;
using Xunit;

namespace NineP.Client.Tests.Compat;

/// <summary>
/// This implementation against other 9P implementations, in both directions. Every test is an
/// opt-in: it skips, naming the variable it wants, unless the environment points it at a peer
/// (<c>tests/interop/setup.sh</c> fetches the peers at pinned versions and prints the variables).
/// The measured results, with versions and machine, are recorded in <c>docs/interop.md</c>;
/// these tests are how anyone reproduces them.
/// </summary>
[Collection(CliCollection.Name)]
[Trait("Kind", "Interop")]
[Trait("Category", "Compat")]
public sealed class InteropTests
{
    private const string Unicode = "héllo — 世界 🚀";

    /// <summary>
    /// The length of the file every peer reads in more than one <c>Tread</c>: at msize 4096 the
    /// payload is 4072 bytes, so this is nine round trips, and plan9port's <c>9p read</c> uses a
    /// 4096-byte buffer, so it is eight there.
    /// </summary>
    private const int ChunkedLength = 32768;

    /// <summary>
    /// The chunked file's content: every eight-byte block is the zero-padded decimal offset of
    /// that block, so a chunk delivered out of order, twice or not at all changes the bytes.
    /// ASCII only, so it is a legal JSON string and the same bytes through every peer.
    /// </summary>
    private static readonly string Chunked = BuildChunked();

    private static readonly byte[] ChunkedBytes = Encoding.ASCII.GetBytes(Chunked);

    private static readonly string Document =
        """{"name":"conformance","unicode":"héllo — 世界 🚀","empty":"","dir":{"file.txt":"content of file.txt"},"list":[1,2],"chunked":"""
        + "\"" + Chunked + "\"}";

    private static CancellationToken Ct => TestDeadlines.Wrap(TestContext.Current.CancellationToken);

    /// <summary>
    /// hugelgupf/p9's <c>p9ufs</c> serves a directory tree over 9P2000.L; our client lists it,
    /// reads a Unicode name and a nested file, and stats a directory.
    /// </summary>
    [Fact]
    public async Task OurClientAgainstP9ufs()
    {
        string p9ufs = Peer("NINEP_INTEROP_P9UFS", "the p9ufs binary: GOBIN=<dir> go install github.com/hugelgupf/p9/cmd/p9ufs@v0.4.1");
        string tree = Tree();
        int port = FreePort();

        using Process server = StartProcess(p9ufs, ["-root", tree, "127.0.0.1:" + port]);
        try
        {
            await WaitForPortAsync(port);
            string addr = "tcp://127.0.0.1:" + port;

            Assert.StartsWith("dialect=9P2000.L", (await Cli(addr, "9P2000.L", "version")).StdoutText, StringComparison.Ordinal);
            Assert.Equal(["chunked", "dir/", "empty", "name", "unicode"], Lines((await Cli(addr, "9P2000.L", "ls", "/")).StdoutText).Order(StringComparer.Ordinal));
            Assert.Empty((await Cli(addr, "9P2000.L", "cat", "/empty")).Stdout);
            Assert.Equal("conformance", (await Cli(addr, "9P2000.L", "cat", "/name")).StdoutText);
            Assert.Equal(Unicode, (await Cli(addr, "9P2000.L", "cat", "/unicode")).StdoutText);
            Assert.Equal("content of file.txt", (await Cli(addr, "9P2000.L", "cat", "/dir/file.txt")).StdoutText);
            Assert.StartsWith("kind=dir", (await Cli(addr, "9P2000.L", "stat", "/dir")).StdoutText, StringComparison.Ordinal);
            Assert.Equal(["file.txt", "sub/"], Lines((await Cli(addr, "9P2000.L", "ls", "/dir")).StdoutText).Order(StringComparer.Ordinal));

            Assert.Equal("dialect=9P2000.L msize=4096", (await Cli(addr, "9P2000.L", "--msize", "4096", "version")).StdoutText.Trim());
            Assert.Equal(ChunkedBytes, (await Cli(addr, "9P2000.L", "--msize", "4096", "cat", "/chunked")).Stdout);
        }
        finally
        {
            Stop(server);
            Directory.Delete(tree, recursive: true);
        }
    }

    /// <summary>
    /// diod, the 9P2000.L server Linux distributions ship, exports a directory from a container;
    /// our client lists, reads, writes, creates, renames and removes through it. diod answers
    /// <c>Trenameat</c> with <c>EOPNOTSUPP</c>, so the rename exercises the client's
    /// <c>Trename</c> fallback against the real thing. diod also reverse-resolves the client's
    /// address and drops the connection when it cannot, so the container gets a hosts entry for
    /// the address a published port carries.
    /// </summary>
    [Fact]
    public async Task OurClientAgainstDiod()
    {
        string image = Peer("NINEP_INTEROP_DIOD_IMAGE", "a docker image with diod: docker build -t ninep-diod -f tests/interop/diod.Dockerfile tests/interop");
        int port = FreePort();
        string container = "ninep-interop-" + Guid.NewGuid().ToString("N")[..8];
        string hosts = Environment.GetEnvironmentVariable("NINEP_INTEROP_DOCKER_HOST_ADDR") is { Length: > 0 } configured
            ? "--add-host=ninep-host:" + configured
            : "--add-host=ninep-host:192.168.65.1 --add-host=ninep-host2:172.17.0.1";

        await RunAsync("docker", [
            "run", "-d", "--name", container, "-p", port + ":5640", .. hosts.Split(' '), image, "sh", "-c",
            "mkdir -p /export/sub && printf 'hello, 9P\\n' > /export/hello.txt && printf 'inner\\n' > /export/sub/inner.txt "
            + "&& awk 'BEGIN { for (i = 0; i < " + ChunkedLength.ToString(CultureInfo.InvariantCulture) + "; i += 8) printf \"%08d\", i }' > /export/chunked "
            + "&& chmod -R a+rwX /export && exec diod -f -n -l 0.0.0.0:5640 -e /export"]);
        try
        {
            await WaitForPortAsync(port);
            string addr = "tcp://127.0.0.1:" + port;
            string[] auth = ["--uname", "root", "--aname", "/export"];

            Assert.StartsWith("dialect=9P2000.L", (await Cli(addr, "9P2000.L", [.. auth, "version"])).StdoutText, StringComparison.Ordinal);
            Assert.Equal(["chunked", "hello.txt", "sub/"], Lines((await Cli(addr, "9P2000.L", [.. auth, "ls", "/"])).StdoutText).Order(StringComparer.Ordinal));
            Assert.Equal("hello, 9P\n", (await Cli(addr, "9P2000.L", [.. auth, "cat", "/hello.txt"])).StdoutText);
            Assert.Equal("inner\n", (await Cli(addr, "9P2000.L", [.. auth, "cat", "/sub/inner.txt"])).StdoutText);
            Assert.StartsWith("kind=dir", (await Cli(addr, "9P2000.L", [.. auth, "stat", "/sub"])).StdoutText, StringComparison.Ordinal);

            // The peer's own view of the file first, so a defect in the fixture is not blamed on the read.
            Assert.StartsWith(Sha256(ChunkedBytes), await RunAsync("docker", ["exec", container, "sha256sum", "/export/chunked"]), StringComparison.Ordinal);
            Assert.Equal("dialect=9P2000.L msize=4096", (await Cli(addr, "9P2000.L", [.. auth, "--msize", "4096", "version"])).StdoutText.Trim());
            Assert.Equal(ChunkedBytes, (await Cli(addr, "9P2000.L", [.. auth, "--msize", "4096", "cat", "/chunked"])).Stdout);

            await Cli(addr, "9P2000.L", [.. auth, "write", "/written.txt"], "from ninep\n"u8.ToArray());
            Assert.Equal("from ninep\n", await RunAsync("docker", ["exec", container, "cat", "/export/written.txt"]));

            await Cli(addr, "9P2000.L", [.. auth, "write", "/written.txt"], []);
            Assert.Empty((await Cli(addr, "9P2000.L", [.. auth, "cat", "/written.txt"])).Stdout);
            Assert.Equal("0", (await RunAsync("docker", ["exec", container, "stat", "-c", "%s", "/export/written.txt"])).Trim());

            await Cli(addr, "9P2000.L", [.. auth, "mkdir", "/d1"]);
            await Cli(addr, "9P2000.L", [.. auth, "mv", "/d1", "/d2"]);
            Assert.Equal(["chunked", "d2", "hello.txt", "sub", "written.txt"], Lines(await RunAsync("docker", ["exec", container, "ls", "/export"])).Order(StringComparer.Ordinal));

            await Cli(addr, "9P2000.L", [.. auth, "rm", "/d2"]);
            await Cli(addr, "9P2000.L", [.. auth, "rm", "/written.txt"]);
            Assert.Equal(["chunked", "hello.txt", "sub"], Lines(await RunAsync("docker", ["exec", container, "ls", "/export"])).Order(StringComparer.Ordinal));
        }
        finally
        {
            await RunAsync("docker", ["rm", "-f", container], allowFailure: true);
        }
    }

    /// <summary>
    /// plan9port's <c>9p</c> is a 9P2000 client; it lists, reads and stats our <c>jsonfs</c>.
    /// <c>9p</c> has no msize flag (<c>usage: 9p [-n] [-a address] [-A aname] cmd args...</c>);
    /// lib9pclient negotiates 8192 and <c>9p read</c> reads through a 4096-byte buffer, so the
    /// chunked file is eight <c>Tread</c>s of 4096 bytes.
    /// </summary>
    [Fact]
    public async Task Plan9portClientAgainstOurJsonfs()
    {
        string plan9 = Peer("NINEP_INTEROP_PLAN9PORT", "a plan9port checkout built with ./INSTALL -b (bin/9p under it)");
        string nine = Path.Combine(plan9, "bin", "9p");
        Assert.True(File.Exists(nine), nine + " does not exist");

        await using CliHarness harness = await CliHarness.StartAsync(Document, "--dialects", "9P2000");
        string dial = "tcp!127.0.0.1!" + new Uri(harness.Address).Port;
        Dictionary<string, string> env = new(StringComparer.Ordinal) { ["PLAN9"] = plan9 };

        string listing = await RunAsync(nine, ["-a", dial, "ls", "/"], env);
        Assert.Equal(["chunked", "dir", "empty", "list", "name", "unicode"], Lines(listing).Order(StringComparer.Ordinal));
        Assert.Empty(await RunAsync(nine, ["-a", dial, "read", "/empty"], env));
        Assert.Equal("conformance", await RunAsync(nine, ["-a", dial, "read", "/name"], env));
        Assert.Equal(Unicode, await RunAsync(nine, ["-a", dial, "read", "/unicode"], env));
        Assert.Equal("content of file.txt", await RunAsync(nine, ["-a", dial, "read", "/dir/file.txt"], env));
        Assert.Contains(" d) m 020000000755 ", await RunAsync(nine, ["-a", dial, "stat", "/dir"], env), StringComparison.Ordinal);
        Assert.Equal(["0", "1"], Lines(await RunAsync(nine, ["-a", dial, "ls", "/list"], env)));
        Assert.Equal(Chunked, await RunAsync(nine, ["-a", dial, "read", "/chunked"], env));
    }

    /// <summary>
    /// The Linux kernel's own client, v9fs, mounts our <c>jsonfs</c> from a VM in each dialect and
    /// reads it back byte for byte. The kernel's TCP transport takes a numeric address, so the
    /// host is named by the address the VM resolves <c>host.lima.internal</c> to. A second mount
    /// with <c>msize=4096</c>, the kernel's minimum, reads the chunked file in nine <c>Tread</c>s.
    /// </summary>
    [Theory]
    [InlineData("9p2000.L")]
    [InlineData("9p2000.u")]
    [InlineData("9p2000")]
    public async Task LinuxV9fsAgainstOurJsonfs(string version)
    {
        string vm = Peer("NINEP_INTEROP_LIMA_VM", "a lima VM whose kernel has the 9p module (tests/interop/setup.sh creates one)");
        string document = Path.Combine(Path.GetTempPath(), "ninep-interop-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(document, Document, Ct);

        using Process server = CliHarness.Start(
            ["exec", CliHarness.BuiltExample("NineP.JsonFs", "jsonfs"), "--listen", "tcp://0.0.0.0:0", "--file", document]);
        try
        {
            string? line = await server.StandardOutput.ReadLineAsync(Ct);
            Assert.True(line is not null && line.StartsWith("listening ", StringComparison.Ordinal), "jsonfs did not announce an address: " + line);
            int port = new Uri(line.Split(' ')[1]).Port;

            string host = (await RunAsync("limactl", ["shell", vm, "--", "sh", "-c", "getent hosts host.lima.internal | cut -d' ' -f1"])).Trim();
            Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", host);

            string script = string.Format(
                CultureInfo.InvariantCulture,
                "set -e; mkdir -p /mnt/ninep-interop /mnt/ninep-interop-4k; "
                + "mount -t 9p -o trans=tcp,port={0},version={1},access=any,uname=ninep {2} /mnt/ninep-interop; "
                + "trap 'cd /; umount /mnt/ninep-interop' EXIT; "
                + "cd /mnt/ninep-interop; test $(wc -c < empty) -eq 0; cat empty; ls; echo ---; cat name; echo; cat unicode; echo; cat dir/file.txt; echo; "
                + "stat -c '%s %F' name; (echo x > name) 2>&1 || true; cd /; "
                + "mount -t 9p -o trans=tcp,port={0},version={1},access=any,uname=ninep,msize=4096 {2} /mnt/ninep-interop-4k; "
                + "trap 'cd /; umount /mnt/ninep-interop; umount /mnt/ninep-interop-4k' EXIT; "
                + "cd /mnt/ninep-interop-4k; stat -c '%s' chunked; cat chunked; echo; grep ' /mnt/ninep-interop-4k ' /proc/mounts; cd /",
                port, version, host);
            string output = await RunAsync("limactl", ["shell", vm, "--", "sudo", "sh", "-c", script]);
            string[] lines = Lines(output);

            int separator = Array.IndexOf(lines, "---");
            Assert.True(separator > 0, output);
            Assert.Equal(["chunked", "dir", "empty", "list", "name", "unicode"], lines[..separator].Order(StringComparer.Ordinal));
            Assert.Equal("conformance", lines[separator + 1]);
            Assert.Equal(Unicode, lines[separator + 2]);
            Assert.Equal("content of file.txt", lines[separator + 3]);
            Assert.Equal("11 regular file", lines[separator + 4]);
            Assert.Contains("name", lines[separator + 5], StringComparison.Ordinal);
            Assert.Equal(ChunkedLength.ToString(CultureInfo.InvariantCulture), lines[separator + 6]);
            Assert.Equal(Chunked, lines[separator + 7]);
            Assert.Contains("msize=4096", lines[separator + 8], StringComparison.Ordinal);
        }
        finally
        {
            Stop(server);
            File.Delete(document);
        }
    }

    private static string Peer(string variable, string howToGetOne)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(value))
        {
            Assert.Skip($"interop: set {variable} to {howToGetOne}; see tests/interop/README.md");
        }

        return value;
    }

    private static string Tree()
    {
        string root = Path.Combine(Path.GetTempPath(), "ninep-interop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "dir", "sub"));
        File.WriteAllText(Path.Combine(root, "name"), "conformance");
        File.WriteAllText(Path.Combine(root, "unicode"), Unicode);
        File.WriteAllText(Path.Combine(root, "empty"), string.Empty);
        File.WriteAllText(Path.Combine(root, "dir", "file.txt"), "content of file.txt");
        File.WriteAllBytes(Path.Combine(root, "chunked"), ChunkedBytes);
        return root;
    }

    private static string BuildChunked()
    {
        StringBuilder text = new(ChunkedLength);
        for (int offset = 0; offset < ChunkedLength; offset += 8)
        {
            text.Append(offset.ToString("D8", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Task<CliRun> Cli(string addr, string dialect, params string[] arguments) => Cli(addr, dialect, arguments, null);

    private static async Task<CliRun> Cli(string addr, string dialect, string[] arguments, byte[]? stdin)
    {
        CliRun run = await CliHarness.RunRawAsync([.. new[] { "--addr", addr, "--dialect", dialect }, .. arguments], stdin);
        return run.Expect(0);
    }

    private static string[] Lines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static int FreePort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static async Task WaitForPortAsync(int port)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            try
            {
                using TcpClient probe = new();
                await probe.ConnectAsync(IPAddress.Loopback, port, Ct);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50, Ct);
            }
        }

        Assert.Fail($"nothing listened on 127.0.0.1:{port} within ten seconds");
    }

    private static Process StartProcess(string file, string[] arguments)
    {
        ProcessStartInfo info = new(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new InvalidOperationException(file + " did not start");
    }

    private static void Stop(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }

    private static async Task<string> RunAsync(
        string file, string[] arguments, IReadOnlyDictionary<string, string>? environment = null, bool allowFailure = false)
    {
        ProcessStartInfo info = new(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach ((string name, string value) in environment ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            info.Environment[name] = value;
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException(file + " did not start");
        Task<string> stderr = process.StandardError.ReadToEndAsync(Ct);
        string stdout = await process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        string errors = await stderr;

        Assert.True(allowFailure || process.ExitCode == 0, $"{file} {string.Join(' ', arguments)} exited {process.ExitCode}: {errors}");
        return stdout;
    }
}
#endif
