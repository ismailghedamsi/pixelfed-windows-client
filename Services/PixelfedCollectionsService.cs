using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PixelfedClient.Services;

public sealed class PixelfedCollectionsService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public async Task<IReadOnlyList<PixelfedCollection>> GetOwnCollectionsAsync(
        Uri instanceBaseUri,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var collections = new List<PixelfedCollection>();
        for (var page = 1; page <= 25; page++)
        {
            var uri = new Uri(instanceBaseUri, $"/api/v1.1/collections/self?page={page}");
            using var response = await HttpClient.SendAsync(CreateRequest(uri, accessToken), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateApiException("load your collections", response.StatusCode);
            }

            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var value in json.RootElement.EnumerateArray())
            {
                var id = GetString(value, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var title = GetString(value, "title");
                var publishedAt = GetString(value, "published_at");
                var thumbnailUrl = GetString(value, "thumb");
                collections.Add(new PixelfedCollection(
                    id,
                    string.IsNullOrWhiteSpace(title) ? "Untitled draft" : title,
                    GetString(value, "visibility") ?? "draft",
                    int.TryParse(GetString(value, "post_count"), out var count) ? count : GetInt32(value, "post_count"),
                    !string.IsNullOrWhiteSpace(publishedAt),
                    GetString(value, "description") ?? string.Empty,
                    Uri.TryCreate(thumbnailUrl, UriKind.Absolute, out var thumbnailUri)
                        && thumbnailUri.Scheme == Uri.UriSchemeHttps
                            ? thumbnailUri
                            : null));
            }
        }

        return collections;
    }

    public async Task<IReadOnlyList<PixelfedPost>> GetCollectionItemsAsync(
        Uri instanceBaseUri,
        string accessToken,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        var posts = new List<PixelfedPost>();
        for (var page = 1; page <= 25; page++)
        {
            var uri = new Uri(
                instanceBaseUri,
                $"/api/v1.1/collections/items/{Uri.EscapeDataString(collectionId)}?page={page}");
            using var response = await HttpClient.SendAsync(CreateRequest(uri, accessToken), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateApiException("load posts in this collection", response.StatusCode);
            }

            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var value in json.RootElement.EnumerateArray())
            {
                var post = PixelfedTimelineService.ParsePost(value);
                if (post is not null)
                {
                    posts.Add(post);
                }
            }
        }

        return posts;
    }

    public async Task AddPostAsync(
        Uri instanceBaseUri,
        string accessToken,
        string collectionId,
        string postId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(instanceBaseUri, "/api/v1.1/collections/add"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = JsonContent.Create(new
        {
            collection_id = collectionId,
            post_id = postId,
        });

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException("add this post to the collection", response.StatusCode);
        }
    }

    private static HttpRequestMessage CreateRequest(Uri uri, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static Exception CreateApiException(string operation, HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.Unauthorized
            ? new InvalidOperationException("Your Pixelfed session has expired. Sign in again from Settings.")
            : statusCode == HttpStatusCode.Forbidden
                ? new InvalidOperationException("This server denied the collection action. Sign in again with collection access, or check whether collections are enabled on this instance.")
                : new InvalidOperationException($"Pixelfed could not {operation} (HTTP {(int)statusCode}). Only your public or unlisted photo, album, and video posts can be added.");

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int GetInt32(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.TryGetInt32(out var value)
            ? value
            : 0;
}

public sealed record PixelfedCollection(
    string Id,
    string Title,
    string Visibility,
    int PostCount,
    bool IsPublished,
    string Description,
    Uri? ThumbnailUri)
{
    public string DisplayName => $"{Title} · {PostCount} posts{(IsPublished ? string.Empty : " · draft")}";
}
