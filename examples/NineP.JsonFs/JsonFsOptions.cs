using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Transports;

namespace NineP.JsonFs;

/// <summary>The parsed command line of <c>jsonfs</c> (spec §8.1).</summary>
internal sealed record JsonFsOptions
{
    /// <summary>The usage text printed when the command line is wrong.</summary>
    public const string Usage = """
        usage: jsonfs --listen <url> [--listen <url>...] --file <path.json> [--writable]
                      [--write-back] [--write-back-delay <ms>] [--max-entries <n>]
                      [--dialects 9P2000,9P2000.u,9P2000.L]
                      [--auth none|token:<secret>|password-file:<path>]
                      [--tls-cert <pem> --tls-key <pem> --tls-client-ca <pem>]
                      [--ws-origin <origin>...] [--msize <bytes>] [--log <level>]

        --write-back implies --writable: there is nothing to write back from a read-only
                     server, so the flag turns writing on rather than being ignored without it.
        --write-back-delay implies --write-back for the same reason: a window with nothing to
                     write back at the end of it is a flag that does nothing. 0 (the default)
                     rewrites the document inside every change; a positive value rewrites once
                     per window, and a graceful stop writes back what the last window still owes.
        --max-entries bounds the entries (object keys and array elements) the document may
                     hold, 100000 by default: a create or mkdir past it is refused with ENOSPC,
                     and a document already past it is refused at startup.
        """;

    /// <summary>Addresses to bind; at least one is required.</summary>
    public IReadOnlyList<NinePAddress> Listen { get; init; } = [];

    /// <summary>The document to serve.</summary>
    public string File { get; init; } = string.Empty;

    /// <summary>True to accept writes, creates, removes and renames.</summary>
    public bool Writable { get; init; }

    /// <summary>
    /// True to rewrite the document after every change. It implies <see cref="Writable"/>: a
    /// read-only server has no change to write back, so the flag turns writing on rather than
    /// being silently inert without <c>--writable</c> beside it.
    /// </summary>
    public bool WriteBack { get; init; }

    /// <summary>
    /// How long after a change the document is rewritten, with every change inside that window
    /// coalesced into one rewrite. Zero, the default, rewrites inside each change. A positive
    /// value implies <see cref="WriteBack"/>, and through it <see cref="Writable"/>.
    /// </summary>
    public TimeSpan WriteBackDelay { get; init; }

    /// <summary>
    /// The most entries — object keys and array elements, anywhere in the document — the served
    /// document may hold. A create or <c>mkdir</c> past it is <c>ENOSPC</c>; a document already
    /// past it is refused at startup.
    /// </summary>
    public long MaxEntries { get; init; } = JsonTree.DefaultMaxEntries;

    /// <summary>The dialects this server will negotiate.</summary>
    public IReadOnlySet<Dialect> Dialects { get; init; } =
        new HashSet<Dialect> { Dialect.P9_2000, Dialect.P9_2000_u, Dialect.P9_2000_L };

    /// <summary>The authenticator the <c>--auth</c> flag asked for; null refuses <c>Tauth</c>.</summary>
    public IAuthenticator? Authenticator { get; init; }

    /// <summary>The server certificate for a <c>tls://</c> or <c>wss://</c> listener.</summary>
    public X509Certificate2? ServerCertificate { get; init; }

    /// <summary>Extra roots a client certificate is validated against.</summary>
    public X509Certificate2Collection? ClientCertificateAuthority { get; init; }

    /// <summary>Origins a WebSocket upgrade may carry; empty accepts any.</summary>
    public IReadOnlyList<string> WebSocketOrigins { get; init; } = [];

    /// <summary>The largest msize this server will negotiate.</summary>
    public uint Msize { get; init; } = Limits.Default.MaxMsize;

    /// <summary>The level at and above which the server logs to standard error; null logs nothing.</summary>
    public LogLevel? MinimumLogLevel { get; init; } = LogLevel.Warning;

    /// <summary>Parses a command line.</summary>
    /// <param name="args">The arguments, without the program name.</param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="JsonFsUsageException">The command line is not one jsonfs will act on.</exception>
    public static JsonFsOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        List<NinePAddress> listen = [];
        List<string> origins = [];
        JsonFsOptions options = new();
        string? certificatePath = null;
        string? keyPath = null;
        string? clientCaPath = null;

        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--listen":
                    listen.Add(Address(Value(args, ref i)));
                    break;
                case "--file":
                    options = options with { File = Value(args, ref i) };
                    break;
                case "--writable":
                    options = options with { Writable = true };
                    break;
                case "--write-back":
                    // Documented in the usage text above and in docs/examples.md: --write-back
                    // implies --writable, because a read-only server has nothing to write back.
                    options = options with { WriteBack = true, Writable = true };
                    break;
                case "--write-back-delay":
                    // Same reasoning one step further: a window is only ever measured for a
                    // write-back, so the flag turns write-back (and with it writing) on.
                    options = options with
                    {
                        WriteBackDelay = ParseWriteBackDelay(Value(args, ref i)),
                        WriteBack = true,
                        Writable = true,
                    };
                    break;
                case "--max-entries":
                    options = options with { MaxEntries = ParseMaxEntries(Value(args, ref i)) };
                    break;
                case "--dialects":
                    options = options with { Dialects = ParseDialects(Value(args, ref i)) };
                    break;
                case "--auth":
                    options = options with { Authenticator = ParseAuthenticator(Value(args, ref i)) };
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
                case "--ws-origin":
                    origins.Add(Value(args, ref i));
                    break;
                case "--msize":
                    options = options with { Msize = ParseMsize(Value(args, ref i)) };
                    break;
                case "--log":
                    options = options with { MinimumLogLevel = Level(Value(args, ref i)) };
                    break;
                default:
                    throw new JsonFsUsageException("unknown flag " + args[i]);
            }
        }

        if (listen.Count == 0)
        {
            throw new JsonFsUsageException("--listen is required");
        }

        if (options.File.Length == 0)
        {
            throw new JsonFsUsageException("--file is required");
        }

        return options with
        {
            Listen = listen,
            WebSocketOrigins = origins,
            ServerCertificate = Certificate(certificatePath, keyPath),
            ClientCertificateAuthority = Roots(clientCaPath),
        };
    }

    private static string Value(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count)
        {
            throw new JsonFsUsageException(args[i] + " needs a value");
        }

        i++;
        return args[i];
    }

    private static NinePAddress Address(string value) =>
        NinePAddress.TryParse(value, out NinePAddress address)
            ? address
            : throw new JsonFsUsageException("not a 9P address: " + value);

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
                _ => throw new JsonFsUsageException("unknown dialect " + name),
            });
        }

        return dialects.Count > 0 ? dialects : throw new JsonFsUsageException("--dialects names none");
    }

    private static IAuthenticator? ParseAuthenticator(string value)
    {
        if (value == "none")
        {
            return null;
        }

        if (value.StartsWith("token:", StringComparison.Ordinal))
        {
            return new TokenAuthenticator(JsonText.ToBytes(value["token:".Length..]));
        }

        if (value.StartsWith("password-file:", StringComparison.Ordinal))
        {
            return new PasswordAuthenticator(PasswordFileStore.Load(value["password-file:".Length..]));
        }

        throw new JsonFsUsageException("--auth takes none, token:<secret> or password-file:<path>");
    }

    private static uint ParseMsize(string value) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint msize)
            ? msize
            : throw new JsonFsUsageException("--msize takes a byte count");

    private static TimeSpan ParseWriteBackDelay(string value) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint milliseconds)
            ? TimeSpan.FromMilliseconds(milliseconds)
            : throw new JsonFsUsageException("--write-back-delay takes a whole number of milliseconds");

    private static long ParseMaxEntries(string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long entries) && entries > 0
            ? entries
            : throw new JsonFsUsageException("--max-entries takes a positive entry count");

    private static LogLevel? Level(string value) => value switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Information,
        "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "none" => null,
        _ => throw new JsonFsUsageException("--log takes trace, debug, info, warn, error or none"),
    };

    private static X509Certificate2? Certificate(string? certificatePath, string? keyPath)
    {
        if (certificatePath is null && keyPath is null)
        {
            return null;
        }

        if (certificatePath is null || keyPath is null)
        {
            throw new JsonFsUsageException("--tls-cert and --tls-key are given together");
        }

        try
        {
            using X509Certificate2 loaded = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);

            // On Windows a certificate built from PEM cannot be used for TLS until it has been
            // round-tripped through a PKCS#12 blob; doing it everywhere keeps one code path.
            return X509CertificateLoader.LoadPkcs12(loaded.Export(X509ContentType.Pkcs12), password: null);
        }
        catch (Exception failure) when (failure is IOException or System.Security.Cryptography.CryptographicException)
        {
            throw new JsonFsUsageException("could not load --tls-cert / --tls-key: " + failure.Message, failure);
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
