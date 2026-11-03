using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PixelfedClient.Services;
using System.Collections.ObjectModel;
using Windows.Storage;

namespace PixelfedClient.Pages;

public sealed partial class CollectionsPage : Page
{
    private readonly PixelfedCollectionsService _collectionsService = new();
    private readonly PixelfedImageDownloadService _imageDownloadService = new();
    private readonly ObservableCollection<CollectionCardViewModel> _collections = new();
    private readonly ObservableCollection<FeedPostViewModel> _posts = new();
    private IReadOnlyList<CollectionCardViewModel> _allCollections = [];
    private Uri? _instanceUri;
    private CredentialVault.SavedPixelfedCredentials? _credentials;
    private PixelfedCollection? _selectedCollection;

    public CollectionsPage()
    {
        InitializeComponent();
        CollectionsGridView.ItemsSource = _collections;
        CollectionPostsGridView.ItemsSource = _posts;
        Loaded += CollectionsPage_Loaded;
    }

    private async void CollectionsPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= CollectionsPage_Loaded;
        await LoadCollectionsAsync();
    }

    private async void RefreshCollections_Click(object sender, RoutedEventArgs e)
    {
        await LoadCollectionsAsync();
    }

    private async Task LoadCollectionsAsync()
    {
        var instanceUrl = (string?)ApplicationData.Current.LocalSettings.Values["pixelfed.instance"];
        if (!Uri.TryCreate(instanceUrl, UriKind.Absolute, out _instanceUri))
        {
            _collections.Clear();
            CollectionsStatusText.Text = "Choose a Pixelfed server and sign in from Settings first.";
            return;
        }

        _credentials = CredentialVault.GetCredentialsFor(_instanceUri.Host);
        if (_credentials is null)
        {
            _collections.Clear();
            CollectionsStatusText.Text = "Sign in to view your collections.";
            return;
        }

        CollectionsStatusText.Text = "Loading your collections…";
        try
        {
            var collections = await _collectionsService.GetOwnCollectionsAsync(
                _instanceUri,
                _credentials.AccessToken);
            _allCollections = collections.Select(collection => new CollectionCardViewModel(collection)).ToArray();
            ApplyCollectionsSearch();

            CollectionsStatusText.Text = collections.Count == 0
                ? "No collections yet. Create one from the ⋯ menu on one of your profile posts."
                : $"{collections.Count} collection{(collections.Count == 1 ? string.Empty : "s")}";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            _collections.Clear();
            CollectionsStatusText.Text = ex.Message;
        }
    }

    private void CollectionsSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyCollectionsSearch();

    private void ApplyCollectionsSearch()
    {
        var query = CollectionsSearchBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allCollections
            : _allCollections.Where(collection => collection.Matches(query)).ToArray();

        _collections.Clear();
        foreach (var collection in filtered)
        {
            _collections.Add(collection);
        }

        if (_allCollections.Count > 0)
        {
            CollectionsStatusText.Text = string.IsNullOrWhiteSpace(query)
                ? $"{_allCollections.Count} collection{(_allCollections.Count == 1 ? string.Empty : "s")}" 
                : $"{filtered.Count} matching collection{(filtered.Count == 1 ? string.Empty : "s")}";
        }
    }

    private async void CollectionsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CollectionCardViewModel selected)
        {
            return;
        }

        _selectedCollection = selected.Collection;
        SelectedCollectionTitleText.Text = _selectedCollection.Title;
        SelectedCollectionDescriptionText.Text = string.IsNullOrWhiteSpace(_selectedCollection.Description)
            ? $"{_selectedCollection.PostCount} posts · {(_selectedCollection.IsPublished ? "published" : "draft")}" 
            : _selectedCollection.Description;

        CollectionsGridView.Visibility = Visibility.Collapsed;
        CollectionsSearchBox.Visibility = Visibility.Collapsed;
        CollectionPostDetailPanel.Visibility = Visibility.Collapsed;
        CollectionDetailPanel.Visibility = Visibility.Visible;
        CollectionPostsGridView.Visibility = Visibility.Collapsed;
        CollectionPostsStatusText.Visibility = Visibility.Visible;
        CollectionPostsStatusText.Text = "Loading posts in this collection…";
        _posts.Clear();

        if (_instanceUri is null || _credentials is null)
        {
            CollectionPostsStatusText.Text = "Sign in to view posts in this collection.";
            return;
        }

        try
        {
            var posts = await _collectionsService.GetCollectionItemsAsync(
                _instanceUri,
                _credentials.AccessToken,
                _selectedCollection.Id);
            foreach (var post in posts)
            {
                _posts.Add(new FeedPostViewModel(post));
            }

            CollectionPostsStatusText.Text = posts.Count == 0
                ? "This collection has no posts yet."
                : $"{posts.Count} post{(posts.Count == 1 ? string.Empty : "s")}";
            CollectionPostsGridView.Visibility = posts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            CollectionPostsStatusText.Text = ex.Message;
        }
    }

    private void CollectionPostsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FeedPostViewModel post)
        {
            return;
        }

        CollectionPostDetailPanel.DataContext = post;
        CollectionDetailPanel.Visibility = Visibility.Collapsed;
        CollectionPostDetailPanel.Visibility = Visibility.Visible;
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

            var file = await _imageDownloadService.SaveImageAsync(
                imageUri,
                WinRT.Interop.WindowNative.GetWindowHandle(window));
            if (file is not null)
            {
                CollectionPostsStatusText.Text = $"Image saved to {file.Path}";
            }
        }
        catch (Exception ex)
        {
            await ShowDownloadErrorAsync(ex.Message);
        }
    }

    private async Task ShowDownloadErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Couldn’t download image",
            Content = message,
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    private void BackToCollections_Click(object sender, RoutedEventArgs e)
    {
        CollectionPostDetailPanel.Visibility = Visibility.Collapsed;
        CollectionDetailPanel.Visibility = Visibility.Collapsed;
        CollectionsGridView.Visibility = Visibility.Visible;
        CollectionsSearchBox.Visibility = Visibility.Visible;
        CollectionPostDetailPanel.DataContext = null;
    }

    private void BackToCollectionPosts_Click(object sender, RoutedEventArgs e)
    {
        CollectionPostDetailPanel.Visibility = Visibility.Collapsed;
        CollectionDetailPanel.Visibility = Visibility.Visible;
        CollectionPostDetailPanel.DataContext = null;
    }

    private sealed class CollectionCardViewModel
    {
        public CollectionCardViewModel(PixelfedCollection collection)
        {
            Collection = collection;
            ThumbnailSource = collection.ThumbnailUri is null
                ? null
                : new BitmapImage(collection.ThumbnailUri);
            ThumbnailVisibility = ThumbnailSource is null ? Visibility.Collapsed : Visibility.Visible;
            PlaceholderVisibility = ThumbnailSource is null ? Visibility.Visible : Visibility.Collapsed;
            Title = collection.Title;
            Description = collection.Description;
            DetailsLabel = $"{collection.PostCount} posts · {(collection.IsPublished ? "published" : "draft")}";
        }

        public PixelfedCollection Collection { get; }
        public string Title { get; }
        public string Description { get; }
        public string DetailsLabel { get; }
        public bool Matches(string query) =>
            Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            || DetailsLabel.Contains(query, StringComparison.OrdinalIgnoreCase);
        public ImageSource? ThumbnailSource { get; }
        public Visibility ThumbnailVisibility { get; }
        public Visibility PlaceholderVisibility { get; }
    }
}
