using NineP.Cli;
using NineP.Cli.Oidc;
using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Transports;

// ninep: the conformance client. Exit codes are frozen by docs/9p/fixtures/conformance.md —
// 0 success, 1 protocol or transport error, 2 server error (Rerror/Rlerror), 3 usage.
return await CliProgram.RunAsync(args);

/// <summary>The cli's entry point, factored out so that the tests can drive it in process.</summary>
internal static class CliProgram
{
    /// <summary>The line the fallback of <c>--auth-optional</c> prints, on standard error.</summary>
    private const string AnonymousNotice =
        "ninep: server requires no authentication; attached anonymously";

    /// <summary>Runs one invocation.</summary>
    /// <param name="args">The arguments, without the program name.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        await using Stream output = Console.OpenStandardOutput();

        try
        {
            CliOptions options = CliOptions.Parse(args);

            using HttpClient http = new();
            ICredential? credential = await CliCredentials
                .CreateAsync(options, http, Console.Error, TimeProvider.System, CancellationToken.None)
                .ConfigureAwait(false);

            await using NinePSession session =
                await ConnectAsync(options, credential).ConfigureAwait(false);
            await AttachAsync(session, options).ConfigureAwait(false);
            await using Stream input = Console.OpenStandardInput();
            await CliCommands.RunAsync(session, options, output, input, CancellationToken.None)
                .ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            return 0;
        }
        catch (OidcException failure)
        {
            // A grant that did not complete is a transport-level failure: nothing was attached, so
            // there is no Rerror to report.
            await Console.Error.WriteLineAsync("ninep: " + failure.Message).ConfigureAwait(false);
            return 1;
        }
        catch (Exception failure) when (CliCommands.ExitCodeFor(failure) is int code)
        {
            await ReportAsync(failure, code).ConfigureAwait(false);
            return code;
        }

        // CA1031: this is a program's entry point, and the contract of
        // docs/9p/fixtures/conformance.md is four exit codes and one line on standard error. A
        // runtime that prints a dozen frames and aborts with 134 satisfies none of it, leaks the
        // absolute paths of the machine that built the binary, and tells a user nothing they can
        // act on. The type name is kept because an exception that reaches here is one this program
        // did not anticipate, and the name is what makes it reportable.
#pragma warning disable CA1031
        catch (Exception failure)
#pragma warning restore CA1031
        {
            await Console.Error
                .WriteLineAsync("ninep: " + failure.GetType().FullName + ": " + failure.Message)
                .ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task ReportAsync(Exception failure, int code)
    {
        if (failure is NinePException { } refusal and not NinePProtocolException and not NinePVersionException)
        {
            await Console.Error.WriteLineAsync(CliCommands.FormatError(refusal.Error)).ConfigureAwait(false);
            return;
        }

        await Console.Error.WriteLineAsync("ninep: " + failure.Message).ConfigureAwait(false);

        if (code == 3)
        {
            await Console.Error.WriteLineAsync(CliOptions.Usage).ConfigureAwait(false);
        }
    }

    private static ValueTask<NinePSession> ConnectAsync(CliOptions options, ICredential? credential)
    {
        ClientOptions client = new()
        {
            Dialects = options.Dialect is Dialect only
                ? [only]
                : [Dialect.P9_2000_L, Dialect.P9_2000_u, Dialect.P9_2000],
            Msize = options.Msize,
            Uname = options.Uname,
            Aname = options.Aname,
            Credential = credential,
        };

        TlsTransportOptions tls = new()
        {
            TrustedRoots = options.TrustedRoots,
            ClientCertificates = options.ClientCertificate is null ? null : [options.ClientCertificate],
        };

        ITransport transport = options.Address.Scheme switch
        {
            NinePScheme.Tls => new TlsTransport(tls),
            NinePScheme.Ws or NinePScheme.Wss => new WebSocketTransport(new WebSocketTransportOptions { Tls = tls }),
            _ => new TcpTransport(),
        };

        return NinePClient.ConnectAsync(transport, options.Address, client);
    }

    private static async Task AttachAsync(NinePSession session, CliOptions options)
    {
        try
        {
            await session.AttachAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (NinePException failure) when (options.AuthOptional && IsAuthRefusal(failure.Error))
        {
            // Conformance Part C.2: --auth-optional means "authenticate if the server offers it",
            // so a refused Tauth falls back to a NOFID attach instead of ending the run. The
            // fallback says so: a credential that was presented and then dropped is exactly the
            // case where a caller believes the session is authenticated and it is not. It goes to
            // standard error, so the output formats the conformance fixture freezes are untouched.
            await Console.Error.WriteLineAsync(AnonymousNotice).ConfigureAwait(false);
            await session.AttachAsync(options.Uname, options.Aname, credential: null, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static bool IsAuthRefusal(NinePError error) =>
        error.Errno == Errno.ECONNREFUSED
        || string.Equals(error.Ename, "authentication not required", StringComparison.Ordinal);
}
