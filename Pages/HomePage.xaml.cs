using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using PixelfedClient.Services;
using PixelfedClient.Views;
using System.ComponentModel;
using System.Collections.ObjectModel;
using Windows.Storage;
using Windows.System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PixelfedClient.Pages;

public sealed partial class HomePage : Page
{
    private const double FeedPrefetchDistance = 1200;
    private readonly PixelfedTimelineService _timelineService = new();
    private readonly PixelfedSearchService _searchService = new();
    private readonly HolosSearchService _holosSearchService = new();
    private readonly PixelfedPostActionsService _postActionsService = new();
    private readonly PixelfedImageDownloadService _imageDownloadService = new();
    private readonly ObservableCollection<FeedPostViewModel> _posts = new();
    private readonly ObservableCollection<FeedSearchSuggestion> _searchSuggestions = new();
    private readonly ObservableCollection<FollowedHashtagChip> _followedHashtagChips = new();
    private readonly Dictionary<string, DateTimeOffset?> _lastShownPostTimeByAuthor = new(StringComparer.OrdinalIgnoreCase);
    private Uri? _instanceUri;
    private string? _accessToken;
    private string? _pageCursor;
    private PixelfedFeedKind _activeFeedKind = PixelfedFeedKind.Local;
    private string? _activeFollowedHashtag;
    private FediverseAccountKind? _accountTypeFilter;
    private int? _savedAccountTypeSelectionForHashtag;
    private bool _isApplyingHashtagAccountSelection;
    private ScrollViewer? _feedScrollViewer;
    private bool _showGrid = true;
    private bool _showCompactGrid;
    private int _gridColumns = 4;
    private ItemsWrapGrid? _feedItemsWrapGrid;
    private ItemsWrapGrid? _compactFeedItemsWrapGrid;
    private bool _hasMore = true;
    private bool _isLoading;
    private double _gridCardHeight = 600;
    private bool _isListeningForPreferences;
    private CancellationTokenSource? _suggestionCancellation;
    private int _suggestionRequestVersion;
    private string? _pendingNotificationStatusId;
    private int _swipeViewerPostIndex;
    private Windows.Foundation.Point? _swipeStartPoint;

    public HomePage()
    {
        InitializeComponent();
        var refreshAccelerator = new KeyboardAccelerator { Key = VirtualKey.F5 };
        refreshAccelerator.Invoked += RefreshKeyboardAccelerator_Invoked;
        KeyboardAccelerators.Add(refreshAccelerator);
        var settings = ApplicationData.Current.LocalSettings.Values;
        FeedSelector.SelectedIndex = ReadOption(settings["feed.defaultKind"], 1, 0, 2);
        AccountTypeSelector.SelectedIndex = ReadOption(settings["feed.defaultAccountType"], 1, 0, 2);
        _gridColumns = ReadOption(settings["feed.gridColumns"], 4, 2, 8);
        GridColumnCountText.Text = $"{_gridColumns} columns";
        FeedGridView.ItemsSource = _posts;
        CompactFeedGridView.ItemsSource = _posts;
        FeedListView.ItemsSource = _posts;
        FeedSearchBox.ItemsSource = _searchSuggestions;
        FollowedHashtagsItemsControl.ItemsSource = _followedHashtagChips;
        FeedGridView.Loaded += FeedGridView_Loaded;
        FeedGridView.SizeChanged += FeedGridView_SizeChanged;
        CompactFeedGridView.Loaded += CompactFeedGridView_Loaded;
        CompactFeedGridView.SizeChanged += CompactFeedGridView_SizeChanged;
        FeedListView.Loaded += FeedListView_Loaded;
        Loaded += HomePage_Loaded;
        Unloaded += HomePage_Unloaded;
    }

    private static int ReadOption(object? value, int fallback, int minimum, int maximum) =>
        value is int selected && selected >= minimum && selected <= maximum ? selected : fallback;

    public async Task RefreshAsync() => await LoadTimelineAsync();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pendingNotificationStatusId = e.Parameter as string;
        if (IsLoaded && !string.IsNullOrWhiteSpace(_pendingNotificationStatusId))
        {
            var statusId = _pendingNotificationStatusId;
            _pendingNotificationStatusId = null;
            await OpenNotificationPostAsync(statusId);
        }
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_isListeningForPreferences)
        {
            ClientPreferences.FeedPreferencesChanged += ClientPreferences_FeedPreferencesChanged;
            _isListeningForPreferences = true;
        }

        await LoadTimelineAsync();
        if (!string.IsNullOrWhiteSpace(_pendingNotificationStatusId))
        {
            var statusId = _pendingNotificationStatusId;
            _pendingNotificationStatusId = null;
            await OpenNotificationPostAsync(statusId);
        }
    }

    private async Task OpenNotificationPostAsync(string statusId)
    {
        if (_instanceUri is null || string.IsNullOrWhiteSpace(_accessToken))
        {
            FeedStatusText.Text = "Sign in to open this notification post in the feed.";
            return;
        }

        FeedSearchBox.Text = string.Empty;
        FeedStatusText.Text = "Opening the notification post in your feed…";
        try
        {
            var post = await _timelineService.GetStatusAsync(_instanceUri, _accessToken, statusId);
            if (post is null)
            {
                FeedStatusText.Text = "This post is unavailable from your Pixelfed server.";
                return;
            }

            var viewModel = _posts.FirstOrDefault(existing => existing.PostId == post.Id);
            if (viewModel is null)
            {
                viewModel = new FeedPostViewModel(post);
                _posts.Insert(0, viewModel);
            }

            FeedGridView.ScrollIntoView(viewModel);
            FeedListView.ScrollIntoView(viewModel);
            FeedStatusText.Text = "Opened the notification post in your feed.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            FeedStatusText.Text = ex.Message;
        }
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_isListeningForPreferences)
        {
            ClientPreferences.FeedPreferencesChanged -= ClientPreferences_FeedPreferencesChanged;
            _isListeningForPreferences = false;
        }
    }

    private async void ClientPreferences_FeedPreferencesChanged(object? sender, EventArgs e)
    {
        if (IsLoaded)
        {
            await LoadTimelineAsync();
        }
    }

    private async void RefreshFeed_Click(object sender, RoutedEventArgs e)
    {
        _suggestionCancellation?.Cancel();
        _searchSuggestions.Clear();
        FeedSearchBox.Text = string.Empty;
        await LoadTimelineAsync();
    }

    private void SwipeView_Click(object sender, RoutedEventArgs e)
    {
        if (_posts.Count == 0)
        {
            FeedStatusText.Text = "Load some posts before opening swipe view.";
            return;
        }

        _swipeViewerPostIndex = 0;
        UpdateSwipeViewerPost();
        SwipeViewerPanel.Visibility = Visibility.Visible;
        SwipeViewerPanel.Focus(FocusState.Programmatic);
    }

    private void CloseSwipeViewer_Click(object sender, RoutedEventArgs e)
    {
        SwipeViewerPanel.Visibility = Visibility.Collapsed;
    }

    private async void PreviousSwipePost_Click(object sender, RoutedEventArgs e) =>
        await MoveSwipeViewerAsync(-1);

    private async void NextSwipePost_Click(object sender, RoutedEventArgs e) =>
        await MoveSwipeViewerAsync(1);

    private async Task MoveSwipeViewerAsync(int direction)
    {
        var targetIndex = _swipeViewerPostIndex + direction;
        if (targetIndex < 0)
        {
            return;
        }

        if (targetIndex >= _posts.Count && direction > 0 && _hasMore)
        {
            SwipeViewerPositionText.Text = "Loading more posts…";
            await LoadNextPageAsync();
            targetIndex = _swipeViewerPostIndex + direction;
        }

        if (targetIndex >= _posts.Count)
        {
            SwipeViewerPositionText.Text = $"End of loaded feed · {_posts.Count} posts";
            return;
        }

        _swipeViewerPostIndex = targetIndex;
        UpdateSwipeViewerPost();
    }

    private void UpdateSwipeViewerPost()
    {
        if (_posts.Count == 0)
        {
            return;
        }

        _swipeViewerPostIndex = Math.Clamp(_swipeViewerPostIndex, 0, _posts.Count - 1);
        var post = _posts[_swipeViewerPostIndex];
        SwipeViewerImage.Source = post.MediaItems.Count > 0
            ? post.MediaItems[0].ImageSource
            : post.MediaSource;
        SwipeViewerAuthorText.Text = $"{post.DisplayName}  {post.Username} · {post.CreatedAtLabel}";
        SwipeViewerCaptionText.Text = post.Caption;
        SwipeViewerActions.Post = post;
        SwipeViewerPositionText.Text = $"{_swipeViewerPostIndex + 1} / {_posts.Count}";
    }

    private void SwipeViewer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down || e.Key == VirtualKey.PageDown)
        {
            e.Handled = true;
            _ = MoveSwipeViewerAsync(1);
        }
        else if (e.Key == VirtualKey.Up || e.Key == VirtualKey.PageUp)
        {
            e.Handled = true;
            _ = MoveSwipeViewerAsync(-1);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            SwipeViewerPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SwipeViewerImage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _swipeStartPoint = e.GetCurrentPoint(SwipeViewerImage).Position;
        SwipeViewerImage.CapturePointer(e.Pointer);
    }

    private async void SwipeViewerImage_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        SwipeViewerImage.ReleasePointerCapture(e.Pointer);
        if (_swipeStartPoint is not { } startPoint)
        {
            return;
        }

        _swipeStartPoint = null;
        var endPoint = e.GetCurrentPoint(SwipeViewerImage).Position;
        var verticalDistance = endPoint.Y - startPoint.Y;
        if (Math.Abs(verticalDistance) >= 80)
        {
            await MoveSwipeViewerAsync(verticalDistance < 0 ? 1 : -1);
        }
    }

    private void SwipeViewerImage_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_posts.Count == 0)
        {
            return;
        }

        var post = _posts[Math.Clamp(_swipeViewerPostIndex, 0, _posts.Count - 1)];
        if (SwipeViewerImage.Source is { } source)
        {
            PixelfedClient.Views.PhotoViewer.Show(XamlRoot, source, post.DisplayName);
            e.Handled = true;
        }
    }

    private void RefreshKeyboardAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        RefreshFeed_Click(this, new RoutedEventArgs());
    }

    private async void PostSeeMore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FeedPostViewModel post)
        {
            return;
        }

        await ShowPostDetailsAsync(post);
    }

    private async void CompactFeedGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FeedPostViewModel post)
        {
            await ShowPostDetailsAsync(post);
        }
    }

    private async void PostCommentRequested(object sender, RoutedEventArgs e)
    {
        if (sender is PostActionsBar { Post: { } post })
        {
            await ShowPostDetailsAsync(post, openCommentEditor: true);
        }
    }

    private async Task ShowPostDetailsAsync(FeedPostViewModel post, bool openCommentEditor = false)
    {
        var content = new StackPanel { Spacing = 12, Width = 560 };
        if (post.MediaItems.Count > 0)
        {
            content.Children.Add(new PostMediaCarousel
            {
                DataContext = post,
                Width = 520,
                MaxHeight = 480,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = $"{post.DisplayName} · {post.Username} · {post.CreatedAtLabel}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = post.Caption,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        });

        var actionBar = new PostActionsBar { Post = post };
        var commentComposer = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var commentEditor = new TextBox
        {
            AcceptsReturn = true,
            MaxLength = 500,
            MinHeight = 112,
            PlaceholderText = "Write a comment…",
            TextWrapping = TextWrapping.Wrap,
        };
        var commentStatus = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };
        var submitCommentButton = new Button
        {
            Content = "Post comment",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        commentComposer.Children.Add(commentEditor);
        commentComposer.Children.Add(commentStatus);
        commentComposer.Children.Add(submitCommentButton);
        actionBar.CommentRequested += (_, _) =>
        {
            commentComposer.Visibility = Visibility.Visible;
            commentEditor.Focus(FocusState.Programmatic);
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = AppThemeService.SelectedTheme,
            Title = "Post details",
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = 680,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            CloseButtonText = "Close",
        };
        content.Children.Add(actionBar);
        content.Children.Add(commentComposer);
        submitCommentButton.Click += async (_, _) =>
        {
            var comment = commentEditor.Text.Trim();
            if (comment.Length == 0)
            {
                commentStatus.Text = "Write a comment before posting it.";
                commentEditor.Focus(FocusState.Programmatic);
                return;
            }

            if (_instanceUri is null || string.IsNullOrEmpty(_accessToken))
            {
                commentStatus.Text = "Sign in to your Pixelfed account before commenting.";
                return;
            }

            submitCommentButton.IsEnabled = false;
            commentStatus.Text = "Posting comment…";
            try
            {
                await _postActionsService.ReplyAsync(_instanceUri, _accessToken, post, comment);
                post.MarkReplyPosted();
                commentEditor.Text = string.Empty;
                commentStatus.Text = "Comment posted.";
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                commentStatus.Text = ex.Message;
            }
            finally
            {
                submitCommentButton.IsEnabled = true;
            }
        };

        if (openCommentEditor)
        {
            commentComposer.Visibility = Visibility.Visible;
        }

        await dialog.ShowAsync();
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
                FeedStatusText.Text = $"Image saved to {file.Path}";
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

    private async void BackToHomeFeed_Click(object sender, RoutedEventArgs e)
    {
        _suggestionCancellation?.Cancel();
        _searchSuggestions.Clear();
        FeedSearchBox.Text = string.Empty;
        RestoreAccountTypeSelectionAfterHashtag();
        await LoadTimelineAsync();
    }

    private async void FeedSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.AddedItems.Count == 0)
        {
            return;
        }

        _suggestionCancellation?.Cancel();
        _searchSuggestions.Clear();
        FeedSearchBox.Text = string.Empty;
        await LoadTimelineAsync();
    }

    private async void FollowedHashtag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || string.IsNullOrWhiteSpace(tag)
            || _activeFeedKind != PixelfedFeedKind.FollowedHashtags)
        {
            return;
        }

        if (_instanceUri is not null && !string.IsNullOrEmpty(_accessToken))
        {
            await LoadGlobalHashtagAsync(_instanceUri, _accessToken, tag);
        }
    }

    private async void AccountTypeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _accountTypeFilter = AccountTypeSelector.SelectedIndex switch
        {
            1 => FediverseAccountKind.Pixelfed,
            2 => FediverseAccountKind.Mastodon,
            _ => null,
        };

        if (_isApplyingHashtagAccountSelection || !IsLoaded || e.AddedItems.Count == 0)
        {
            return;
        }

        if (_activeFollowedHashtag is not null
            && _instanceUri is not null
            && !string.IsNullOrEmpty(_accessToken))
        {
            _savedAccountTypeSelectionForHashtag = AccountTypeSelector.SelectedIndex;
            await LoadGlobalHashtagAsync(
                _instanceUri,
                _accessToken,
                _activeFollowedHashtag,
                applyGlobalAccountSelection: false);
            return;
        }

        await LoadTimelineAsync();
    }

    private async void FeedSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        var query = sender.Text.Trim();
        _suggestionCancellation?.Cancel();
        _suggestionCancellation?.Dispose();
        _suggestionCancellation = null;
        _searchSuggestions.Clear();

        if (query.Length == 0)
        {
            return;
        }

        var instanceUrl = (string?)ApplicationData.Current.LocalSettings.Values["pixelfed.instance"];
        if (!Uri.TryCreate(instanceUrl, UriKind.Absolute, out var instanceUri))
        {
            return;
        }

        var credentials = CredentialVault.GetCredentialsFor(instanceUri.Host);
        if (credentials is null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _suggestionCancellation = cancellation;
        var requestVersion = ++_suggestionRequestVersion;
        try
        {
            await Task.Delay(250, cancellation.Token);
            var suggestions = await _searchService.GetSuggestionsAsync(
                instanceUri,
                credentials.AccessToken,
                query,
                cancellation.Token);
            IReadOnlyList<string> federatedHashtags = [];
            try
            {
                federatedHashtags = await _holosSearchService.GetHashtagSuggestionsAsync(query, cancellation.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                // Keep the instance's account and hashtag suggestions available if the public search is unavailable.
            }

            if (cancellation.IsCancellationRequested || requestVersion != _suggestionRequestVersion)
            {
                return;
            }

            if (suggestions.Accounts.Count > 0)
            {
                _searchSuggestions.Add(FeedSearchSuggestion.Header("People"));
                foreach (var account in suggestions.Accounts)
                {
                    _searchSuggestions.Add(FeedSearchSuggestion.ForAccount(account));
                }
            }

            var hashtags = suggestions.Hashtags
                .Concat(federatedHashtags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (hashtags.Length > 0)
            {
                _searchSuggestions.Add(FeedSearchSuggestion.Header("Hashtags · cross-instance", showDivider: _searchSuggestions.Count > 0));
                foreach (var hashtag in hashtags)
                {
                    _searchSuggestions.Add(FeedSearchSuggestion.ForHashtag(hashtag));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke has started a more relevant suggestion request.
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            if (requestVersion == _suggestionRequestVersion)
            {
                FeedStatusText.Text = ex.Message;
            }
        }
    }

    private void FeedSearch_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not FeedSearchSuggestion { IsHeader: false } suggestion)
        {
            return;
        }

        sender.Text = suggestion.QueryText;
    }

    private async void FeedSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is FeedSearchSuggestion { IsHeader: false } suggestion)
        {
            _suggestionCancellation?.Cancel();
            _searchSuggestions.Clear();
            await LoadSearchSuggestionAsync(suggestion);
            return;
        }

        var query = sender.Text.Trim();
        if (Uri.TryCreate(query, UriKind.Absolute, out var postUri) && postUri.Scheme == Uri.UriSchemeHttps)
        {
            _searchSuggestions.Clear();
            await LoadSearchUrlAsync(query);
        }
        else if (query.StartsWith('#') && query.Length > 1)
        {
            _searchSuggestions.Clear();
            await LoadSearchSuggestionAsync(FeedSearchSuggestion.ForHashtag(query.TrimStart('#')));
        }
        else
        {
            FeedStatusText.Text = "Choose a suggested person or hashtag. Pixelfed does not provide full-text post search.";
        }
    }

    private async Task LoadSearchSuggestionAsync(FeedSearchSuggestion suggestion)
    {
        if (!TryGetSearchContext(out var instanceUri, out var accessToken))
        {
            return;
        }

        if (suggestion.Hashtag is not null)
        {
            await LoadGlobalHashtagAsync(instanceUri, accessToken, suggestion.Hashtag);
            return;
        }

        await LoadSearchResultsAsync(
            suggestion.Account!.Username,
            () => _searchService.GetPhotoPostsForAccountAsync(instanceUri, accessToken, suggestion.Account!.Id));
    }

    private async Task LoadGlobalHashtagAsync(
        Uri instanceUri,
        string accessToken,
        string tag,
        bool applyGlobalAccountSelection = true)
    {
        _instanceUri = instanceUri;
        _accessToken = accessToken;
        _activeFollowedHashtag = tag.Trim().TrimStart('#');
        if (applyGlobalAccountSelection)
        {
            _savedAccountTypeSelectionForHashtag ??= AccountTypeSelector.SelectedIndex;
            _isApplyingHashtagAccountSelection = true;
            AccountTypeSelector.SelectedIndex = 0;
            _accountTypeFilter = null;
            _isApplyingHashtagAccountSelection = false;
        }
        BackToHomeFeedButton.Visibility = Visibility.Visible;
        FeedDescriptionText.Text = $"Indexed photo posts with #{_activeFollowedHashtag} across instances. Showing all account types.";
        _posts.Clear();
        _pageCursor = null;
        _hasMore = true;
        _lastShownPostTimeByAuthor.Clear();
        FeedStatusText.Text = $"Searching indexed posts tagged #{_activeFollowedHashtag}…";
        await LoadNextPageAsync();
    }

    private void RestoreAccountTypeSelectionAfterHashtag()
    {
        if (_savedAccountTypeSelectionForHashtag is not int selection)
        {
            return;
        }

        _isApplyingHashtagAccountSelection = true;
        AccountTypeSelector.SelectedIndex = selection;
        _isApplyingHashtagAccountSelection = false;
        _accountTypeFilter = selection switch
        {
            1 => FediverseAccountKind.Pixelfed,
            2 => FediverseAccountKind.Mastodon,
            _ => null,
        };
        _savedAccountTypeSelectionForHashtag = null;
    }

    private async Task LoadSearchUrlAsync(string postUrl)
    {
        if (!TryGetSearchContext(out var instanceUri, out var accessToken))
        {
            return;
        }

        await LoadSearchResultsAsync(
            "post URL",
            () => _searchService.ResolvePhotoPostAsync(instanceUri, accessToken, postUrl));
    }

    private bool TryGetSearchContext(out Uri instanceUri, out string accessToken)
    {
        var instanceUrl = (string?)ApplicationData.Current.LocalSettings.Values["pixelfed.instance"];
        if (Uri.TryCreate(instanceUrl, UriKind.Absolute, out var parsedInstance))
        {
            var credentials = CredentialVault.GetCredentialsFor(parsedInstance.Host);
            if (credentials is not null)
            {
                instanceUri = parsedInstance;
                accessToken = credentials.AccessToken;
                return true;
            }
        }

        FeedStatusText.Text = "Sign in to Pixelfed from Settings to search.";
        instanceUri = null!;
        accessToken = string.Empty;
        return false;
    }

    private async Task LoadSearchResultsAsync(
        string label,
        Func<Task<IReadOnlyList<PixelfedPost>>> fetchPosts)
    {
        BackToHomeFeedButton.Visibility = Visibility.Visible;
        _suggestionCancellation?.Cancel();
        _pageCursor = null;
        _hasMore = false;
        _lastShownPostTimeByAuthor.Clear();
        _posts.Clear();
        FeedStatusText.Text = $"Loading results for {label}…";
        try
        {
            var posts = await fetchPosts();
            foreach (var post in posts.Where(ShouldShowPost))
            {
                AddFeedPost(post);
            }

            var shownCount = _posts.Count;
            FeedStatusText.Text = shownCount == 0
                ? $"No indexed photo posts found for {label}. Holos Discover indexes public posts from discoverable Fediverse accounts."
                : $"Showing {shownCount} photo {(shownCount == 1 ? "post" : "posts")} for {label} from Holos Discover’s cross-instance index. Use Refresh to return to your feed.";
            DispatcherQueue.TryEnqueue(CheckFeedNeedsMore);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            FeedStatusText.Text = ex.Message;
        }
    }

    private void GridMode_Click(object sender, RoutedEventArgs e) => SetFeedViewMode(showGrid: true);

    private void ListMode_Click(object sender, RoutedEventArgs e) => SetFeedViewMode(showGrid: false);

    private void CompactGridMode_Click(object sender, RoutedEventArgs e)
    {
        _showGrid = false;
        _showCompactGrid = true;
        FeedGridView.Visibility = Visibility.Collapsed;
        CompactFeedGridView.Visibility = Visibility.Visible;
        FeedListView.Visibility = Visibility.Collapsed;
        GridColumnControls.Visibility = Visibility.Visible;
        GridModeButton.IsChecked = false;
        ListModeButton.IsChecked = false;
        CompactGridModeButton.IsChecked = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateGridColumns();
            UpdateActiveScrollViewer();
        });
    }

    private void SetFeedViewMode(bool showGrid)
    {
        _showGrid = showGrid;
        _showCompactGrid = false;
        FeedGridView.Visibility = showGrid ? Visibility.Visible : Visibility.Collapsed;
        CompactFeedGridView.Visibility = Visibility.Collapsed;
        FeedListView.Visibility = showGrid ? Visibility.Collapsed : Visibility.Visible;
        GridColumnControls.Visibility = showGrid ? Visibility.Visible : Visibility.Collapsed;
        GridModeButton.IsChecked = showGrid;
        ListModeButton.IsChecked = !showGrid;
        CompactGridModeButton.IsChecked = false;
        DispatcherQueue.TryEnqueue(UpdateActiveScrollViewer);
    }

    private void DecreaseGridColumns_Click(object sender, RoutedEventArgs e) => SetGridColumnCount(_gridColumns - 1);

    private void IncreaseGridColumns_Click(object sender, RoutedEventArgs e) => SetGridColumnCount(_gridColumns + 1);

    private void SetGridColumnCount(int count)
    {
        _gridColumns = Math.Clamp(count, 2, 8);
        ApplicationData.Current.LocalSettings.Values["feed.gridColumns"] = _gridColumns;
        GridColumnCountText.Text = $"{_gridColumns} columns";
        UpdateGridColumns();
    }

    private void FeedGridView_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateGridColumns();

    private void UpdateGridColumns()
    {
        _feedItemsWrapGrid ??= FindItemsWrapGrid(FeedGridView);
        _compactFeedItemsWrapGrid ??= FindItemsWrapGrid(CompactFeedGridView);
        if (_feedItemsWrapGrid is null && _compactFeedItemsWrapGrid is null)
        {
            return;
        }

        if (_feedItemsWrapGrid is not null)
        {
            _feedItemsWrapGrid.MaximumRowsOrColumns = _gridColumns;
        }
        if (_compactFeedItemsWrapGrid is not null)
        {
            _compactFeedItemsWrapGrid.MaximumRowsOrColumns = _gridColumns;
        }
        if (FeedGridView.ActualWidth > 0)
        {
            var itemWidth = Math.Max(150, (FeedGridView.ActualWidth / _gridColumns) - 8);
            if (_feedItemsWrapGrid is not null)
            {
                _feedItemsWrapGrid.ItemWidth = itemWidth;
                _feedItemsWrapGrid.ItemHeight = Math.Ceiling(itemWidth + 390);
            }
            _gridCardHeight = Math.Ceiling(itemWidth + 382);
            foreach (var post in _posts)
            {
                post.SetGridCardHeight(_gridCardHeight);
            }
        }
        if (CompactFeedGridView.ActualWidth > 0)
        {
            var itemWidth = Math.Max(150, (CompactFeedGridView.ActualWidth / _gridColumns) - 8);
            if (_compactFeedItemsWrapGrid is not null)
            {
                _compactFeedItemsWrapGrid.ItemWidth = itemWidth;
                _compactFeedItemsWrapGrid.ItemHeight = Math.Ceiling(itemWidth + 52);
            }

            foreach (var post in _posts)
            {
                post.SetCompactGridCardHeight(Math.Ceiling(itemWidth + 44));
            }
        }
    }

    private void FeedGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        args.ItemContainer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        args.ItemContainer.VerticalContentAlignment = VerticalAlignment.Stretch;
        args.ItemContainer.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private void AddFeedPost(PixelfedPost post)
    {
        var viewModel = new FeedPostViewModel(post);
        viewModel.SetGridCardHeight(_gridCardHeight);
        _posts.Add(viewModel);
    }

    private static ItemsWrapGrid? FindItemsWrapGrid(DependencyObject parent)
    {
        if (parent is ItemsWrapGrid panel)
        {
            return panel;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var found = FindItemsWrapGrid(VisualTreeHelper.GetChild(parent, index));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void FeedGridView_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateGridColumns();
        UpdateActiveScrollViewer();
    }

    private void CompactFeedGridView_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateGridColumns();
        UpdateActiveScrollViewer();
    }

    private void CompactFeedGridView_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateGridColumns();

    private void FeedListView_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateActiveScrollViewer();
    }

    private void UpdateActiveScrollViewer()
    {
        var activeFeed = _showGrid
            ? (DependencyObject)FeedGridView
            : _showCompactGrid ? CompactFeedGridView : FeedListView;
        var activeScrollViewer = FindScrollViewer(activeFeed);
        if (ReferenceEquals(activeScrollViewer, _feedScrollViewer))
        {
            return;
        }

        if (_feedScrollViewer is not null)
        {
            _feedScrollViewer.ViewChanged -= FeedScrollViewer_ViewChanged;
        }

        _feedScrollViewer = activeScrollViewer;
        if (_feedScrollViewer is not null)
        {
            _feedScrollViewer.ViewChanged += FeedScrollViewer_ViewChanged;
            CheckFeedNeedsMore();
        }
    }

    private void FeedScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_feedScrollViewer is null)
        {
            return;
        }

        if (_feedScrollViewer.VerticalOffset + _feedScrollViewer.ViewportHeight
            >= _feedScrollViewer.ScrollableHeight - FeedPrefetchDistance)
        {
            _ = LoadNextPageAsync();
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < childCount; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
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

    private async Task LoadTimelineAsync()
    {
        RestoreAccountTypeSelectionAfterHashtag();
        BackToHomeFeedButton.Visibility = Visibility.Collapsed;
        _activeFollowedHashtag = null;
        _activeFeedKind = FeedSelector.SelectedIndex switch
        {
            1 => PixelfedFeedKind.Home,
            2 => PixelfedFeedKind.FollowedHashtags,
            _ => PixelfedFeedKind.Local,
        };
        FeedDescriptionText.Text = _activeFeedKind switch
        {
            PixelfedFeedKind.Local => "Recent posts from this Pixelfed server.",
            PixelfedFeedKind.Home => "Posts from accounts you follow.",
            PixelfedFeedKind.FollowedHashtags => "Photo posts tagged with hashtags you follow. Choose a hashtag below to explore it across instances.",
            _ => "Your Pixelfed timeline.",
        };
        FollowedHashtagsPanel.Visibility = _activeFeedKind == PixelfedFeedKind.FollowedHashtags
            ? Visibility.Visible
            : Visibility.Collapsed;
        _followedHashtagChips.Clear();
        var instanceUrl = (string?)ApplicationData.Current.LocalSettings.Values["pixelfed.instance"];
        if (!Uri.TryCreate(instanceUrl, UriKind.Absolute, out _instanceUri))
        {
            FeedStatusText.Text = "Choose a Pixelfed server in Settings to connect your account.";
            return;
        }

        var credentials = CredentialVault.GetCredentialsFor(_instanceUri.Host);
        if (credentials is null)
        {
            FeedStatusText.Text = "Sign in to this Pixelfed server to load your home feed.";
            return;
        }

        _accessToken = credentials.AccessToken;
        if (_activeFeedKind == PixelfedFeedKind.FollowedHashtags)
        {
            try
            {
                var followedTags = await _timelineService.GetFollowedHashtagNamesAsync(_instanceUri, _accessToken);
                foreach (var tag in followedTags)
                {
                    _followedHashtagChips.Add(new FollowedHashtagChip(tag));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                FeedStatusText.Text = ex.Message;
            }
        }

        _posts.Clear();
        _pageCursor = null;
        _hasMore = true;
        _lastShownPostTimeByAuthor.Clear();
        FeedStatusText.Text = $"Loading your {FeedLabel(_activeFeedKind)} feed…";
        await LoadNextPageAsync();
    }

    private async Task LoadNextPageAsync()
    {
        if (_isLoading || !_hasMore || _instanceUri is null || string.IsNullOrEmpty(_accessToken))
        {
            return;
        }

        _isLoading = true;
        var previousCursor = _pageCursor;
        FeedStatusText.Text = _posts.Count == 0
            ? $"Loading {FeedLabel(_activeFeedKind)} posts…"
            : $"Showing {_posts.Count} posts · loading more…";
        try
        {
            PixelfedTimelinePage page;
            if (!string.IsNullOrWhiteSpace(_activeFollowedHashtag))
            {
                var globalPage = await _holosSearchService.GetPhotoPostsForTagPageAsync(
                    _activeFollowedHashtag,
                    _pageCursor);
                page = new PixelfedTimelinePage(globalPage.Posts, globalPage.NextCursor, globalPage.HasMore);
            }
            else
            {
                page = await _timelineService.GetTimelinePageAsync(
                    _instanceUri,
                    _accessToken,
                    _activeFeedKind,
                    _pageCursor);
            }
            _pageCursor = page.NextMaxId;
            _hasMore = page.HasMore && !string.IsNullOrEmpty(_pageCursor) && _pageCursor != previousCursor;

            foreach (var post in page.Posts.Where(ShouldShowPost))
            {
                AddFeedPost(post);
            }

            UpdateFeedStatus();

            if (_hasMore)
            {
                DispatcherQueue.TryEnqueue(CheckFeedNeedsMore);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            FeedStatusText.Text = ex.Message;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void CheckFeedNeedsMore()
    {
        if (!_hasMore || _feedScrollViewer is null)
        {
            return;
        }

        if (_posts.Count == 0
            || _feedScrollViewer.VerticalOffset + _feedScrollViewer.ViewportHeight
                >= _feedScrollViewer.ScrollableHeight - FeedPrefetchDistance)
        {
            _ = LoadNextPageAsync();
        }
    }

    private void UpdateFeedStatus()
    {
        var countLabel = _posts.Count == 1 ? "post" : "posts";
        var feedLabel = _activeFollowedHashtag is null
            ? FeedLabel(_activeFeedKind)
            : $"#{_activeFollowedHashtag}";
        if (!_hasMore)
        {
            FeedStatusText.Text = _posts.Count == 0
                ? $"No posts were found in your {feedLabel} feed."
                : $"Showing all {_posts.Count} {countLabel} in your {feedLabel} feed.";
        }
        else
        {
            FeedStatusText.Text = _posts.Count == 0
                ? "Looking for posts…"
                : $"Showing {_posts.Count} {countLabel} · scroll down for more.";
        }
    }

    private static string FeedLabel(PixelfedFeedKind feedKind) => feedKind switch
    {
        PixelfedFeedKind.Local => "local",
        PixelfedFeedKind.Home => "home",
        PixelfedFeedKind.FollowedHashtags => "followed hashtags",
        _ => "selected",
    };

    private bool MatchesAccountTypeFilter(PixelfedPost post) =>
        _accountTypeFilter is null || post.AccountKind == _accountTypeFilter.Value;

    private bool ShouldShowPost(PixelfedPost post)
    {
        if (!MatchesAccountTypeFilter(post) || (!ClientPreferences.AllowReblog && post.IsReblog))
        {
            return false;
        }

        if (!ClientPreferences.LimitFrequentPostersToOnePostPer24Hours)
        {
            return true;
        }

        var authorKey = string.IsNullOrWhiteSpace(post.AccountKey) ? post.Username : post.AccountKey;
        if (!_lastShownPostTimeByAuthor.TryGetValue(authorKey, out var previousTime))
        {
            _lastShownPostTimeByAuthor[authorKey] = post.CreatedAtUtc;
            return true;
        }

        if (previousTime is null || post.CreatedAtUtc is null)
        {
            return false;
        }

        if ((previousTime.Value - post.CreatedAtUtc.Value).Duration() < TimeSpan.FromHours(24))
        {
            return false;
        }

        _lastShownPostTimeByAuthor[authorKey] = post.CreatedAtUtc;
        return true;
    }
}

public sealed class FeedSearchSuggestion
{
    private FeedSearchSuggestion(
        string title,
        string primaryText,
        string secondaryText,
        bool isHeader,
        bool showDivider,
        PixelfedSearchAccount? account,
        string? hashtag)
    {
        Title = title;
        PrimaryText = primaryText;
        SecondaryText = secondaryText;
        IsHeader = isHeader;
        ShowDivider = showDivider;
        Account = account;
        Hashtag = hashtag;
        QueryText = hashtag is not null ? $"#{hashtag}" : account?.Username ?? string.Empty;
    }

    public string Title { get; }
    public string PrimaryText { get; }
    public string SecondaryText { get; }
    public string QueryText { get; }
    public string HashtagMarker => Hashtag is not null ? "#" : string.Empty;
    public bool IsHeader { get; }
    public bool ShowDivider { get; }
    public Microsoft.UI.Xaml.Visibility HeaderVisibility => IsHeader
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ResultVisibility => IsHeader
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;
    public Microsoft.UI.Xaml.Visibility DividerVisibility => ShowDivider
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility HashtagMarkerVisibility => Hashtag is not null
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility PersonIconVisibility => Hashtag is null && !IsHeader
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;
    public PixelfedSearchAccount? Account { get; }
    public string? Hashtag { get; }

    public static FeedSearchSuggestion Header(string title, bool showDivider = false) =>
        new(title, string.Empty, string.Empty, true, showDivider, null, null);

    public static FeedSearchSuggestion ForAccount(PixelfedSearchAccount account) =>
        new(string.Empty, account.DisplayName, account.Username, false, false, account, null);

    public static FeedSearchSuggestion ForHashtag(string hashtag) =>
        new(string.Empty, $"#{hashtag}", "Photo posts with this hashtag", false, false, null, hashtag);
}

public sealed record FollowedHashtagChip(string Name)
{
    public string Label => $"#{Name}";
}

public sealed class FeedPostViewModel : INotifyPropertyChanged
{
    private bool _isFavourited;
    private bool _isReblogged;
    private bool _isBookmarked;
    private int _favouriteCount;
    private int _shareCount;
    private int _replyCount;
    private int _currentImageIndex;
    private double _gridCardHeight = 600;
    private double _compactGridCardHeight = 440;
    private readonly string? _fallbackDownloadUrl;

    public FeedPostViewModel(PixelfedPost post)
    {
        PostId = post.Id;
        _fallbackDownloadUrl = post.DownloadImageUrl ?? post.ImageUrl;
        PostUrl = post.PostUrl;
        DisplayName = post.DisplayName;
        Username = post.Username;
        Caption = post.Caption;
        CreatedAtLabel = post.CreatedAtLabel;
        IsCollectionEligible = post.IsCollectionEligible;
        Visibility = post.Visibility;
        IsFavourited = post.IsFavourited ?? false;
        IsReblogged = post.IsReblogged ?? false;
        IsBookmarked = post.IsBookmarked ?? false;
        FavouriteCount = post.FavouriteCount ?? 0;
        ShareCount = post.ShareCount ?? 0;
        ReplyCount = post.ReplyCount ?? 0;
        CanReply = post.CommentsDisabled != true;
        CanInteractWithPost = !post.IsExternalSearchResult;
        var imageAttachments = post.ImageAttachments.Count > 0
            ? post.ImageAttachments
            : Uri.TryCreate(post.ImageUrl, UriKind.Absolute, out var fallbackImageUri)
                ? [new PixelfedImageAttachment(fallbackImageUri.AbsoluteUri, post.DownloadImageUrl ?? fallbackImageUri.AbsoluteUri)]
                : [];
        foreach (var attachment in imageAttachments)
        {
            MediaItems.Add(new FeedPostMediaViewModel(
                this,
                new BitmapImage(new Uri(attachment.DisplayUrl)),
                attachment.DownloadUrl));
        }

        MediaVisibility = MediaItems.Count > 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        MediaPlaceholderVisibility = MediaItems.Count > 0
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;
    }

    public string DisplayName { get; }
    public string Username { get; }
    public string PostId { get; }
    public string? ImageDownloadUrl => MediaItems.Count == 0
        ? _fallbackDownloadUrl
        : MediaItems[Math.Clamp(CurrentImageIndex, 0, MediaItems.Count - 1)].DownloadUrl;
    public string? PostUrl { get; }
    public bool HasPostUrl => Uri.TryCreate(PostUrl, UriKind.Absolute, out var postUri)
        && postUri.Scheme == Uri.UriSchemeHttps;
    public string Caption { get; }
    public Visibility CaptionSeeMoreVisibility => Caption.Length > 100
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string CreatedAtLabel { get; }
    public string? Visibility { get; }
    public ObservableCollection<FeedPostMediaViewModel> MediaItems { get; } = new();
    public ImageSource? MediaSource => MediaItems.Count == 0 ? null : MediaItems[Math.Clamp(CurrentImageIndex, 0, MediaItems.Count - 1)].ImageSource;
    public Microsoft.UI.Xaml.Visibility MediaVisibility { get; }
    public Microsoft.UI.Xaml.Visibility MediaPlaceholderVisibility { get; }
    public int CurrentImageIndex
    {
        get => _currentImageIndex;
        set
        {
            var clamped = MediaItems.Count == 0 ? 0 : Math.Clamp(value, 0, MediaItems.Count - 1);
            if (_currentImageIndex == clamped) return;
            _currentImageIndex = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentImageIndex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageCounterText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaSource)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageDownloadUrl)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviousImageVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NextImageVisibility)));
        }
    }
    public string ImageCounterText => MediaItems.Count > 1 ? $"{CurrentImageIndex + 1} / {MediaItems.Count}" : string.Empty;
    public Microsoft.UI.Xaml.Visibility ImageCounterVisibility => MediaItems.Count > 1
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility PreviousImageVisibility => CurrentImageIndex > 0
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility NextImageVisibility => CurrentImageIndex < MediaItems.Count - 1
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;
    public bool IsCollectionEligible { get; }
    public bool CanReply { get; }
    public bool CanInteractWithPost { get; }
    public double GridCardHeight => _gridCardHeight;
    public double CompactGridCardHeight => _compactGridCardHeight;

    public void SetGridCardHeight(double height)
    {
        if (Math.Abs(_gridCardHeight - height) < 1)
        {
            return;
        }

        _gridCardHeight = height;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GridCardHeight)));
    }

    public void SetCompactGridCardHeight(double height)
    {
        if (Math.Abs(_compactGridCardHeight - height) < 1)
        {
            return;
        }

        _compactGridCardHeight = height;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompactGridCardHeight)));
    }

    public bool IsFavourited
    {
        get => _isFavourited;
        private set => SetValue(ref _isFavourited, value, nameof(IsFavourited), nameof(LikeButtonText), nameof(CompactLikeButtonText));
    }

    public bool IsReblogged
    {
        get => _isReblogged;
        private set => SetValue(ref _isReblogged, value, nameof(IsReblogged), nameof(ShareButtonText), nameof(CompactShareButtonText));
    }

    public bool IsBookmarked
    {
        get => _isBookmarked;
        private set => SetValue(ref _isBookmarked, value, nameof(IsBookmarked), nameof(BookmarkButtonText));
    }

    public int FavouriteCount
    {
        get => _favouriteCount;
        private set => SetValue(ref _favouriteCount, value, nameof(FavouriteCount), nameof(LikeButtonText), nameof(CompactLikeButtonText));
    }

    public int ShareCount
    {
        get => _shareCount;
        private set => SetValue(ref _shareCount, value, nameof(ShareCount), nameof(CompactShareButtonText));
    }

    public int ReplyCount
    {
        get => _replyCount;
        private set => SetValue(ref _replyCount, value, nameof(ReplyCount), nameof(ReplyButtonText), nameof(CompactReplyButtonText));
    }

    public string LikeButtonText => $"{(IsFavourited ? "♥ Liked" : "♡ Like")} · {FavouriteCount}";
    public string ShareButtonText => $"{(IsReblogged ? "Shared" : "Share")} · {ShareCount}";
    public string BookmarkButtonText => IsBookmarked ? "Saved" : "Save";
    public string ReplyButtonText => CanReply ? $"Comment · {ReplyCount}" : "Comments off";
    public string CompactLikeButtonText => $"{(IsFavourited ? "♥" : "♡")} {FavouriteCount}";
    public string CompactShareButtonText => $"{(IsReblogged ? "✓" : "↻")} {ShareCount}";
    public string CompactReplyButtonText => $"▱ {ReplyCount}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyActionState(PixelfedPostActionState state)
    {
        IsFavourited = state.IsFavourited;
        IsReblogged = state.IsReblogged;
        IsBookmarked = state.IsBookmarked;
        FavouriteCount = state.FavouriteCount;
        ShareCount = state.ShareCount;
        ReplyCount = state.ReplyCount;
    }

    public void MarkReplyPosted() => ReplyCount++;

    private void SetValue<T>(ref T field, T value, params string[] propertyNames)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        foreach (var propertyName in propertyNames)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

public sealed class FeedPostMediaViewModel(
    FeedPostViewModel post,
    ImageSource imageSource,
    string downloadUrl)
{
    public FeedPostViewModel Post { get; } = post;
    public ImageSource ImageSource { get; } = imageSource;
    public string DownloadUrl { get; } = downloadUrl;
}
