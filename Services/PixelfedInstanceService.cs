using System.Net.Http.Json;
using System.Text.Json;

namespace PixelfedClient.Services;

public sealed class PixelfedInstanceService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public async Task<PixelfedInstanceDetails> GetInstanceAsync(string? address, CancellationToken cancellationToken = default)
    {
        var baseUri = ParseBaseUri(address);
        using var instanceResponse = await HttpClient.GetAsync(
            new Uri(baseUri, "/api/v2/instance"),
            cancellationToken);

        if (!instanceResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The server returned {(int)instanceResponse.StatusCode} while checking its instance information.");
        }

        using var instanceJson = await JsonDocument.ParseAsync(
            await instanceResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var instanceRoot = instanceJson.RootElement;
        var title = GetString(instanceRoot, "title") ?? baseUri.Host;
        var domain = GetString(instanceRoot, "domain") ?? baseUri.Host;
        var version = GetString(instanceRoot, "version") ?? "unknown";

        using var configResponse = await HttpClient.GetAsync(
            new Uri(baseUri, "/api/v2.1/config"),
            cancellationToken);

        if (!configResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{domain} responded to the standard instance check, but its Pixelfed configuration endpoint returned {(int)configResponse.StatusCode}.");
        }

        using var configJson = await JsonDocument.ParseAsync(
            await configResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var oauthEnabled = configJson.RootElement.TryGetProperty("oauth", out var oauthElement)
            && oauthElement.ValueKind == JsonValueKind.True;

        return new PixelfedInstanceDetails(baseUri, title, domain, version, oauthEnabled);
    }

    private static Uri ParseBaseUri(string? address)
    {
        var value = address?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Enter your Pixelfed server address first.");
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = $"https://{value}";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException("Enter a server domain such as pixelfed.social. Use HTTPS and omit paths, query strings, and fragments.");
        }

        return new UriBuilder(uri) { Path = "/", Query = string.Empty, Fragment = string.Empty }.Uri;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

public sealed record PixelfedInstanceDetails(
    Uri BaseUri,
    string Title,
    string Domain,
    string Version,
    bool OAuthEnabled)
{
    public string BaseUrl => BaseUri.GetLeftPart(UriPartial.Authority);
}
