using System.Globalization;
using System.Net;
using System.Text.Json;

namespace PixelfedClient.Services;

public sealed class HolosSearchService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(25),
    };

    private static readonly Uri ServiceUri = new("https://discover.holos.social/");

    public async Task<IReadOnlyList<string>> GetHashtagSuggestionsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query.Trim().TrimStart('#');
        if (normalizedQuery.Length < 2)
        {
            return [];
        }

        var requestUri = new Uri(
            ServiceUri,
            $"api/v1/search/hashtags?q={Uri.EscapeDataString(normalizedQuery)}&limit=6");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Holos Discover could not suggest hashtags (HTTP {(int)response.StatusCode}).");
        }

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("data", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return items.EnumerateArray()
            .Select(item => GetString(item, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.TrimStart('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<PixelfedPost>> GetPhotoPostsForTagAsync(
        string tag,
        CancellationToken cancellationToken = default) =>
        (await GetPhotoPostsForTagPageAsync(tag, null, cancellationToken)).Posts;

    public async Task<HolosPhotoTimelinePage> GetPhotoPostsForTagPageAsync(
        string tag,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var normalizedTag = tag.Trim().TrimStart('#');
        if (normalizedTag.Length == 0)
        {
            return new HolosPhotoTimelinePage([], null, false);
        }

        var query = "limit=40&contentType=image&sensitive=hide";
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        var requestUri = new Uri(ServiceUri,
            $"api/v1/timeline/tag/{Uri.EscapeDataString(normalizedTag)}?{query}");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("Cross-instance hashtag search is temporarily rate limited. Please wait a minute and try again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Holos Discover could not search #{normalizedTag} (HTTP {(int)response.StatusCode}).");
        }

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("data", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Holos Discover returned an unexpected hashtag search response.");
        }

        var posts = items.EnumerateArray()
            .Select(ParsePost)
            .Where(post => post is { HasImage: true })
            .Select(post => post!)
            .ToArray();
        var nextCursor = json.RootElement.TryGetProperty("meta", out var meta)
            ? GetString(meta, "cursor")
            : null;
        var hasMore = !string.IsNullOrWhiteSpace(nextCursor)
            && !string.Equals(nextCursor, cursor, StringComparison.Ordinal);
        return new HolosPhotoTimelinePage(posts, nextCursor, hasMore);
    }

    private static PixelfedPost? ParsePost(JsonElement item)
    {
        var handle = GetString(item, "authorHandle");
        var domain = GetString(item, "authorDomain");
        if (string.IsNullOrWhiteSpace(handle) || string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        var attachment = item.TryGetProperty("attachments", out var attachments)
            && attachments.ValueKind == JsonValueKind.Array
                ? attachments.EnumerateArray()
                    .FirstOrDefault(candidate => string.Equals(GetString(candidate, "type"), "image", StringComparison.OrdinalIgnoreCase))
                : default;
        if (attachment.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var imageUrl = GetHttpsUrl(GetString(attachment, "previewUrl"))
            ?? GetHttpsUrl(GetString(attachment, "url"));
        if (imageUrl is null)
        {
            return null;
        }

        var publishedAt = GetString(item, "publishedAt");
        var hasTimestamp = DateTimeOffset.TryParse(
            publishedAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var timestamp);
        var postUrl = GetHttpsUrl(GetString(item, "url"));
        var authorName = GetString(item, "authorName");
        var acct = $"{handle.TrimStart('@')}@{domain}";

        return new PixelfedPost(
            GetString(item, "id") ?? postUrl?.AbsoluteUri ?? acct,
            string.IsNullOrWhiteSpace(authorName) ? handle : authorName,
            $"@{acct}",
            PixelfedTimelineService.StripHtml(GetString(item, "content") ?? string.Empty),
            imageUrl.AbsoluteUri,
            hasTimestamp ? timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : string.Empty,
            postUrl?.AbsoluteUri,
            GetString(item, "visibility"),
            null,
            null,
            null,
            null,
            GetInt32(item, "favouritesCount"),
            GetInt32(item, "reblogsCount"),
            GetInt32(item, "repliesCount"),
            false)
        {
            CreatedAtUtc = hasTimestamp ? timestamp : null,
            AccountKind = FediverseAccountClassifier.Classify(acct, null, domain),
            IsExternalSearchResult = true,
            AccountKey = acct.ToLowerInvariant(),
            DownloadImageUrl = GetHttpsUrl(GetString(attachment, "url"))?.AbsoluteUri ?? imageUrl.AbsoluteUri,
        };
    }

    private static Uri? GetHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : null;

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt32(out var value)
            ? value
            : null;
}

public sealed record HolosPhotoTimelinePage(
    IReadOnlyList<PixelfedPost> Posts,
    string? NextCursor,
    bool HasMore);
