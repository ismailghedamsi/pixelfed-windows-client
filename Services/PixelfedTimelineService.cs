using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;

namespace PixelfedClient.Services;

public sealed class PixelfedTimelineService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private const int PageSize = 20;
    private const int AccountHistoryPageSize = 40;

    public async Task<PixelfedTimelinePage> GetAccountPhotoHistoryPageAsync(
        Uri instanceBaseUri,
        string accessToken,
        string accountId,
        string? maxId,
        CancellationToken cancellationToken = default)
    {
        var query = $"limit={AccountHistoryPageSize}&only_media=true&_pe=1";
        if (!string.IsNullOrWhiteSpace(maxId))
        {
            query += $"&max_id={Uri.EscapeDataString(maxId)}";
        }

        using var request = CreateGetRequest(
            new Uri(instanceBaseUri, $"/api/v1/accounts/{Uri.EscapeDataString(accountId)}/statuses?{query}"),
            accessToken);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "load older posts from followed accounts");
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Pixelfed returned an unexpected account history response.");
        }

        var statuses = json.RootElement.EnumerateArray().ToArray();
        var posts = statuses
            .Select(status => ParsePost(status, instanceBaseUri.Host))
            .Where(post => post is { HasImage: true })
            .Select(post => post!)
            .ToArray();
        var nextMaxId = GetNextMaxId(response.Headers, instanceBaseUri)
            ?? (statuses.Length >= AccountHistoryPageSize ? GetString(statuses[^1], "id") : null);
        return new PixelfedTimelinePage(posts, nextMaxId, !string.IsNullOrWhiteSpace(nextMaxId));
    }

    public async Task<PixelfedPost?> GetStatusAsync(
        Uri instanceBaseUri,
        string accessToken,
        string statusId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateGetRequest(
            new Uri(instanceBaseUri, $"/api/v1/statuses/{Uri.EscapeDataString(statusId)}?_pe=1"), accessToken);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "open the post");
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return json.RootElement.ValueKind == JsonValueKind.Object
            ? ParsePost(json.RootElement, instanceBaseUri.Host)
            : null;
    }

    public Task<PixelfedTimelinePage> GetTimelinePageAsync(
        Uri instanceBaseUri,
        string accessToken,
        PixelfedFeedKind feedKind,
        string? maxId = null,
        CancellationToken cancellationToken = default) =>
        feedKind == PixelfedFeedKind.FollowedHashtags
            ? GetFollowedHashtagTimelinePageAsync(instanceBaseUri, accessToken, maxId, cancellationToken)
            : GetStatusTimelinePageAsync(instanceBaseUri, accessToken, feedKind, maxId, cancellationToken);

    public async Task<PixelfedTimelinePage> GetBookmarksPageAsync(
        Uri instanceBaseUri,
        string accessToken,
        string? maxId = null,
        CancellationToken cancellationToken = default)
    {
        var query = $"limit={PageSize}&_pe=1";
        if (!string.IsNullOrWhiteSpace(maxId))
        {
            query += $"&max_id={Uri.EscapeDataString(maxId)}";
        }

        using var request = CreateGetRequest(new Uri(instanceBaseUri, $"/api/v1/bookmarks?{query}"), accessToken);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "load your bookmarks");
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Pixelfed returned an unexpected bookmarks response.");
        }

        var statuses = json.RootElement.EnumerateArray().ToArray();
        var posts = statuses
            .Select(status => ParsePost(status, instanceBaseUri.Host))
            .Where(post => post is { HasImage: true })
            .Select(post => post!)
            .ToArray();
        var linkedNextMaxId = GetNextMaxId(response.Headers, instanceBaseUri);
        var nextMaxId = linkedNextMaxId
            ?? (statuses.Length == 0 ? null : GetString(statuses[^1], "id"));
        return new PixelfedTimelinePage(posts, nextMaxId, linkedNextMaxId is not null || statuses.Length >= PageSize);
    }

    public async Task<IReadOnlyList<string>> GetFollowedHashtagNamesAsync(
        Uri instanceBaseUri,
        string accessToken,
        CancellationToken cancellationToken = default) =>
        (await GetFollowedTagStatesAsync(instanceBaseUri, accessToken, cancellationToken))
            .Select(state => state.Name)
            .ToArray();

    private async Task<PixelfedTimelinePage> GetStatusTimelinePageAsync(
        Uri instanceBaseUri,
        string accessToken,
        PixelfedFeedKind feedKind,
        string? maxId,
        CancellationToken cancellationToken)
    {
        var query = $"limit={PageSize}&_pe=1";
        if (feedKind == PixelfedFeedKind.Home)
        {
            // Keep photo filtering client-side (below). Pixelfed's server-side
            // photos_reblogs_only filter can exhaust its Home timeline scan
            // before the Link/max_id cursor reaches older pages.
            query += $"&include_reblogs={(ClientPreferences.AllowReblog ? "true" : "false")}";
        }

        if (!string.IsNullOrWhiteSpace(maxId))
        {
            query += $"&max_id={Uri.EscapeDataString(maxId)}";
        }

        var endpoint = feedKind == PixelfedFeedKind.Local
            ? $"/api/v1/timelines/public?local=true&{query}"
            : $"/api/v1/timelines/home?{query}";
        for (var attempt = 0; ; attempt++)
        {
            using var request = CreateGetRequest(new Uri(instanceBaseUri, endpoint), accessToken);
            using var response = await HttpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException("Your Pixelfed session has expired. Sign in again from Settings.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Pixelfed could not load the {FeedLabel(feedKind)} feed (HTTP {(int)response.StatusCode}).");
            }

            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);

            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Pixelfed returned an unexpected feed response.");
            }

            if (feedKind == PixelfedFeedKind.Home
                && response.StatusCode == System.Net.HttpStatusCode.PartialContent
                && json.RootElement.GetArrayLength() == 0)
            {
                if (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken);
                    continue;
                }

                throw new InvalidOperationException("Pixelfed is still preparing your home feed. Please retry in a moment.");
            }

            var linkedNextMaxId = GetNextMaxId(response.Headers, instanceBaseUri);
            var statuses = json.RootElement.EnumerateArray().ToArray();
            var posts = new List<PixelfedPost>();
            foreach (var status in statuses)
            {
                var post = ParsePost(status, instanceBaseUri.Host);
                if (post is { HasImage: true })
                {
                    posts.Add(post);
                }
            }

            var nextMaxId = linkedNextMaxId
                ?? (statuses.Length == 0 ? null : GetString(statuses[^1], "id"));
            return new PixelfedTimelinePage(
                posts,
                nextMaxId,
                linkedNextMaxId is not null || statuses.Length > 0);
        }
    }

    private async Task<PixelfedTimelinePage> GetFollowedHashtagTimelinePageAsync(
        Uri instanceBaseUri,
        string accessToken,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var tagStates = string.IsNullOrWhiteSpace(cursor)
            ? await GetFollowedTagStatesAsync(instanceBaseUri, accessToken, cancellationToken)
            : DeserializeTagCursor(cursor);
        var activeStates = tagStates.Where(state => state.HasMore).Take(8).ToArray();
        if (activeStates.Length == 0)
        {
            return new PixelfedTimelinePage([], null, false);
        }

        using var concurrencyGate = new SemaphoreSlim(4, 4);
        var tagPages = await Task.WhenAll(activeStates.Select(async state =>
        {
            await concurrencyGate.WaitAsync(cancellationToken);
            try
            {
                var query = "limit=5&only_media=true&_pe=1";
                if (!string.IsNullOrWhiteSpace(state.Cursor))
                {
                    query += $"&cursor={Uri.EscapeDataString(state.Cursor)}";
                }

                var uri = new Uri(
                    instanceBaseUri,
                    $"/api/v1/timelines/tag/{Uri.EscapeDataString(state.Name)}?{query}");
                using var request = CreateGetRequest(uri, accessToken);
                using var response = await HttpClient.SendAsync(request, cancellationToken);
                await EnsureSuccessAsync(response, $"load posts for #{state.Name}");
                using var json = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                if (json.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException($"Pixelfed returned an unexpected response for #{state.Name}.");
                }

                var statuses = json.RootElement.EnumerateArray().ToArray();
                var nextCursor = GetNextLinkParameter(response.Headers, instanceBaseUri, "cursor");
                var posts = statuses
                    .Select(status => ParsePost(status, instanceBaseUri.Host))
                    .Where(post => post is { HasImage: true })
                    .Select(post => post!)
                    .ToArray();
                var hasMore = !string.IsNullOrWhiteSpace(nextCursor)
                    && !string.Equals(nextCursor, state.Cursor, StringComparison.Ordinal);
                return (State: state with { Cursor = nextCursor, HasMore = hasMore }, Posts: posts);
            }
            finally
            {
                concurrencyGate.Release();
            }
        }));

        var updatedByName = tagPages.ToDictionary(page => page.State.Name, StringComparer.OrdinalIgnoreCase);
        var nextStates = tagStates
            .Select(state => updatedByName.TryGetValue(state.Name, out var page) ? page.State : state)
            .ToArray();
        var postsById = tagPages
            .SelectMany(page => page.Posts)
            .GroupBy(post => post.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(post => post.CreatedAtUtc ?? DateTimeOffset.MinValue)
            .ToArray();
        var hasUnloadedTags = nextStates.Any(state => state.HasMore && !activeStates.Any(active =>
            string.Equals(active.Name, state.Name, StringComparison.OrdinalIgnoreCase)));
        var hasMoreTags = nextStates.Any(state => state.HasMore);
        var nextCursor = hasMoreTags
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(nextStates)))
            : null;
        return new PixelfedTimelinePage(postsById, nextCursor, hasMoreTags || hasUnloadedTags);
    }

    private static async Task<IReadOnlyList<FollowedTagCursor>> GetFollowedTagStatesAsync(
        Uri instanceBaseUri,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateGetRequest(new Uri(instanceBaseUri, "/api/v1/followed_tags?limit=200"), accessToken);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "load followed hashtags");
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Pixelfed returned an unexpected followed hashtags response.");
        }

        return json.RootElement.EnumerateArray()
            .Select(tag => GetString(tag, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new FollowedTagCursor(name!, null, true))
            .DistinctBy(state => state.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<FollowedTagCursor> DeserializeTagCursor(string cursor)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return JsonSerializer.Deserialize<FollowedTagCursor[]>(json) ?? [];
        }
        catch (FormatException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static HttpRequestMessage CreateGetRequest(Uri uri, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Your Pixelfed session has expired. Sign in again from Settings.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Pixelfed could not {operation} (HTTP {(int)response.StatusCode}).");
        }
    }

    private static string FeedLabel(PixelfedFeedKind feedKind) => feedKind switch
    {
        PixelfedFeedKind.Local => "local",
        PixelfedFeedKind.Home => "home",
        PixelfedFeedKind.FollowedHashtags => "followed hashtags",
        _ => "selected",
    };

    private static string? GetNextMaxId(System.Net.Http.Headers.HttpResponseHeaders headers, Uri instanceBaseUri) =>
        GetNextLinkParameter(headers, instanceBaseUri, "max_id");

    private static string? GetNextLinkParameter(
        System.Net.Http.Headers.HttpResponseHeaders headers,
        Uri instanceBaseUri,
        string parameterName)
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
                    if (parts.Length == 2 && Uri.UnescapeDataString(parts[0]) == parameterName)
                    {
                        return Uri.UnescapeDataString(parts[1].Replace('+', ' '));
                    }
                }
            }
        }

        return null;
    }

    public static PixelfedPost? ParsePost(JsonElement status, string? localInstanceHost = null)
    {
        var reblogElement = default(JsonElement);
        var isReblog = status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("reblog", out reblogElement)
            && reblogElement.ValueKind == JsonValueKind.Object;
        // Mastodon-compatible timelines wrap the original status in `reblog`.
        // The wrapper carries the timeline ID and the current user's action
        // state, while its nested status carries the actual author and media.
        var contentStatus = isReblog ? reblogElement : status;
        var account = contentStatus.TryGetProperty("account", out var accountElement)
            ? accountElement
            : default;
        var username = GetString(account, "username");
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var displayName = GetString(account, "display_name");
        var caption = GetString(contentStatus, "content_text");
        if (string.IsNullOrWhiteSpace(caption))
        {
            caption = StripHtml(GetString(contentStatus, "content") ?? string.Empty);
        }
        else
        {
            caption = WebUtility.HtmlDecode(caption);
        }

        var mediaAttachments = GetImageAttachments(contentStatus);
        var mediaUrl = mediaAttachments.FirstOrDefault()?.DisplayUrl;
        var createdAt = GetString(contentStatus, "created_at") ?? GetString(status, "created_at");
        var hasTimestamp = DateTimeOffset.TryParse(
            createdAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var timestamp);
        var timeLabel = hasTimestamp
                ? timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                : string.Empty;

        return new PixelfedPost(
            GetString(status, "id") ?? string.Empty,
            displayName is { Length: > 0 } ? displayName : username,
            $"@{username}",
            caption,
            mediaUrl,
            timeLabel,
            GetString(status, "url") ?? GetString(contentStatus, "url"),
            GetString(contentStatus, "visibility"),
            GetString(contentStatus, "pf_type"),
            GetBoolean(status, "favourited"),
            GetBoolean(status, "reblogged"),
            GetBoolean(status, "bookmarked"),
            GetInt32(status, "favourites_count"),
            GetInt32(status, "reblogs_count"),
            GetInt32(status, "replies_count") ?? GetInt32(status, "reply_count"),
            GetBoolean(status, "comments_disabled"))
        {
            CreatedAtUtc = hasTimestamp ? timestamp : null,
            AccountId = GetString(account, "id"),
            AccountKind = FediverseAccountClassifier.Classify(
                GetString(account, "acct"),
                GetString(account, "url"),
                localInstanceHost),
            AccountKey = GetAccountKey(account, username, localInstanceHost),
            IsReblog = isReblog,
            DownloadImageUrl = mediaAttachments.FirstOrDefault()?.DownloadUrl ?? mediaUrl,
            ImageAttachments = mediaAttachments,
        };
    }

    private static string GetAccountKey(JsonElement account, string username, string? localInstanceHost)
    {
        var acct = GetString(account, "acct") ?? username;
        if (!acct.Contains('@'))
        {
            var profileHost = Uri.TryCreate(GetString(account, "url"), UriKind.Absolute, out var profileUri)
                ? profileUri.Host
                : localInstanceHost;
            if (!string.IsNullOrWhiteSpace(profileHost))
            {
                acct = $"{acct}@{profileHost}";
            }
        }

        return acct.ToLowerInvariant();
    }

    private static IReadOnlyList<PixelfedImageAttachment> GetImageAttachments(JsonElement status)
    {
        if (!status.TryGetProperty("media_attachments", out var attachments)
            || attachments.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var images = new List<PixelfedImageAttachment>();
        foreach (var attachment in attachments.EnumerateArray())
        {
            if (!IsImageAttachment(attachment))
            {
                continue;
            }

            var displayUrl = GetHttpsImageUrl(attachment, "optimized_url")
                ?? GetHttpsImageUrl(attachment, "url")
                ?? GetHttpsImageUrl(attachment, "preview_url");
            var downloadUrl = GetHttpsImageUrl(attachment, "url")
                ?? GetHttpsImageUrl(attachment, "optimized_url")
                ?? GetHttpsImageUrl(attachment, "preview_url");
            if (displayUrl is not null)
            {
                images.Add(new PixelfedImageAttachment(displayUrl, downloadUrl ?? displayUrl));
            }
        }

        return images;
    }

    private static string? GetHttpsImageUrl(JsonElement element, string propertyName)
    {
        var url = GetString(element, propertyName);
        return Uri.TryCreate(url, UriKind.Absolute, out var mediaUri)
            && mediaUri.Scheme == Uri.UriSchemeHttps
                ? mediaUri.AbsoluteUri
                : null;
    }

    private static bool IsImageAttachment(JsonElement attachment)
    {
        var type = GetString(attachment, "type");
        var mime = GetString(attachment, "mime");
        return string.Equals(type, "image", StringComparison.OrdinalIgnoreCase)
            || (mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public static string StripHtml(string html)
    {
        var withBreaks = Regex.Replace(html, "</(p|div|br|li)>|<br\\s*/?>", " ", RegexOptions.IgnoreCase);
        var withoutTags = Regex.Replace(withBreaks, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(Regex.Replace(withoutTags, "\\s+", " ")).Trim();
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool? GetBoolean(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
        {
            return value;
        }

        return property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), out value)
                ? value
                : null;
    }
}

public sealed record PixelfedTimelinePage(
    IReadOnlyList<PixelfedPost> Posts,
    string? NextMaxId,
    bool HasMore);

public sealed record PixelfedImageAttachment(string DisplayUrl, string DownloadUrl);

public enum PixelfedFeedKind
{
    Local,
    Home,
    FollowedHashtags,
}

public sealed record FollowedTagCursor(string Name, string? Cursor, bool HasMore);

public sealed record PixelfedPost(
    string Id,
    string DisplayName,
    string Username,
    string Caption,
    string? ImageUrl,
    string CreatedAtLabel,
    string? PostUrl,
    string? Visibility,
    string? PostType,
    bool? IsFavourited,
    bool? IsReblogged,
    bool? IsBookmarked,
    int? FavouriteCount,
    int? ShareCount,
    int? ReplyCount,
    bool? CommentsDisabled)
{
    public DateTimeOffset? CreatedAtUtc { get; init; }
    public string? AccountId { get; init; }
    public FediverseAccountKind AccountKind { get; init; }
    public bool IsExternalSearchResult { get; init; }
    public bool IsReblog { get; init; }
    public string? AccountKey { get; init; }
    public string? DownloadImageUrl { get; init; }
    public IReadOnlyList<PixelfedImageAttachment> ImageAttachments { get; init; } = [];

    public bool HasImage => !string.IsNullOrWhiteSpace(ImageUrl);

    public bool IsCollectionEligible =>
        (Visibility is "public" or "unlisted")
        && (PostType is "photo" or "photo:album" or "video" or "video:album" or "photo:video:album");
}
