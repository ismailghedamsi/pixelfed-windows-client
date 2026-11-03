using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PixelfedClient.Pages;

namespace PixelfedClient.Services;

public sealed class PixelfedPostActionsService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public async Task<PixelfedPostActionState> SetFavouriteAsync(
        Uri instanceBaseUri,
        string accessToken,
        FeedPostViewModel post,
        bool favourite,
        CancellationToken cancellationToken = default)
    {
        var expectedFavouriteCount = Math.Max(
            0,
            post.FavouriteCount + (favourite ? 1 : -1));
        var responseState = await SendActionAsync(
            instanceBaseUri,
            accessToken,
            post,
            favourite ? "favourite" : "unfavourite",
            favourite,
            post.IsReblogged,
            post.IsBookmarked,
            post.FavouriteCount + (favourite ? 1 : -1),
            post.ShareCount,
            post.ReplyCount,
            cancellationToken);

        // Some Pixelfed instances return a stale status object after an
        // unfavourite. Keep the UI aligned with the action that just succeeded
        // and update the count from the current local count exactly once.
        return responseState with
        {
            IsFavourited = favourite,
            FavouriteCount = expectedFavouriteCount,
        };
    }

    public Task<PixelfedPostActionState> SetReblogAsync(
        Uri instanceBaseUri,
        string accessToken,
        FeedPostViewModel post,
        bool reblog,
        CancellationToken cancellationToken = default) =>
        SendActionAsync(
            instanceBaseUri,
            accessToken,
            post,
            reblog ? "reblog" : "unreblog",
            post.IsFavourited,
            reblog,
            post.IsBookmarked,
            post.FavouriteCount,
            post.ShareCount + (reblog ? 1 : -1),
            post.ReplyCount,
            cancellationToken);

    public Task<PixelfedPostActionState> SetBookmarkAsync(
        Uri instanceBaseUri,
        string accessToken,
        FeedPostViewModel post,
        bool bookmark,
        CancellationToken cancellationToken = default) =>
        SendActionAsync(
            instanceBaseUri,
            accessToken,
            post,
            bookmark ? "bookmark" : "unbookmark",
            post.IsFavourited,
            post.IsReblogged,
            bookmark,
            post.FavouriteCount,
            post.ShareCount,
            post.ReplyCount,
            cancellationToken);

    public async Task ReplyAsync(
        Uri instanceBaseUri,
        string accessToken,
        FeedPostViewModel post,
        string text,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(instanceBaseUri, "/api/v1/statuses?_pe=1"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = JsonContent.Create(new
        {
            status = text,
            in_reply_to_id = post.PostId,
            visibility = post.Visibility is "public" or "unlisted" or "private" or "direct"
                ? post.Visibility
                : "public",
        });

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync("post this comment", response);
        }
    }

    private async Task<PixelfedPostActionState> SendActionAsync(
        Uri instanceBaseUri,
        string accessToken,
        FeedPostViewModel post,
        string action,
        bool fallbackFavourited,
        bool fallbackReblogged,
        bool fallbackBookmarked,
        int fallbackFavouriteCount,
        int fallbackShareCount,
        int fallbackReplyCount,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(
                instanceBaseUri,
                $"/api/v1/statuses/{Uri.EscapeDataString(post.PostId)}/{action}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync($"{action} this post", response);
        }

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var value = json.RootElement;
        return new PixelfedPostActionState(
            GetBoolean(value, "favourited") ?? fallbackFavourited,
            GetBoolean(value, "reblogged") ?? fallbackReblogged,
            GetBoolean(value, "bookmarked") ?? fallbackBookmarked,
            GetInt32(value, "favourites_count") ?? fallbackFavouriteCount,
            GetInt32(value, "reblogs_count") ?? fallbackShareCount,
            GetInt32(value, "replies_count") ?? GetInt32(value, "reply_count") ?? fallbackReplyCount);
    }

    private static async Task<InvalidOperationException> CreateApiExceptionAsync(
        string operation,
        HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new InvalidOperationException("Your Pixelfed session has expired. Sign in again from Settings.");
        }

        var detail = string.Empty;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            detail = GetString(json.RootElement, "error")
                ?? GetString(json.RootElement, "message")
                ?? string.Empty;
        }
        catch (JsonException)
        {
            // Keep the HTTP status as the fallback when the instance returns a non-JSON error page.
        }

        return new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail)
                ? $"Pixelfed could not {operation} (HTTP {(int)response.StatusCode})."
                : $"Pixelfed could not {operation}: {detail}");
    }

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

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

public sealed record PixelfedPostActionState(
    bool IsFavourited,
    bool IsReblogged,
    bool IsBookmarked,
    int FavouriteCount,
    int ShareCount,
    int ReplyCount);
