using System.Net.Http.Headers;
using System.Text.Json;
using Windows.Storage;

namespace PixelfedClient.Services;

public sealed class PixelfedPostCreationService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(3) };

    public async Task<PixelfedCreatedPost> CreatePhotoPostAsync(
        Uri instanceBaseUri,
        string accessToken,
        StorageFile photo,
        string caption,
        string altText,
        string visibility,
        CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        await using var stream = await photo.OpenStreamForReadAsync();
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMediaType(photo.FileType));
        form.Add(fileContent, "file", photo.Name);
        form.Add(new StringContent(caption), "status");
        if (!string.IsNullOrWhiteSpace(altText))
        {
            form.Add(new StringContent(altText), "description");
        }
        form.Add(new StringContent(visibility), "visibility");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(instanceBaseUri, "/api/v1.1/status/create"))
        {
            Content = form,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException("Your Pixelfed session has expired. Sign in again from Settings.");
            }

            throw new InvalidOperationException(ExtractError(detail)
                ?? $"Pixelfed could not publish this photo (HTTP {(int)response.StatusCode}).");
        }

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var id = GetString(json.RootElement, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("Pixelfed published the photo but returned no post ID, so it could not be added to a collection.");
        }

        return new PixelfedCreatedPost(id, GetString(json.RootElement, "url"));
    }

    private static string GetMediaType(string fileType) => fileType.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        _ => "application/octet-stream",
    };

    private static string? ExtractError(string responseBody)
    {
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            return GetString(json.RootElement, "error") ?? GetString(json.RootElement, "message");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

public sealed record PixelfedCreatedPost(string Id, string? Url);
