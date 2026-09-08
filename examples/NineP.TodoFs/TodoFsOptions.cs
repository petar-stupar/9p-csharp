using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using NineP.Protocol;
using NineP.Protocol.Transports;

namespace NineP.TodoFs;

/// <summary>The parsed command line of <c>todofs</c> (spec §8.3).</summary>
internal sealed record TodoFsOptions
{
    /// <summary>The usage text printed when the command line is wrong.</summary>
    public const string Usage = """
        usage: todofs --listen <url>... --db <path.sqlite> --oidc-issuer <url>
                      --oidc-audience <aud> [--admin-role todofs-admin] [--jwks-cache <secs>]
                      [--allow-insecure-issuer]
                      [--dialects 9P2000,9P2000.u,9P2000.L]
                      [--tls-cert <pem> --tls-key <pem> --tls-client-ca <pem>] [--log <level>]

        --allow-insecure-issuer  fetch the realm's discovery and JWKS documents over plain HTTP;
                               for a loopback development issuer only
        """;

    /// <summary>Addresses to bind; at least one is required.</summary>
    public IReadOnlyList<NinePAddress> Listen { get; init; } = [];

    /// <summary>The SQLite database to serve.</summary>
    public string Database { get; init; } = string.Empty;

    /// <summary>The realm's issuer URL.</summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>The audience this server is.</summary>
    public string Audience { get; init; } = string.Empty;

    /// <summary>The realm role that may use <c>/users/ctl</c>.</summary>
    public string AdminRole { get; init; } = "todofs-admin";

    /// <summary>How long the realm's JWKS is reused.</summary>
    public TimeSpan JwksCache { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The dialects this server will negotiate.</summary>
    public IReadOnlySet<Dialect> Dialects { get; init; } =
        new HashSet<Dialect> { Dialect.P9_2000, Dialect.P9_2000_u, Dialect.P9_2000_L };

    /// <summary>The server certificate for a <c>tls://</c> or <c>wss://</c> listener.</summary>
    public X509Certificate2? ServerCertificate { get; init; }

    /// <summary>Extra roots a client certificate is validated against.</summary>
    public X509Certificate2Collection? ClientCertificateAuthority { get; init; }

    /// <summary>The level at and above which the server logs; null logs nothing.</summary>
    public LogLevel? MinimumLogLevel { get; init; } = LogLevel.Warning;

    /// <summary>
    /// Whether the realm's discovery and JWKS documents must be fetched over HTTPS. It is true in
    /// every deployment. The one case that needs it off is a development issuer on loopback — the
    /// repository's own fake issuer serves plain HTTP there (E-4) — and
    /// <c>--allow-insecure-issuer</c> is the flag that turns it off. Off, the realm's keys can be
    /// substituted by anything on the path, so every token this server accepts is forgeable.
    /// </summary>
    public bool RequireHttps { get; init; } = true;

    /// <summary>Parses a command line.</summary>
    /// <param name="args">The arguments, without the program name.</param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="TodoFsUsageException">The command line is not one todofs will act on.</exception>
    public static TodoFsOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        List<NinePAddress> listen = [];
        TodoFsOptions options = new();
        string? certificatePath = null;
        string? keyPath = null;
        string? clientCaPath = null;

        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--listen":
                    listen.Add(ParseAddress(Value(args, ref i)));
                    break;
                case "--db":
                    options = options with { Database = Value(args, ref i) };
                    break;
                case "--oidc-issuer":
                    options = options with { Issuer = Value(args, ref i) };
                    break;
                case "--oidc-audience":
                    options = options with { Audience = Value(args, ref i) };
                    break;
                case "--admin-role":
                    options = options with { AdminRole = Value(args, ref i) };
                    break;
                case "--jwks-cache":
                    options = options with { JwksCache = ParseSeconds(Value(args, ref i)) };
                    break;
                case "--allow-insecure-issuer":
                    options = options with { RequireHttps = false };
                    break;
                case "--dialects":
                    options = options with { Dialects = ParseDialects(Value(args, ref i)) };
                    break;
                case "--tls-cert":
                    certificatePath = Value(args, ref i);
                    break;
                case "--tls-key":
                    keyPath = Value(args, ref i);
                    break;
                case "--tls-client-ca":
                    clientCaPath = Value(args, ref i);
                    break;
                case "--log":
                    options = options with { MinimumLogLevel = ParseLevel(Value(args, ref i)) };
                    break;
                default:
                    throw new TodoFsUsageException("unknown flag " + args[i]);
            }
        }

        return Validated(options with
        {
            Listen = listen,
            ServerCertificate = Certificate(certificatePath, keyPath),
            ClientCertificateAuthority = Roots(clientCaPath),
        });
    }

    private static TodoFsOptions Validated(TodoFsOptions options)
    {
        if (options.Listen.Count == 0)
        {
            throw new TodoFsUsageException("--listen is required");
        }

        foreach ((string flag, string value) in new[]
        {
            ("--db", options.Database),
            ("--oidc-issuer", options.Issuer),
            ("--oidc-audience", options.Audience),
        })
        {
            if (value.Length == 0)
            {
                throw new TodoFsUsageException(flag + " is required");
            }
        }

        return options;
    }

    private static string Value(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count)
        {
            throw new TodoFsUsageException(args[i] + " needs a value");
        }

        i++;
        return args[i];
    }

    private static NinePAddress ParseAddress(string value) =>
        NinePAddress.TryParse(value, out NinePAddress address)
            ? address
            : throw new TodoFsUsageException("not a 9P address: " + value);

    private static TimeSpan ParseSeconds(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
            ? TimeSpan.FromSeconds(seconds)
            : throw new TodoFsUsageException("--jwks-cache takes a number of seconds");

    private static HashSet<Dialect> ParseDialects(string value)
    {
        HashSet<Dialect> dialects = [];
        foreach (string name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            dialects.Add(name switch
            {
                "9P2000" => Dialect.P9_2000,
                "9P2000.u" => Dialect.P9_2000_u,
                "9P2000.L" => Dialect.P9_2000_L,
                _ => throw new TodoFsUsageException("unknown dialect " + name),
            });
        }

        return dialects.Count > 0 ? dialects : throw new TodoFsUsageException("--dialects names none");
    }

    private static LogLevel? ParseLevel(string value) => value switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Information,
        "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "none" => null,
        _ => throw new TodoFsUsageException("--log takes trace, debug, info, warn, error or none"),
    };

    private static X509Certificate2? Certificate(string? certificatePath, string? keyPath)
    {
        if (certificatePath is null && keyPath is null)
        {
            return null;
        }

        if (certificatePath is null || keyPath is null)
        {
            throw new TodoFsUsageException("--tls-cert and --tls-key are given together");
        }

        try
        {
            using X509Certificate2 loaded = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
            return X509CertificateLoader.LoadPkcs12(loaded.Export(X509ContentType.Pkcs12), password: null);
        }
        catch (Exception failure) when (failure is IOException or CryptographicException)
        {
            throw new TodoFsUsageException("could not load --tls-cert / --tls-key: " + failure.Message, failure);
        }
    }

    private static X509Certificate2Collection? Roots(string? path)
    {
        if (path is null)
        {
            return null;
        }

        X509Certificate2Collection roots = [];
        roots.ImportFromPemFile(path);
        return roots;
    }
}
