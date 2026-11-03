using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PixelfedClient.Services;
using PixelfedClient.Views;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;

namespace PixelfedClient.Pages;

public sealed partial class ProfilePage : Page
{
    private readonly PixelfedProfileService _profileService = new();
    private readonly PixelfedCollectionsService _collectionsService = new();
    private readonly PixelfedImageDownloadService _imageDownloadService = new();
    private readonly ObservableCollection<FeedPostViewModel> _posts = new();
    private Uri? _instanceUri;
    private CredentialVault.SavedPixelfedCredentials? _credentials;
    private string? _accountId;
    private string? _postsCursor;
    private ScrollViewer? _postsScrollViewer;
    private bool _hasMorePosts;
    private bool _isLoadingPosts;

    public ProfilePage()
    {
        InitializeComponent();
        OwnPostsGridView.ItemsSource = _posts;
        OwnPostsGridView.Loaded += OwnPostsGridView_Loaded;
        Loaded += ProfilePage_Loaded;
    }

    private async void ProfilePage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ProfilePage_Loaded;
        await LoadProfileAsync();
    }

    private async Task LoadProfileAsync()
    {
        var instanceUrl = (string?)ApplicationData.Current.LocalSettings.Values["pixelfed.instance"];
        if (!Uri.TryCreate(instanceUrl, UriKind.Absolute, out _instanceUri))
        {
            ProfileStatusText.Text = "Choose a Pixelfed server and sign in from Settings first.";
            return;
        }

        _credentials = CredentialVault.GetCredentialsFor(_instanceUri.Host);
        if (_credentials is null)
        {
            ProfileStatusText.Text = "Sign in to view your profile.";
            return;
        }

        CollectionPermissionPanel.Visibility = CredentialVault.HasWriteScope(_instanceUri.Host)
            ? Visibility.Collapsed
            : Visibility.Visible;
        ProfileStatusText.Text = "Loading your posts…";

        try
        {
            var profile = await _profileService.GetOwnProfileAsync(_instanceUri, _credentials.AccessToken);
            _accountId = profile.AccountId;
            ProfileDisplayNameText.Text = profile.DisplayName;
            ProfileUsernameText.Text = profile.Username;
            ProfileBioText.Text = profile.Bio;
            ProfileAvatarImage.Source = profile.AvatarUri is null
                ? null
                : new BitmapImage(profile.AvatarUri);

            _posts.Clear();
            _postsCursor = null;
            _hasMorePosts = true;
            await LoadNextPostsPageAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            _posts.Clear();
            ProfileStatusText.Text = ex.Message;
        }
    }

    private void OwnPostsGridView_Loaded(object sender, RoutedEventArgs e)
    {
        AttachPostsScrollViewer();
    }

    private async void PostsScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_postsScrollViewer is not null
            && _postsScrollViewer.VerticalOffset + _postsScrollViewer.ViewportHeight
                >= _postsScrollViewer.ScrollableHeight - 500)
        {
            await LoadNextPostsPageAsync();
        }
    }

    private void AttachPostsScrollViewer()
    {
        var viewer = FindScrollViewer(OwnPostsGridView);
        if (ReferenceEquals(viewer, _postsScrollViewer))
        {
            CheckPostsNeedMore();
            return;
        }

        if (_postsScrollViewer is not null)
        {
            _postsScrollViewer.ViewChanged -= PostsScrollViewer_ViewChanged;
        }

        _postsScrollViewer = viewer;
        if (_postsScrollViewer is not null)
        {
            _postsScrollViewer.ViewChanged += PostsScrollViewer_ViewChanged;
            CheckPostsNeedMore();
        }
    }

    private void CheckPostsNeedMore()
    {
        if (!_hasMorePosts || _isLoadingPosts || _postsScrollViewer is null)
        {
            return;
        }

        if (_posts.Count == 0
            || _postsScrollViewer.VerticalOffset + _postsScrollViewer.ViewportHeight
                >= _postsScrollViewer.ScrollableHeight - 500)
        {
            _ = LoadNextPostsPageAsync();
        }
    }

    private async void LoadMoreProfilePosts_Click(object sender, RoutedEventArgs e)
    {
        await LoadNextPostsPageAsync();
    }

    private async Task LoadNextPostsPageAsync()
    {
        if (_isLoadingPosts || !_hasMorePosts || _instanceUri is null
            || _credentials is null || string.IsNullOrWhiteSpace(_accountId))
        {
            return;
        }

        _isLoadingPosts = true;
        LoadMoreProfilePostsButton.IsEnabled = false;
        LoadMoreProfilePostsButton.Content = "Loading posts…";
        ProfileStatusText.Text = _posts.Count == 0
            ? "Loading your posts…"
            : $"Your posts · {_posts.Count} shown · loading more…";

        try
        {
            var page = await _profileService.GetOwnPostsPageAsync(
                _instanceUri,
                _credentials.AccessToken,
                _accountId,
                _postsCursor);
            _postsCursor = page.NextMaxId;
            _hasMorePosts = page.HasMore && !string.IsNullOrWhiteSpace(_postsCursor);
            foreach (var post in page.Posts)
            {
                _posts.Add(new FeedPostViewModel(post));
            }

            LoadMoreProfilePostsButton.Visibility = _hasMorePosts ? Visibility.Visible : Visibility.Collapsed;
            LoadMoreProfilePostsButton.Content = "Load more posts";
            ProfileStatusText.Text = _posts.Count == 0
                ? "You don’t have any photo posts yet."
                : _hasMorePosts
                    ? $"Your posts · {_posts.Count} shown · scroll to load more"
                    : $"Your posts · all {_posts.Count} photo posts loaded";

            DispatcherQueue.TryEnqueue(CheckPostsNeedMore);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            ProfileStatusText.Text = ex.Message;
            LoadMoreProfilePostsButton.Content = "Retry loading posts";
            LoadMoreProfilePostsButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _isLoadingPosts = false;
            LoadMoreProfilePostsButton.IsEnabled = true;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            var descendant = FindScrollViewer(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void OwnPostsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FeedPostViewModel post)
        {
            return;
        }

        PostDetailView.DataContext = post;
        OwnPostsGridView.Visibility = Visibility.Collapsed;
        PostDetailView.Visibility = Visibility.Visible;
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
                ProfileStatusText.Text = $"Image saved to {file.Path}";
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Couldn’t download image", ex.Message);
        }
    }

    private void BackToGrid_Click(object sender, RoutedEventArgs e)
    {
        PostDetailView.Visibility = Visibility.Collapsed;
        OwnPostsGridView.Visibility = Visibility.Visible;
        PostDetailView.DataContext = null;
    }

    private async void EnableCollectionAccess_Click(object sender, RoutedEventArgs e)
    {
        await ReauthorizeAsync();
    }

    private async void AddToCollection_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetPost(sender, out var post))
        {
            return;
        }

        if (!await EnsureCollectionAccessAsync())
        {
            return;
        }

        if (!post.IsCollectionEligible)
        {
            await ShowMessageAsync(
                "This post can’t be added",
                "Pixelfed collections accept your public or unlisted photo, album, and video posts.");
            return;
        }

        try
        {
            var collections = await _collectionsService.GetOwnCollectionsAsync(_instanceUri!, _credentials!.AccessToken);
            if (collections.Count == 0)
            {
                await ShowMessageAsync("No collections yet", "Choose “Create collection from this post…” to make one and add this post.");
                return;
            }

            var selected = await ChooseCollectionAsync(collections, "Add to collection");
            if (selected is null)
            {
                return;
            }

            await _collectionsService.AddPostAsync(_instanceUri!, _credentials.AccessToken, selected.Id, post.PostId);
            ProfileStatusText.Text = selected.IsPublished
                ? $"Added to “{selected.Title}”."
                : $"Added to “{selected.Title}”. This collection is still a draft; publish it on Pixelfed when you’re ready.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            await ShowMessageAsync("Couldn’t add this post", ex.Message);
        }
    }

    private async void CreateCollection_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetPost(sender, out var post))
        {
            return;
        }

        if (!await EnsureCollectionAccessAsync())
        {
            return;
        }

        if (!post.IsCollectionEligible)
        {
            await ShowMessageAsync(
                "This post can’t be added",
                "Pixelfed collections accept your public or unlisted photo, album, and video posts.");
            return;
        }

        try
        {
            var before = await _collectionsService.GetOwnCollectionsAsync(_instanceUri!, _credentials!.AccessToken);
            var createDialog = new CollectionCreationDialog(_instanceUri!)
            {
                XamlRoot = XamlRoot,
            };
            var result = await createDialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            var after = await _collectionsService.GetOwnCollectionsAsync(_instanceUri!, _credentials.AccessToken);
            var oldIds = before.Select(collection => collection.Id).ToHashSet(StringComparer.Ordinal);
            var newlyCreated = after.Where(collection => !oldIds.Contains(collection.Id)).ToList();
            var selected = newlyCreated.Count == 1
                ? newlyCreated[0]
                : await ChooseCollectionAsync(after, "Choose the collection for this post");

            if (selected is null)
            {
                ProfileStatusText.Text = "No new collection was found. Refresh your collections and try again.";
                return;
            }

            await _collectionsService.AddPostAsync(_instanceUri!, _credentials.AccessToken, selected.Id, post.PostId);
            ProfileStatusText.Text = selected.IsPublished
                ? $"Created “{selected.Title}” and added this post."
                : $"Added this post to “{selected.Title}”. It’s a draft; publish it on Pixelfed when you’re ready.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            await ShowMessageAsync("Couldn’t create the collection", ex.Message);
        }
    }

    private async Task<bool> EnsureCollectionAccessAsync()
    {
        if (_instanceUri is null)
        {
            return false;
        }

        _credentials = CredentialVault.GetCredentialsFor(_instanceUri.Host);
        if (_credentials is not null && CredentialVault.HasWriteScope(_instanceUri.Host))
        {
            return true;
        }

        return await ReauthorizeAsync();
    }

    private async Task<bool> ReauthorizeAsync()
    {
        var dialog = new ConnectDialog
        {
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
        if (_instanceUri is null)
        {
            var instanceUrl = (string?)ApplicationData.Current.LocalSettings.Values["pixelfed.instance"];
            Uri.TryCreate(instanceUrl, UriKind.Absolute, out _instanceUri);
        }

        if (_instanceUri is null)
        {
            return false;
        }

        _credentials = CredentialVault.GetCredentialsFor(_instanceUri.Host);
        CollectionPermissionPanel.Visibility = CredentialVault.HasWriteScope(_instanceUri.Host)
            ? Visibility.Collapsed
            : Visibility.Visible;
        return _credentials is not null && CredentialVault.HasWriteScope(_instanceUri.Host);
    }

    private async Task<PixelfedCollection?> ChooseCollectionAsync(
        IReadOnlyList<PixelfedCollection> collections,
        string title)
    {
        if (collections.Count == 0)
        {
            return null;
        }

        var filteredCollections = new ObservableCollection<PixelfedCollection>(collections);
        var selector = new ListView
        {
            ItemsSource = filteredCollections,
            DisplayMemberPath = nameof(PixelfedCollection.DisplayName),
            SelectedIndex = 0,
            MinWidth = 340,
            MaxHeight = 360,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var searchBox = new TextBox
        {
            PlaceholderText = "Search collections by name or description",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(searchBox);
        content.Children.Add(selector);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = "Select collection",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        selector.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = selector.SelectedItem is not null;
        searchBox.TextChanged += (_, _) =>
        {
            var selectedId = (selector.SelectedItem as PixelfedCollection)?.Id;
            var query = searchBox.Text.Trim();
            var matches = string.IsNullOrWhiteSpace(query)
                ? collections
                : collections.Where(collection =>
                    collection.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || collection.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || collection.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            filteredCollections.Clear();
            foreach (var collection in matches)
            {
                filteredCollections.Add(collection);
            }

            selector.SelectedItem = filteredCollections.FirstOrDefault(collection => collection.Id == selectedId)
                ?? filteredCollections.FirstOrDefault();
            dialog.IsPrimaryButtonEnabled = selector.SelectedItem is not null;
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? selector.SelectedItem as PixelfedCollection
            : null;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    private static bool TryGetPost(object sender, out FeedPostViewModel post)
    {
        var element = sender as FrameworkElement;
        post = element?.Tag as FeedPostViewModel
            ?? element?.DataContext as FeedPostViewModel
            ?? null!;
        return post is not null;
    }
}
