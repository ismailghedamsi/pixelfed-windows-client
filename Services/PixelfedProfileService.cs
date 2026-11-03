using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PixelfedClient.Services;

public sealed class PixelfedProfileService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public async Task<PixelfedProfile> GetOwnProfileAsync(
        Uri instanceBaseUri,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var accountRequest = CreateRequest(
            new Uri(instanceBaseUri, "/api/v1/accounts/verify_credentials?_pe=1"),
            accessToken);
        using var accountResponse = await HttpClient.SendAsync(accountRequest, cancellationToken);
        if (!accountResponse.IsSuccessStatusCode)
        {
            throw CreateApiException("load your profile", accountResponse.StatusCode);
        }

        using var accountJson = await JsonDocument.ParseAsync(
            await accountResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var account = accountJson.RootElement;
        var accountId = GetString(account, "id");
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new InvalidOperationException("Pixelfed did not return an account ID for your profile.");
        }

        var username = GetString(account, "username") ?? "Pixelfed user";
        var displayName = GetString(account, "display_name");
        var bio = PixelfedTimelineService.StripHtml(WebUtility.HtmlDecode(GetString(account, "note") ?? string.Empty));
        var avatar = GetString(account, "avatar") ?? GetString(account, "avatar_static");
        return new PixelfedProfile(
            accountId,
            string.IsNullOrWhiteSpace(displayName) ? username : displayName,
            $"@{username}",
            bio,
            GetImageUri(avatar));
    }

    public async Task<PixelfedProfilePostsPage> GetOwnPostsPageAsync(
        Uri instanceBaseUri,
        string accessToken,
        string accountId,
        string? maxId = null,
        CancellationToken cancellationToken = default)
    {
        var query = "limit=40&only_media=true&_pe=1";
        if (!string.IsNullOrWhiteSpace(maxId))
        {
            query += $"&max_id={Uri.EscapeDataString(maxId)}";
        }

        var postsUri = new Uri(
            instanceBaseUri,
            $"/api/v1/accounts/{Uri.EscapeDataString(accountId)}/statuses?{query}");
        using var postsRequest = CreateRequest(postsUri, accessToken);
        using var postsResponse = await HttpClient.SendAsync(postsRequest, cancellationToken);
        if (!postsResponse.IsSuccessStatusCode)
        {
            throw CreateApiException("load your posts", postsResponse.StatusCode);
        }

        using var postsJson = await JsonDocument.ParseAsync(
            await postsResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (postsJson.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Pixelfed returned an unexpected profile posts response.");
        }

        var statuses = postsJson.RootElement.EnumerateArray().ToArray();
        var posts = statuses
            .Select(status => PixelfedTimelineService.ParsePost(status, instanceBaseUri.Host))
            .Where(post => post is { HasImage: true })
            .Select(post => post!)
            .ToArray();
        var nextMaxId = GetNextMaxId(postsResponse.Headers, instanceBaseUri)
            ?? (statuses.Length == 40 ? GetString(statuses[^1], "id") : null);

        return new PixelfedProfilePostsPage(posts, nextMaxId, !string.IsNullOrWhiteSpace(nextMaxId));
    }

    private static string? GetNextMaxId(System.Net.Http.Headers.HttpResponseHeaders headers, Uri instanceBaseUri)
    {
        if (!headers.TryGetValues("Link", out var linkHeaders))
        {
            return null;
        }

        foreach (var linkHeader in linkHeaders)
        {
            foreach (Match match in Regex.Matches(linkHeader, "<([^>]+)>[^,]*\\brel=\\\"?next\\\"?", RegexOptions.IgnoreCase))
            {
                if (!Uri.TryCreate(instanceBaseUri, match.Groups[1].Value, out var nextUri)
                    || nextUri.Scheme != Uri.UriSchemeHttps
                    || !string.Equals(nextUri.Host, instanceBaseUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var parameter in nextUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = parameter.Split('=', 2);
                    if (parts.Length == 2 && Uri.UnescapeDataString(parts[0]) == "max_id")
                    {
                        return Uri.UnescapeDataString(parts[1].Replace('+', ' '));
                    }
                }
            }
        }

        return null;
    }

    private static HttpRequestMessage CreateRequest(Uri uri, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static Exception CreateApiException(string operation, System.Net.HttpStatusCode statusCode) =>
        statusCode == System.Net.HttpStatusCode.Unauthorized
            ? new InvalidOperationException("Your Pixelfed session has expired. Sign in again from Settings.")
            : new InvalidOperationException($"Pixelfed could not {operation} (HTTP {(int)statusCode}).");

    private static Uri? GetImageUri(string? value) =>
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

public sealed record PixelfedProfile(
    string AccountId,
    string DisplayName,
    string Username,
    string Bio,
    Uri? AvatarUri);

public sealed record PixelfedProfilePostsPage(
    IReadOnlyList<PixelfedPost> Posts,
    string? NextMaxId,
    bool HasMore);
