using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PixelfedClient.Services;

public sealed class PixelfedSearchService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public async Task<PixelfedSearchSuggestions> GetSuggestionsAsync(
        Uri instanceBaseUri,
        string accessToken,
        string query,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri(
            instanceBaseUri,
            $"/api/v2/search?q={Uri.EscapeDataString(query.Trim())}&limit=8&_pe=1");
        using var json = await GetJsonAsync(uri, accessToken, "search Pixelfed", cancellationToken);
        var root = json.RootElement;

        var accounts = new List<PixelfedSearchAccount>();
        if (root.TryGetProperty("accounts", out var accountArray)
            && accountArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var account in accountArray.EnumerateArray())
            {
                var username = GetString(account, "acct") ?? GetString(account, "username");
                if (string.IsNullOrWhiteSpace(username))
                {
                    continue;
                }

                accounts.Add(new PixelfedSearchAccount(
                    GetString(account, "id") ?? string.Empty,
                    GetString(account, "display_name") is { Length: > 0 } name
                        ? name
                        : GetString(account, "username") ?? username,
                    "@" + username,
                    GetHttpsUri(GetString(account, "avatar") ?? GetString(account, "avatar_static")),
                    GetHttpsUri(GetString(account, "url"))));
            }
        }

        var hashtags = root.TryGetProperty("hashtags", out var hashtagArray)
            && hashtagArray.ValueKind == JsonValueKind.Array
                ? hashtagArray.EnumerateArray()
                    .Select(tag => GetString(tag, "name"))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!.TrimStart('#'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];

        return new PixelfedSearchSuggestions(accounts, hashtags);
    }

    public Task<IReadOnlyList<PixelfedPost>> GetPhotoPostsForTagAsync(
        Uri instanceBaseUri,
        string accessToken,
        string tag,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri(
            instanceBaseUri,
            $"/api/v1/timelines/tag/{Uri.EscapeDataString(tag.TrimStart('#'))}?limit=20&only_media=true&_pe=1");
        return GetPhotoStatusesAsync(uri, instanceBaseUri, accessToken, $"load #{tag} posts", cancellationToken);
    }

    public Task<IReadOnlyList<PixelfedPost>> GetPhotoPostsForAccountAsync(
        Uri instanceBaseUri,
        string accessToken,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri(
            instanceBaseUri,
            $"/api/v1/accounts/{Uri.EscapeDataString(accountId)}/statuses?limit=20&only_media=true&_pe=1");
        return GetPhotoStatusesAsync(uri, instanceBaseUri, accessToken, "load this person’s photo posts", cancellationToken);
    }

    public Task<IReadOnlyList<PixelfedPost>> ResolvePhotoPostAsync(
        Uri instanceBaseUri,
        string accessToken,
        string postUrl,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri(
            instanceBaseUri,
            $"/api/v2/search?q={Uri.EscapeDataString(postUrl)}&type=statuses&resolve=true&_pe=1");
        return GetPhotoStatusesAsync(uri, instanceBaseUri, accessToken, "resolve this post", cancellationToken, "statuses");
    }

    private static async Task<IReadOnlyList<PixelfedPost>> GetPhotoStatusesAsync(
        Uri uri,
        Uri instanceBaseUri,
        string accessToken,
        string operation,
        CancellationToken cancellationToken,
        string arrayProperty = "")
    {
        using var json = await GetJsonAsync(uri, accessToken, operation, cancellationToken);
        var statuses = json.RootElement;
        if (arrayProperty.Length > 0)
        {
            if (!statuses.TryGetProperty(arrayProperty, out statuses)
                || statuses.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
        }

        if (statuses.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Pixelfed returned an unexpected post search response.");
        }

        return statuses.EnumerateArray()
            .Select(status => PixelfedTimelineService.ParsePost(status))
            .Where(post => post is { HasImage: true })
            .Select(post => post!)
            .ToArray();
    }

    private static async Task<JsonDocument> GetJsonAsync(
        Uri uri,
        string accessToken,
        string operation,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Your Pixelfed session has expired. Sign in again from Settings.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Pixelfed could not {operation} (HTTP {(int)response.StatusCode}).");
        }

        return await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }

    private static Uri? GetHttpsUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : null;

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

public sealed record PixelfedSearchSuggestions(
    IReadOnlyList<PixelfedSearchAccount> Accounts,
    IReadOnlyList<string> Hashtags);

public sealed record PixelfedSearchAccount(
    string Id,
    string DisplayName,
    string Username,
    Uri? AvatarUri,
    Uri? ProfileUri);
