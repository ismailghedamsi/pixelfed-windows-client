using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace PixelfedClient.Services;

public sealed class PixelfedOAuthService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public const string RedirectUri = "pixelfedclient://oauth-callback";

    public static string CreateState() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public async Task<PixelfedOAuthClient> RegisterClientAsync(Uri instanceBaseUri, CancellationToken cancellationToken = default)
    {
        using var response = await HttpClient.PostAsJsonAsync(
            new Uri(instanceBaseUri, "/api/v1/apps"),
            new
            {
                client_name = "Pixelfed Windows Client",
                redirect_uris = RedirectUri,
                scopes = "read write",
                website = "https://pixelfed.org",
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"This Pixelfed server could not register the client (HTTP {(int)response.StatusCode}).");
        }

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var root = json.RootElement;
        var clientId = GetString(root, "client_id");
        var clientSecret = GetString(root, "client_secret");

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("The Pixelfed server returned incomplete OAuth client details.");
        }

        return new PixelfedOAuthClient(clientId, clientSecret);
    }

    public Uri BuildAuthorizationUri(Uri instanceBaseUri, PixelfedOAuthClient client, string state)
    {
        var query = string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "read write",
            ["state"] = state,
        }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        return new Uri($"{instanceBaseUri.GetLeftPart(UriPartial.Authority)}/oauth/authorize?{query}");
    }

    public async Task<PixelfedSignedInAccount> ExchangeCodeAsync(
        Uri instanceBaseUri,
        PixelfedOAuthClient client,
        string code,
        CancellationToken cancellationToken = default)
    {
        using var tokenResponse = await HttpClient.PostAsJsonAsync(
            new Uri(instanceBaseUri, "/oauth/token"),
            new
            {
                grant_type = "authorization_code",
                code,
                client_id = client.ClientId,
                client_secret = client.ClientSecret,
                redirect_uri = RedirectUri,
            },
            cancellationToken);

        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Pixelfed did not complete sign-in (HTTP {(int)tokenResponse.StatusCode}). Try again.");
        }

        using var tokenJson = await JsonDocument.ParseAsync(
            await tokenResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var accessToken = GetString(tokenJson.RootElement, "access_token");
        var refreshToken = GetString(tokenJson.RootElement, "refresh_token");

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Pixelfed returned no access token.");
        }

        using var accountRequest = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(instanceBaseUri, "/api/v1/accounts/verify_credentials"));
        accountRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        accountRequest.Headers.Accept.ParseAdd("application/json");

        using var accountResponse = await HttpClient.SendAsync(accountRequest, cancellationToken);
        if (!accountResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Pixelfed issued a token, but account verification failed (HTTP {(int)accountResponse.StatusCode}).");
        }

        using var accountJson = await JsonDocument.ParseAsync(
            await accountResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var accountId = GetString(accountJson.RootElement, "id") ?? string.Empty;
        var username = GetString(accountJson.RootElement, "username") ?? "Pixelfed user";
        var displayName = GetString(accountJson.RootElement, "display_name") ?? username;

        return new PixelfedSignedInAccount(accountId, username, displayName, accessToken, refreshToken, "read write");
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record PixelfedOAuthClient(string ClientId, string ClientSecret);

public sealed record PixelfedSignedInAccount(
    string AccountId,
    string Username,
    string DisplayName,
    string AccessToken,
    string? RefreshToken,
    string GrantedScopes);
