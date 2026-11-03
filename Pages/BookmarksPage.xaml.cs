using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PixelfedClient.Services;
using System.Collections.ObjectModel;
using Windows.Storage;

namespace PixelfedClient.Pages;

public sealed partial class BookmarksPage : Page
{
    private readonly PixelfedTimelineService _timelineService = new();
    private readonly PixelfedImageDownloadService _imageDownloadService = new();
    private readonly ObservableCollection<FeedPostViewModel> _posts = new();
    private Uri? _instanceUri;
    private CredentialVault.SavedPixelfedCredentials? _credentials;
    private string? _cursor;
    private bool _hasMore;
    private bool _isLoading;

    public BookmarksPage()
    {
        InitializeComponent();
        PostsGridView.ItemsSource = _posts;
        PostsListView.ItemsSource = _posts;
        Loaded += BookmarksPage_Loaded;
    }

    private async void BookmarksPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= BookmarksPage_Loaded;
        await LoadPageAsync(reset: true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadPageAsync(reset: true);
    private async void LoadMore_Click(object sender, RoutedEventArgs e) => await LoadPageAsync(reset: false);

    private async void PostsScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate || _isLoading || !_hasMore)
        {
            return;
        }

        if (PostsScrollViewer.ScrollableHeight - PostsScrollViewer.VerticalOffset < 600)
        {
            await LoadPageAsync(reset: false);
        }
    }

    private async Task LoadPageAsync(bool reset)
    {
        if (_isLoading)
        {
            return;
        }

        var instanceUrl = ApplicationData.Current.LocalSettings.Values["pixelfed.instance"] as string;
        if (!Uri.TryCreate(instanceUrl, UriKind.Absolute, out _instanceUri))
        {
            _posts.Clear();
            StatusText.Text = "Choose a Pixelfed server and sign in from Settings first.";
            return;
        }

        _credentials = CredentialVault.GetCredentialsFor(_instanceUri.Host);
        if (_credentials is null)
        {
            _posts.Clear();
            StatusText.Text = "Sign in to your Pixelfed account to view bookmarks.";
            return;
        }

        if (reset)
        {
            _posts.Clear();
            _cursor = null;
            _hasMore = true;
            StatusText.Text = "Loading your bookmarks…";
        }

        if (!_hasMore)
        {
            return;
        }

        _isLoading = true;
        SetLoadMoreEnabled(false);
        try
        {
            var page = await _timelineService.GetBookmarksPageAsync(_instanceUri, _credentials.AccessToken, _cursor);
            foreach (var post in page.Posts)
            {
                if (!_posts.Any(existing => existing.PostId == post.Id))
                {
                    _posts.Add(new FeedPostViewModel(post));
                }
            }

            _cursor = page.NextMaxId;
            _hasMore = page.HasMore && !string.IsNullOrWhiteSpace(_cursor);
            StatusText.Text = _posts.Count == 0
                ? "No bookmarked photo posts yet. Save a post with its bookmark button and refresh here."
                : $"{_posts.Count} bookmarked photo post{(_posts.Count == 1 ? string.Empty : "s")}";
            SetLoadMoreVisibility(_hasMore);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            StatusText.Text = ex.Message;
            SetLoadMoreVisibility(true);
        }
        finally
        {
            _isLoading = false;
            SetLoadMoreEnabled(true);
        }
    }

    private void GridMode_Click(object sender, RoutedEventArgs e)
    {
        GridModeButton.IsChecked = true;
        ListModeButton.IsChecked = false;
        PostsGridView.Visibility = Visibility.Visible;
        PostsListView.Visibility = Visibility.Collapsed;
    }

    private void ListMode_Click(object sender, RoutedEventArgs e)
    {
        GridModeButton.IsChecked = false;
        ListModeButton.IsChecked = true;
        PostsGridView.Visibility = Visibility.Collapsed;
        PostsListView.Visibility = Visibility.Visible;
    }

    private void SetLoadMoreVisibility(bool visible)
    {
        var value = visible ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreGridButton.Visibility = value;
        LoadMoreListButton.Visibility = value;
    }

    private void SetLoadMoreEnabled(bool enabled)
    {
        LoadMoreGridButton.IsEnabled = enabled;
        LoadMoreListButton.IsEnabled = enabled;
        LoadMoreGridButton.Content = enabled ? "Load more bookmarks" : "Loading…";
        LoadMoreListButton.Content = enabled ? "Load more bookmarks" : "Loading…";
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

            var file = await _imageDownloadService.SaveImageAsync(imageUri, WinRT.Interop.WindowNative.GetWindowHandle(window));
            if (file is not null)
            {
                StatusText.Text = $"Image saved to {file.Path}";
            }
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

    private void PostImage_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not FeedPostViewModel post || post.MediaSource is null)
        {
            return;
        }

        PixelfedClient.Views.PhotoViewer.Show(XamlRoot, post.MediaSource, post.DisplayName);
        e.Handled = true;
    }
}
