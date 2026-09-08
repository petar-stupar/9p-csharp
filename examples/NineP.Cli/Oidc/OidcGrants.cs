using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace NineP.Cli.Oidc;

/// <summary>
/// The two grants the cli can run (§8.2, S-30). They live here and in no published package: the
/// libraries never perform a login, and a caller who already holds a token uses
/// <c>--auth bearer:</c> instead. Refresh tokens stay in memory for the life of the process and
/// are never written to disk; no token is ever logged.
/// </summary>
internal static class OidcGrants
{
    /// <summary>How much <c>slow_down</c> adds to the polling interval, per RFC 8628 §3.5.</summary>
    private static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The RFC 8628 device authorization grant. The verification URL and the user code go to
    /// <b>standard error</b>, so that a command whose output is being piped is not corrupted by
    /// them.
    /// </summary>
    /// <param name="http">The client to talk to the issuer with.</param>
    /// <param name="issuer">The issuer's URL.</param>
    /// <param name="clientId">The client id to present.</param>
    /// <param name="prompt">Where the verification URL and user code are printed.</param>
    /// <param name="clock">The clock the expiry and the polling are measured on.</param>
    /// <param name="cancellationToken">Cancels the grant.</param>
    /// <returns>The access token.</returns>
    /// <exception cref="OidcException">The issuer refused, or the code expired.</exception>
    public static async Task<string> DeviceAsync(
        HttpClient http,
        string issuer,
        string clientId,
        TextWriter prompt,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(clock);

        OidcEndpoints endpoints = await OidcEndpoints
            .DiscoverAsync(http, issuer, cancellationToken).ConfigureAwait(false);

        string device = endpoints.DeviceEndpoint
            ?? throw new OidcException(issuer + " advertises no device_authorization_endpoint");

        using JsonDocument started = await PostAsync(
            http,
            device,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["client_id"] = clientId },
            cancellationToken).ConfigureAwait(false);

        string deviceCode = Text(started.RootElement, "device_code")
            ?? throw new OidcException("the device authorization carried no device_code");

        await prompt.WriteLineAsync(string.Format(
            CultureInfo.InvariantCulture,
            "open {0} and enter the code {1}",
            Text(started.RootElement, "verification_uri_complete")
                ?? Text(started.RootElement, "verification_uri")
                ?? issuer,
            Text(started.RootElement, "user_code") ?? "(none)")).ConfigureAwait(false);

        return await PollAsync(
            http, endpoints.TokenEndpoint, clientId, deviceCode, started.RootElement, clock, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The resource-owner password grant, which exists for development and test and says so on
    /// every use.
    /// </summary>
    /// <param name="http">The client to talk to the issuer with.</param>
    /// <param name="issuer">The issuer's URL.</param>
    /// <param name="clientId">The client id to present.</param>
    /// <param name="user">The user name.</param>
    /// <param name="password">The password.</param>
    /// <param name="warnings">Where the warning is printed.</param>
    /// <param name="cancellationToken">Cancels the grant.</param>
    /// <returns>The access token.</returns>
    /// <exception cref="OidcException">The issuer refused the credentials.</exception>
    public static async Task<string> PasswordAsync(
        HttpClient http,
        string issuer,
        string clientId,
        string user,
        string password,
        TextWriter warnings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(warnings);

        await warnings.WriteLineAsync("warning: the password grant is for development and test only")
            .ConfigureAwait(false);

        OidcEndpoints endpoints = await OidcEndpoints
            .DiscoverAsync(http, issuer, cancellationToken).ConfigureAwait(false);

        using JsonDocument answer = await PostAsync(
            http,
            endpoints.TokenEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "password",
                ["client_id"] = clientId,
                ["username"] = user,
                ["password"] = password,
            },
            cancellationToken).ConfigureAwait(false);

        return AccessToken(answer.RootElement);
    }

    private static async Task<string> PollAsync(
        HttpClient http,
        string tokenEndpoint,
        string clientId,
        string deviceCode,
        JsonElement started,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(Number(started, "interval") ?? 5);
        long deadlineStarted = clock.GetTimestamp();
        TimeSpan expiresIn = TimeSpan.FromSeconds(Number(started, "expires_in") ?? 600);

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = clientId,
            ["device_code"] = deviceCode,
        };

        while (clock.GetElapsedTime(deadlineStarted) < expiresIn)
        {
            using JsonDocument answer =
                await PostAsync(http, tokenEndpoint, form, cancellationToken, allowErrors: true)
                    .ConfigureAwait(false);

            string? error = Text(answer.RootElement, "error");

            if (error is null)
            {
                return AccessToken(answer.RootElement);
            }

            // RFC 8628 §3.5: slow_down widens the interval by five seconds and is not an error.
            if (error == "slow_down")
            {
                interval += SlowDownStep;
            }
            else if (error != "authorization_pending")
            {
                throw new OidcException("the device grant was refused: " + error);
            }

            await Task.Delay(interval, clock, cancellationToken).ConfigureAwait(false);
        }

        throw new OidcException("the device code expired before it was approved");
    }

    private static async Task<JsonDocument> PostAsync(
        HttpClient http,
        string url,
        Dictionary<string, string> form,
        CancellationToken cancellationToken,
        bool allowErrors = false)
    {
        try
        {
            using FormUrlEncodedContent content = new(form);
            using HttpResponseMessage response =
                await http.PostAsync(new Uri(url), content, cancellationToken).ConfigureAwait(false);

            if (!allowErrors && !response.IsSuccessStatusCode)
            {
                throw new OidcException(string.Format(
                    CultureInfo.InvariantCulture, "{0} answered {1}", url, (int)response.StatusCode));
            }

            return await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new OidcException(url + " answered with no document");
        }
        catch (Exception failure) when (failure is HttpRequestException or JsonException)
        {
            throw new OidcException(
                string.Format(CultureInfo.InvariantCulture, "{0} failed: {1}", url, failure.Message),
                failure);
        }
    }

    private static string AccessToken(JsonElement answer) =>
        Text(answer, "access_token") ?? throw new OidcException("the issuer returned no access_token");

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;

    private static int? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number)
            ? number
            : null;
}
