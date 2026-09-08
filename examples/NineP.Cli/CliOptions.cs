using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Protocol.Transports;

namespace NineP.Cli;

/// <summary>The parsed command line of <c>ninep</c> (spec §8.2).</summary>
internal sealed record CliOptions
{
    /// <summary>The usage text printed when the command line is wrong.</summary>
    public const string Usage = """
        usage: ninep [--addr <url>] [--dialect 9P2000|9P2000.u|9P2000.L] [--uname <u>]
                     [--aname <a>] [--auth none|token:<secret>|bearer:<token>|oidc-device|oidc-password]
                     [--auth-optional] [--oidc-issuer <url>] [--oidc-client-id <id>]
                     [--msize <bytes>] [--tls-ca <pem>] [--tls-cert <pem> --tls-key <pem>]
                     <command> [args]

        commands: version | ls [-l] PATH | cat PATH | stat PATH | write PATH
                  mkdir PATH | rm PATH | mv OLD NEW | readlink PATH

        ls prints one entry per line, sorted bytewise by name; -l prefixes each with
        "<dir|file|symlink> <size in bytes> ", the size being the entry's own. -l belongs to
        ls alone, the --tls-* flags need a tls:// or wss:// --addr, and --oidc-* need --auth
        oidc-device or oidc-password: a flag that cannot apply is a usage error, not a flag
        that is read and then ignored.
        """;

    /// <summary>The server to talk to.</summary>
    public NinePAddress Address { get; init; } = new(NinePScheme.Tcp, "127.0.0.1", 564, string.Empty);

    /// <summary>The dialect to offer; null offers all three, most capable first.</summary>
    public Dialect? Dialect { get; init; }

    /// <summary>
    /// The user name sent in <c>Tauth</c> and <c>Tattach</c>. It defaults to the local user
    /// rather than to the empty string, because a server owns an attached tree by the attaching
    /// user and an anonymous attach owns nothing — which is how plan9port's own <c>9p</c>
    /// behaves too.
    /// </summary>
    public string Uname { get; init; } = LocalUser();

    /// <summary>The tree name sent in <c>Tauth</c> and <c>Tattach</c>.</summary>
    public string Aname { get; init; } = string.Empty;

    /// <summary>
    /// What <c>--auth</c> asked for: <c>none</c>, <c>token:&lt;secret&gt;</c>,
    /// <c>bearer:&lt;token&gt;</c>, <c>oidc-device</c> or <c>oidc-password</c>. The credential
    /// itself is built at connect time, because two of the five have to talk to an issuer first.
    /// </summary>
    public string Auth { get; init; } = "none";

    /// <summary>The OIDC issuer the two grants discover their endpoints from.</summary>
    public string OidcIssuer { get; init; } = string.Empty;

    /// <summary>The OIDC client id the two grants present.</summary>
    public string OidcClientId { get; init; } = string.Empty;

    /// <summary>True to fall back to a <c>NOFID</c> attach when the server refuses <c>Tauth</c>.</summary>
    public bool AuthOptional { get; init; }

    /// <summary>The msize to ask for; null takes the client's default.</summary>
    public uint? Msize { get; init; }

    /// <summary>Extra roots the server certificate is validated against.</summary>
    public X509Certificate2Collection? TrustedRoots { get; init; }

    /// <summary>The client certificate offered for mutual TLS.</summary>
    public X509Certificate2? ClientCertificate { get; init; }

    /// <summary>The command to run.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>The command's positional arguments.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>True when <c>ls</c> was given <c>-l</c>.</summary>
    public bool LongListing { get; init; }

    /// <summary>Parses a command line.</summary>
    /// <param name="args">The arguments, without the program name.</param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="CliUsageException">The command line is not one ninep will act on.</exception>
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        CliOptions options = new();
        List<string> positional = [];
        string? caPath = null;
        string? certificatePath = null;
        string? keyPath = null;
        bool longListing = false;
        bool oidcIssuer = false;
        bool oidcClientId = false;

        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--addr":
                    options = options with { Address = ParseAddress(Value(args, ref i)) };
                    break;
                case "--dialect":
                    options = options with { Dialect = ParseDialect(Value(args, ref i)) };
                    break;
                case "--uname":
                    options = options with { Uname = Value(args, ref i) };
                    break;
                case "--aname":
                    options = options with { Aname = Value(args, ref i) };
                    break;
                case "--auth":
                    options = options with { Auth = ParseAuth(Value(args, ref i)) };
                    break;
                case "--oidc-issuer":
                    options = options with { OidcIssuer = Value(args, ref i) };
                    oidcIssuer = true;
                    break;
                case "--oidc-client-id":
                    options = options with { OidcClientId = Value(args, ref i) };
                    oidcClientId = true;
                    break;
                case "--auth-optional":
                    options = options with { AuthOptional = true };
                    break;
                case "--msize":
                    options = options with { Msize = ParseMsize(Value(args, ref i)) };
                    break;
                case "--tls-ca":
                    caPath = Value(args, ref i);
                    break;
                case "--tls-cert":
                    certificatePath = Value(args, ref i);
                    break;
                case "--tls-key":
                    keyPath = Value(args, ref i);
                    break;
                case "-l":
                    longListing = true;
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliUsageException("unknown flag " + args[i]);
                    }

                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count == 0)
        {
            throw new CliUsageException("a command is required");
        }

        // The command and its arity are checked before anything is dialled, so a typo is exit 3
        // and not a connection failure that happens to be exit 1.
        RequireArity(positional[0], positional.Count - 1);

        // A flag that cannot apply to the rest of the command line is a mistake, not a default:
        // loading it and then never using it hides the mistake behind a run that looks like it
        // worked. Each of these used to be accepted and dropped.
        RequireLongListingIsForLs(longListing, positional[0]);
        RequireTlsFlagsMatchTheAddress(options.Address, caPath, certificatePath, keyPath);
        RequireOidcFlagsMatchTheGrant(options.Auth, oidcIssuer, oidcClientId);

        return options with
        {
            Command = positional[0],
            Arguments = positional.Skip(1).ToArray(),
            LongListing = longListing,
            TrustedRoots = Roots(caPath),
            ClientCertificate = Certificate(certificatePath, keyPath),
        };
    }

    /// <summary><c>-l</c> is a flag of <c>ls</c>; on any other command it means nothing.</summary>
    /// <param name="longListing">True when <c>-l</c> was given.</param>
    /// <param name="command">The command it was given with.</param>
    /// <exception cref="CliUsageException">The command is not <c>ls</c>.</exception>
    private static void RequireLongListingIsForLs(bool longListing, string command)
    {
        if (longListing && command != "ls")
        {
            throw new CliUsageException("-l is a flag of ls, not of " + command);
        }
    }

    /// <summary>
    /// The TLS flags configure a TLS handshake, and a <c>tcp://</c> or <c>ws://</c> address never
    /// performs one. Reading a certificate and then not using it is how a connection a caller
    /// believed was encrypted turns out not to be, so the mismatch is a usage error.
    /// </summary>
    /// <param name="address">The address that was given, or the default.</param>
    /// <param name="caPath">What <c>--tls-ca</c> carried, or null.</param>
    /// <param name="certificatePath">What <c>--tls-cert</c> carried, or null.</param>
    /// <param name="keyPath">What <c>--tls-key</c> carried, or null.</param>
    /// <exception cref="CliUsageException">A TLS flag was given with a plaintext address.</exception>
    private static void RequireTlsFlagsMatchTheAddress(
        NinePAddress address, string? caPath, string? certificatePath, string? keyPath)
    {
        if (address.Scheme is NinePScheme.Tls or NinePScheme.Wss)
        {
            return;
        }

        string? flag = caPath is not null
            ? "--tls-ca"
            : certificatePath is not null
                ? "--tls-cert"
                : keyPath is not null ? "--tls-key" : null;

        if (flag is null)
        {
            return;
        }

        throw new CliUsageException(string.Format(
            CultureInfo.InvariantCulture,
            "{0} needs a tls:// or wss:// address; --addr is {1}://",
            flag,
            SchemeName(address.Scheme)));
    }

    /// <summary>
    /// <c>--oidc-issuer</c> and <c>--oidc-client-id</c> configure the two grants and nothing else.
    /// With any other <c>--auth</c> they are read and never used, which reads as a login that was
    /// configured when none was performed.
    /// </summary>
    /// <param name="auth">What <c>--auth</c> asked for.</param>
    /// <param name="issuer">True when <c>--oidc-issuer</c> was given.</param>
    /// <param name="clientId">True when <c>--oidc-client-id</c> was given.</param>
    /// <exception cref="CliUsageException">An OIDC flag was given without an OIDC grant.</exception>
    private static void RequireOidcFlagsMatchTheGrant(string auth, bool issuer, bool clientId)
    {
        if (auth is "oidc-device" or "oidc-password" || (!issuer && !clientId))
        {
            return;
        }

        throw new CliUsageException(string.Format(
            CultureInfo.InvariantCulture,
            "{0} is only for --auth oidc-device or oidc-password; --auth is {1}",
            issuer ? "--oidc-issuer" : "--oidc-client-id",
            auth));
    }

    /// <summary>The scheme as it is spelled in an address.</summary>
    /// <param name="scheme">The scheme to name.</param>
    /// <returns>The lower-case scheme text.</returns>
    private static string SchemeName(NinePScheme scheme) => scheme switch
    {
        NinePScheme.Tls => "tls",
        NinePScheme.Ws => "ws",
        NinePScheme.Wss => "wss",
        NinePScheme.Unix => "unix",
        NinePScheme.Memory => "memory",
        _ => "tcp",
    };

    /// <summary>The number of paths each command takes.</summary>
    /// <param name="command">The command name.</param>
    /// <param name="given">How many positional arguments followed it.</param>
    /// <exception cref="CliUsageException">The command is unknown or its arity is wrong.</exception>
    private static void RequireArity(string command, int given)
    {
        int wanted = command switch
        {
            "version" => 0,
            "ls" or "cat" or "stat" or "write" or "mkdir" or "rm" or "readlink" => 1,
            "mv" => 2,
            _ => throw new CliUsageException("unknown command " + command),
        };

        if (given != wanted)
        {
            throw new CliUsageException(string.Format(
                CultureInfo.InvariantCulture, "{0} takes {1} argument(s), not {2}", command, wanted, given));
        }
    }

    /// <summary>The local user name, or "none" where the platform reports none.</summary>
    /// <returns>The default <c>uname</c>.</returns>
    private static string LocalUser()
    {
        string user = Environment.UserName;
        return user.Length == 0 ? "none" : user;
    }

    private static string Value(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count)
        {
            throw new CliUsageException(args[i] + " needs a value");
        }

        i++;
        return args[i];
    }

    private static NinePAddress ParseAddress(string value) =>
        NinePAddress.TryParse(value, out NinePAddress address)
            ? address
            : throw new CliUsageException("not a 9P address: " + value);

    private static Dialect ParseDialect(string value) => value switch
    {
        "9P2000" => Protocol.Dialect.P9_2000,
        "9P2000.u" => Protocol.Dialect.P9_2000_u,
        "9P2000.L" => Protocol.Dialect.P9_2000_L,
        _ => throw new CliUsageException("unknown dialect " + value),
    };

    /// <summary>Checks the <c>--auth</c> value without acting on it.</summary>
    /// <param name="value">What the flag carried.</param>
    /// <returns>The same value, once it is one the cli knows.</returns>
    /// <exception cref="CliUsageException">The value names no grant.</exception>
    private static string ParseAuth(string value) =>
        value is "none" or "oidc-device" or "oidc-password"
        || value.StartsWith("token:", StringComparison.Ordinal)
        || value.StartsWith("bearer:", StringComparison.Ordinal)
            ? value
            : throw new CliUsageException(
                "--auth takes none, token:<secret>, bearer:<token>, oidc-device or oidc-password");

    private static uint ParseMsize(string value) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint msize)
            ? msize
            : throw new CliUsageException("--msize takes a byte count");

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

    private static X509Certificate2? Certificate(string? certificatePath, string? keyPath)
    {
        if (certificatePath is null && keyPath is null)
        {
            return null;
        }

        if (certificatePath is null || keyPath is null)
        {
            throw new CliUsageException("--tls-cert and --tls-key are given together");
        }

        try
        {
            using X509Certificate2 loaded = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
            return X509CertificateLoader.LoadPkcs12(loaded.Export(X509ContentType.Pkcs12), password: null);
        }
        catch (Exception failure) when (failure is IOException or CryptographicException)
        {
            throw new CliUsageException("could not load --tls-cert / --tls-key: " + failure.Message, failure);
        }
    }
}
