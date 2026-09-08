using NineP.Protocol.Auth;

namespace NineP.Cli.Oidc;

/// <summary>Turns what <c>--auth</c> asked for into the credential the attach will run.</summary>
internal static class CliCredentials
{
    /// <summary>The environment variable the password grant reads when there is no terminal.</summary>
    public const string PasswordVariable = "NINEP_PASSWORD";

    /// <summary>Builds the credential, running an OIDC grant first when one was asked for.</summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="http">The client the grants talk to the issuer with.</param>
    /// <param name="prompt">Where a grant prints its prompts and warnings.</param>
    /// <param name="clock">The clock a grant's polling is measured on.</param>
    /// <param name="cancellationToken">Cancels the grant.</param>
    /// <returns>The credential, or null for a <c>NOFID</c> attach.</returns>
    /// <exception cref="CliUsageException">A grant was asked for without what it needs.</exception>
    /// <exception cref="OidcException">The grant did not complete.</exception>
    public static async Task<ICredential?> CreateAsync(
        CliOptions options,
        HttpClient http,
        TextWriter prompt,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Auth == "none")
        {
            return null;
        }

        if (options.Auth.StartsWith("token:", StringComparison.Ordinal))
        {
            return new TokenCredential(CliText.Utf8.GetBytes(options.Auth["token:".Length..]));
        }

        if (options.Auth.StartsWith("bearer:", StringComparison.Ordinal))
        {
            return new BearerTokenCredential(options.Auth["bearer:".Length..]);
        }

        string issuer = Require(options.OidcIssuer, "--oidc-issuer");
        string clientId = Require(options.OidcClientId, "--oidc-client-id");

        // The token is fetched once, here, and lives in this process for its lifetime; nothing is
        // written to disk and nothing is logged.
        string token = options.Auth == "oidc-device"
            ? await OidcGrants.DeviceAsync(http, issuer, clientId, prompt, clock, cancellationToken)
                .ConfigureAwait(false)
            : await OidcGrants.PasswordAsync(
                http, issuer, clientId, options.Uname, Password(), prompt, cancellationToken)
                .ConfigureAwait(false);

        return new BearerTokenCredential(token);
    }

    /// <summary>
    /// The password for the resource-owner grant: the environment first, then the terminal. It is
    /// never taken from the command line, where it would reach the process table and the shell's
    /// history.
    /// </summary>
    /// <returns>The password.</returns>
    /// <exception cref="CliUsageException">There is neither a variable nor a terminal.</exception>
    private static string Password()
    {
        if (Environment.GetEnvironmentVariable(PasswordVariable) is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        if (Console.IsInputRedirected)
        {
            throw new CliUsageException(
                "the password grant needs a terminal or the " + PasswordVariable + " environment variable");
        }

        Console.Error.Write("password: ");
        return ReadHidden();
    }

    private static string ReadHidden()
    {
        System.Text.StringBuilder password = new();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return password.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }
    }

    private static string Require(string value, string flag) =>
        value.Length > 0 ? value : throw new CliUsageException(flag + " is required by this grant");
}
