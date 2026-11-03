using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PixelfedClient.Pages;
using PixelfedClient.Services;

namespace PixelfedClient.Views;

public sealed partial class PostMediaCarousel : UserControl
{
    private readonly PixelfedImageDownloadService _downloadService = new();

    public static readonly DependencyProperty ResponsiveFrameProperty = DependencyProperty.Register(
        nameof(ResponsiveFrame),
        typeof(bool),
        typeof(PostMediaCarousel),
        new PropertyMetadata(false));

    public bool ResponsiveFrame
    {
        get => (bool)GetValue(ResponsiveFrameProperty);
        set => SetValue(ResponsiveFrameProperty, value);
    }

    public PostMediaCarousel()
    {
        InitializeComponent();
        MediaImage.MaxHeight = 320;
        SizeChanged += PostMediaCarousel_SizeChanged;
    }

    private void PostMediaCarousel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
        {
            return;
        }

        var responsiveMaxHeight = ResponsiveFrame
            ? e.NewSize.Width
            : Math.Clamp(e.NewSize.Width * 1.3, 180, 720);
        if (ResponsiveFrame && Math.Abs(Height - responsiveMaxHeight) > 1)
        {
            Height = responsiveMaxHeight;
        }

        if (Math.Abs(MediaImage.MaxHeight - responsiveMaxHeight) > 1)
        {
            MediaImage.MaxHeight = responsiveMaxHeight;
        }
    }

    private void PreviousImage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is FeedPostViewModel post)
        {
            post.CurrentImageIndex--;
        }
    }

    private void NextImage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is FeedPostViewModel post)
        {
            post.CurrentImageIndex++;
        }
    }

    private void MediaImage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Image image)
        {
            image.Tag = e.GetCurrentPoint(image).Position.X;
            image.CapturePointer(e.Pointer);
        }
    }

    private void MediaImage_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Image image || image.Tag is not double startX)
        {
            return;
        }

        image.Tag = null;
        image.ReleasePointerCapture(e.Pointer);
        var deltaX = e.GetCurrentPoint(image).Position.X - startX;
        if (Math.Abs(deltaX) < 55 || DataContext is not FeedPostViewModel post)
        {
            return;
        }

        post.CurrentImageIndex += deltaX < 0 ? 1 : -1;
    }

    private void MediaImage_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not Image image
            || DataContext is not FeedPostViewModel post
            || post.MediaSource is null)
        {
            return;
        }

        PhotoViewer.Show(XamlRoot, post.MediaSource, post.DisplayName);
        e.Handled = true;
    }

    private async void DownloadImage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not FeedPostViewModel post
            || !Uri.TryCreate(post.ImageDownloadUrl, UriKind.Absolute, out var imageUri)
            || imageUri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            var window = ((App)Application.Current).MainWindow;
            if (window is null)
            {
                return;
            }

            await _downloadService.SaveImageAsync(
                imageUri,
                WinRT.Interop.WindowNative.GetWindowHandle(window));
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Couldn’t download image",
                Content = ex.Message,
                CloseButtonText = "OK",
            };
            await dialog.ShowAsync();
        }
    }
}
