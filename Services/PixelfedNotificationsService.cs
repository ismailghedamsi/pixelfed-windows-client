using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PixelfedClient.Services;

public sealed class PixelfedNotificationsService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<PixelfedNotificationPage> GetPageAsync(
        Uri instanceUri,
        string accessToken,
        string? maxId = null,
        CancellationToken cancellationToken = default)
    {
        var query = "limit=20&_pe=1";
        if (!string.IsNullOrWhiteSpace(maxId))
        {
            query += $"&max_id={Uri.EscapeDataString(maxId)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(instanceUri, $"/api/v1/notifications?{query}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Your Pixelfed session has expired. Sign in again from Settings.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Pixelfed could not load notifications (HTTP {(int)response.StatusCode}).");
        }

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Pixelfed returned an unexpected notifications response.");
        }

        var items = json.RootElement.EnumerateArray().ToArray();
        var notifications = items.Select(Parse).Where(item => item is not null).Select(item => item!).ToArray();
        var nextId = GetNextMaxId(response, instanceUri)
            ?? (items.Length == 0 ? null : GetString(items[^1], "id"));
        return new PixelfedNotificationPage(notifications, nextId, items.Length >= 20);
    }

    public async Task MarkAllAsReadAsync(
        Uri instanceUri,
        string accessToken,
        string latestNotificationId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(instanceUri, "/api/v1/markers"))
        {
            Content = JsonContent.Create(new
            {
                notifications = new { last_read_id = latestNotificationId },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Your Pixelfed session has expired. Sign in again from Settings.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Pixelfed could not mark notifications as read (HTTP {(int)response.StatusCode}). The server may require write permission.");
        }
    }

    private static PixelfedNotification? Parse(JsonElement value)
    {
        var account = GetObject(value, "account");
        var username = GetString(account, "username");
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var displayName = GetString(account, "display_name");
        var type = GetString(value, "type") ?? "notification";
        var status = GetObject(value, "status");
        var actionLabel = DescribeType(type, status);
        var caption = PixelfedTimelineService.StripHtml(GetString(status, "content") ?? string.Empty);
        var media = GetObject(status, "media_attachments");
        var imageUrl = media.ValueKind == JsonValueKind.Array
            ? media.EnumerateArray().Select(attachment => GetString(attachment, "preview_url") ?? GetString(attachment, "url"))
                .FirstOrDefault(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            : null;
        var createdAt = DateTimeOffset.TryParse(
            GetString(value, "created_at"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var timestamp)
                ? timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                : string.Empty;

        return new PixelfedNotification(
            GetString(value, "id") ?? string.Empty,
            GetString(account, "id") ?? string.Empty,
            displayName is { Length: > 0 } ? displayName : username,
            $"@{GetString(account, "acct") ?? username}",
            GetString(account, "url"),
            type,
            type is "favourite" or "favorite" ? "Likes"
                : type is "follow" or "follow_request" ? "Follows"
                : type is "mention" or "status" ? "Comments"
                : "Other",
            actionLabel,
            createdAt,
            caption,
            imageUrl,
            GetString(status, "url"),
            GetString(status, "id"));
    }

    private static string DescribeType(string type, JsonElement status)
    {
        var isReply = status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("in_reply_to_id", out var replyId)
            && replyId.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        return type switch
        {
            "favourite" or "favorite" => "♥  Liked your post",
            "reblog" => "↻  Shared your post",
            "follow" => "＋  Followed you",
            "follow_request" => "＋  Requested to follow you",
            "mention" when isReply => "💬  Commented on your post",
            "mention" => "@  Mentioned you",
            "status" when isReply => "💬  Commented on your post",
            "status" => "▣  Posted",
            "poll" => "Poll ended",
            "update" => "Edited a post",
            _ => type.Replace('_', ' '),
        };
    }

    private static string? GetNextMaxId(HttpResponseMessage response, Uri instanceUri)
    {
        if (!response.Headers.TryGetValues("Link", out var headers))
        {
            return null;
        }

        foreach (var header in headers)
        foreach (var part in header.Split(','))
        {
            if (!part.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)) continue;
            var start = part.IndexOf('<');
            var end = part.IndexOf('>');
            if (start < 0 || end <= start) continue;
            var url = part[(start + 1)..end];
            if (!Uri.TryCreate(instanceUri, url, out var nextUri)) continue;
            foreach (var parameter in nextUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = parameter.Split('=', 2);
                if (pair.Length == 2 && Uri.UnescapeDataString(pair[0]) == "max_id")
                {
                    return Uri.UnescapeDataString(pair[1].Replace('+', ' '));
                }
            }
        }

        return null;
    }

    private static JsonElement GetObject(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : default;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed record PixelfedNotificationPage(
    IReadOnlyList<PixelfedNotification> Notifications,
    string? NextMaxId,
    bool HasMore);

public sealed record PixelfedNotification(
    string Id,
    string AccountId,
    string DisplayName,
    string Username,
    string? AccountUrl,
    string Type,
    string Category,
    string Description,
    string CreatedAtLabel,
    string Caption,
    string? ImageUrl,
    string? StatusUrl,
    string? StatusId)
{
    public string? TargetUrl => Type == "follow" || Type == "follow_request" ? AccountUrl : StatusUrl ?? AccountUrl;
    public bool HasPreview => !string.IsNullOrWhiteSpace(Caption) || !string.IsNullOrWhiteSpace(ImageUrl);
}
