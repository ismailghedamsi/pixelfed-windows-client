using Windows.Storage;
using Windows.Storage.Pickers;

namespace PixelfedClient.Services;

public sealed class PixelfedImageDownloadService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public async Task<StorageFile?> SaveImageAsync(
        Uri imageUri,
        IntPtr windowHandle,
        CancellationToken cancellationToken = default)
    {
        if (imageUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Only secure HTTPS image URLs can be downloaded.");
        }

        var extension = Path.GetExtension(imageUri.AbsolutePath).ToLowerInvariant();
        var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".heic", ".tif", ".tiff" };
        if (!supportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            extension = ".jpg";
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = Path.GetFileNameWithoutExtension(imageUri.AbsolutePath) is { Length: > 0 } name
                ? name
                : "pixelfed-image",
        };
        picker.FileTypeChoices.Add("Image file", [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return null;
        }

        using var response = await HttpClient.GetAsync(
            imageUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The image server returned HTTP {(int)response.StatusCode}.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = await file.OpenStreamForWriteAsync();
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        return file;
    }
}
