using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace NineP.Cli.Oidc;

/// <summary>The three endpoints the two grants need, from the issuer's discovery document.</summary>
/// <param name="TokenEndpoint">Where a grant is exchanged for a token.</param>
/// <param name="DeviceEndpoint">Where a device authorization is started; null when unsupported.</param>
internal readonly record struct OidcEndpoints(string TokenEndpoint, string? DeviceEndpoint)
{
    /// <summary>Fetches an issuer's discovery document.</summary>
    /// <param name="http">The client to fetch with.</param>
    /// <param name="issuer">The issuer's URL.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The endpoints it advertises.</returns>
    /// <exception cref="OidcException">The document is missing or does not name a token endpoint.</exception>
    public static async Task<OidcEndpoints> DiscoverAsync(
        HttpClient http, string issuer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrEmpty(issuer);

        string url = issuer.TrimEnd('/') + "/.well-known/openid-configuration";

        try
        {
            using JsonDocument document = await http
                .GetFromJsonAsync<JsonDocument>(url, cancellationToken).ConfigureAwait(false)
                ?? throw new OidcException("the issuer returned an empty discovery document");

            string token = Text(document.RootElement, "token_endpoint")
                ?? throw new OidcException(url + " names no token_endpoint");

            return new OidcEndpoints(token, Text(document.RootElement, "device_authorization_endpoint"));
        }
        catch (Exception failure) when (failure is HttpRequestException or JsonException)
        {
            throw new OidcException(
                string.Format(CultureInfo.InvariantCulture, "could not read {0}: {1}", url, failure.Message),
                failure);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
}
