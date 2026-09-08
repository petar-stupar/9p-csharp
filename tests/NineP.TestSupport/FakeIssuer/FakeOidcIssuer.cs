using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;

namespace NineP.TestSupport.FakeIssuer;

/// <summary>
/// An in-process OpenID Connect issuer: discovery, JWKS, the RFC 8628 device grant and the token
/// endpoint, and nothing else (§8.4). It listens on <c>http://127.0.0.1:0/</c>, which is legal
/// here because only the <c>https://</c> prefix is unusable without a certificate (E-4).
/// </summary>
public sealed class FakeOidcIssuer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly FakeIssuerKeys _keys = new();
    private readonly ConcurrentDictionary<string, int> _hits = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _serving;

    private int _pendingLeft;

    private FakeOidcIssuer(HttpListener listener, string issuer)
    {
        _listener = listener;
        Issuer = issuer;
        _pendingLeft = PendingResponses;
        _serving = Task.Run(ServeAsync);
    }

    /// <summary>The issuer's URL, with no trailing slash.</summary>
    public string Issuer { get; }

    /// <summary>The audience tokens carry unless a test overrides it.</summary>
    public string Audience { get; set; } = "todofs";

    /// <summary>The client id the grants expect.</summary>
    public string ClientId { get; set; } = "todofs-cli";

    /// <summary>The user name the password grant accepts.</summary>
    public string PasswordUser { get; set; } = "glenda";

    /// <summary>The password the password grant accepts.</summary>
    public string Password { get; set; } = "hunter2";

    /// <summary>
    /// How many times the device grant answers <c>authorization_pending</c> before the single
    /// <c>slow_down</c> and then the token, so a test can drive the whole polling path quickly.
    /// </summary>
    public int PendingResponses { get; set; } = 1;

    /// <summary>The interval the device grant tells the client to poll at, in seconds.</summary>
    public int PollIntervalSeconds { get; set; } = 1;

    /// <summary>The claims every issued token carries unless a test overrides them.</summary>
    public FakeTokenOptions Default { get; set; } = new();

    /// <summary>How many requests each path has received, so a stampede is countable.</summary>
    public IReadOnlyDictionary<string, int> Hits => _hits;

    /// <summary>The published RSA key's id.</summary>
    public string RsaKid => _keys.RsaKid;

    /// <summary>The published EC key's id.</summary>
    public string EcKid => _keys.EcKid;

    /// <summary>The discovery document's address.</summary>
    public Uri DiscoveryUri =>
        new(Issuer + "/.well-known/openid-configuration");

    /// <summary>Starts an issuer on a port the kernel chooses.</summary>
    /// <returns>The running issuer.</returns>
    public static FakeOidcIssuer Start()
    {
        // HttpListener cannot bind port 0, so a free port is found by letting the kernel pick one
        // for a socket and then handing that number to the listener.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            int port = FreePort();
            string prefix = string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/", port);
            HttpListener listener = new();
            listener.Prefixes.Add(prefix);

            try
            {
                listener.Start();
                return new FakeOidcIssuer(listener, prefix.TrimEnd('/'));
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }

        throw new InvalidOperationException("no free loopback port for the fake issuer");
    }

    /// <summary>Mints one token.</summary>
    /// <param name="options">What to vary; the issuer's defaults when null.</param>
    /// <returns>The compact serialisation.</returns>
    public string IssueToken(FakeTokenOptions? options = null) =>
        FakeToken.Build(options ?? Default, Issuer, Audience, _keys, DateTimeOffset.UtcNow);

    /// <summary>Stops the listener.</summary>
    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();

        try
        {
            _serving.GetAwaiter().GetResult();
        }
        catch (Exception failure) when (failure is OperationCanceledException or HttpListenerException
            or ObjectDisposedException)
        {
            // The listener was closed on purpose.
        }

        _stopping.Dispose();
        _keys.Dispose();
    }

    private static int FreePort()
    {
        using System.Net.Sockets.Socket probe = new(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);

        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                await AnswerAsync(context).ConfigureAwait(false);
            }

            // CA1031: the fixture must not take the test process down because one request went
            // wrong; the test sees the failure as a refused grant.
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Abort, not Close: a request that failed after its ContentLength64 was set has
                // fewer bytes written than promised, and http.sys on Windows refuses to close such
                // a response ("Cannot close stream until all bytes are written"). The managed
                // listener on Linux and macOS is lenient, which is why this never showed there.
                context.Response.Abort();
            }
        }
    }

    private async Task AnswerAsync(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath ?? "/";
        _hits.AddOrUpdate(path, 1, (_, count) => count + 1);

        (int status, string body) = path switch
        {
            "/.well-known/openid-configuration" => (200, Discovery()),
            "/jwks" => (200, _keys.Jwks()),
            "/device" => (200, Device()),
            "/token" => await TokenAsync(context).ConfigureAwait(false),
            _ => (404, "{\"error\":\"not_found\"}"),
        };

        byte[] bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;

        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private string Discovery() => string.Format(
        CultureInfo.InvariantCulture,
        "{{\"issuer\":\"{0}\",\"jwks_uri\":\"{0}/jwks\",\"token_endpoint\":\"{0}/token\"," +
        "\"device_authorization_endpoint\":\"{0}/device\",\"authorization_endpoint\":\"{0}/authorize\"," +
        "\"grant_types_supported\":[\"password\",\"refresh_token\"," +
        "\"urn:ietf:params:oauth:grant-type:device_code\"]," +
        "\"id_token_signing_alg_values_supported\":[\"RS256\",\"ES256\"]}}",
        Issuer);

    private string Device() => string.Format(
        CultureInfo.InvariantCulture,
        "{{\"device_code\":\"device-1\",\"user_code\":\"WDJB-MJHT\"," +
        "\"verification_uri\":\"{0}/activate\"," +
        "\"verification_uri_complete\":\"{0}/activate?user_code=WDJB-MJHT\"," +
        "\"expires_in\":600,\"interval\":{1}}}",
        Issuer,
        PollIntervalSeconds);

    private async Task<(int Status, string Body)> TokenAsync(HttpListenerContext context)
    {
        string body;
        using (StreamReader reader = new(context.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        Dictionary<string, string> form = Form(body);
        string grant = form.GetValueOrDefault("grant_type", string.Empty);

        return grant switch
        {
            "urn:ietf:params:oauth:grant-type:device_code" => DeviceToken(),
            "password" => PasswordToken(form),
            "refresh_token" => (200, Tokens()),
            _ => (400, "{\"error\":\"unsupported_grant_type\"}"),
        };
    }

    private (int Status, string Body) DeviceToken()
    {
        int left = Interlocked.Decrement(ref _pendingLeft);

        if (left >= 0)
        {
            return (400, "{\"error\":\"authorization_pending\"}");
        }

        // RFC 8628 §3.5: one slow_down, which the client must honour by widening its interval.
        return left == -1
            ? (400, "{\"error\":\"slow_down\"}")
            : (200, Tokens());
    }

    private (int Status, string Body) PasswordToken(IReadOnlyDictionary<string, string> form) =>
        form.GetValueOrDefault("username") == PasswordUser
        && form.GetValueOrDefault("password") == Password
            ? (200, Tokens())
            : (400, "{\"error\":\"invalid_grant\"}");

    private string Tokens() => string.Format(
        CultureInfo.InvariantCulture,
        "{{\"access_token\":\"{0}\",\"refresh_token\":\"refresh-1\",\"token_type\":\"Bearer\"," +
        "\"expires_in\":300}}",
        IssueToken());

    private static Dictionary<string, string> Form(string body)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal);

        foreach (string pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                continue;
            }

            form[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        return form;
    }
}
