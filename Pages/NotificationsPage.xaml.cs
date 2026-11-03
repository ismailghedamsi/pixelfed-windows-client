using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PixelfedClient.Services;
using System.Collections.ObjectModel;
using Windows.Storage;
using Windows.System;

namespace PixelfedClient.Pages;

public sealed partial class NotificationsPage : Page
{
    private readonly PixelfedNotificationsService _service = new();
    private readonly ObservableCollection<NotificationViewModel> _notifications = new();
    private readonly List<NotificationViewModel> _allNotifications = new();
    private Uri? _instanceUri;
    private string? _accessToken;
    private string? _cursor;
    private bool _hasMore;
    private bool _isLoading;

    public NotificationsPage()
    {
        InitializeComponent();
        NotificationsList.ItemsSource = _notifications;
        CategoryFilter.SelectedIndex = 0;
        SearchBox.TextChanged += SearchBox_TextChanged;
        CategoryFilter.SelectionChanged += CategoryFilter_SelectionChanged;
        Loaded += NotificationsPage_Loaded;
    }

    private async void NotificationsPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= NotificationsPage_Loaded;
        await LoadAsync(reset: true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(reset: true);
    private async void LoadMore_Click(object sender, RoutedEventArgs e) => await LoadAsync(reset: false);
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilters();
    private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilters();

    private async void MarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        if (_instanceUri is null || string.IsNullOrWhiteSpace(_accessToken) || _allNotifications.Count == 0)
        {
            StatusText.Text = "Load notifications before marking them as read.";
            return;
        }

        MarkAllReadButton.IsEnabled = false;
        try
        {
            await _service.MarkAllAsReadAsync(_instanceUri, _accessToken, _allNotifications[0].Id);
            StatusText.Text = "All notifications are marked as read on your Pixelfed account.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            MarkAllReadButton.IsEnabled = true;
        }
    }

    private async Task LoadAsync(bool reset)
    {
        if (_isLoading) return;
        var instance = ApplicationData.Current.LocalSettings.Values["pixelfed.instance"] as string;
        if (!Uri.TryCreate(instance, UriKind.Absolute, out _instanceUri))
        {
            _notifications.Clear();
            _allNotifications.Clear();
            StatusText.Text = "Choose a Pixelfed server and sign in from Settings first.";
            return;
        }

        var credentials = CredentialVault.GetCredentialsFor(_instanceUri.Host);
        if (credentials is null)
        {
            _notifications.Clear();
            _allNotifications.Clear();
            StatusText.Text = "Sign in to your Pixelfed account to view notifications.";
            return;
        }

        _accessToken = credentials.AccessToken;
        if (reset)
        {
            _notifications.Clear();
            _allNotifications.Clear();
            _cursor = null;
            _hasMore = true;
            StatusText.Text = "Loading notifications…";
        }
        if (!_hasMore || _instanceUri is null || string.IsNullOrWhiteSpace(_accessToken)) return;

        _isLoading = true;
        LoadMoreButton.IsEnabled = false;
        try
        {
            var page = await _service.GetPageAsync(_instanceUri, _accessToken, _cursor);
            foreach (var notification in page.Notifications)
            {
                if (_allNotifications.All(existing => existing.Id != notification.Id))
                {
                    _allNotifications.Add(new NotificationViewModel(notification));
                }
            }

            _cursor = page.NextMaxId;
            _hasMore = page.HasMore && !string.IsNullOrWhiteSpace(_cursor);
            ApplyFilters();
            LoadMoreButton.Visibility = _hasMore ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            StatusText.Text = ex.Message;
            LoadMoreButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _isLoading = false;
            LoadMoreButton.IsEnabled = true;
        }
    }

    private void ApplyFilters()
    {
        if (_notifications is null || SearchBox is null || CategoryFilter is null) return;
        var category = CategoryFilter.SelectedIndex switch
        {
            1 => "Comments",
            2 => "Likes",
            3 => "Follows",
            _ => null,
        };
        var query = SearchBox.Text?.Trim();
        var matches = _allNotifications.Where(item =>
            (category is null || item.Category == category)
            && (string.IsNullOrWhiteSpace(query)
                || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Username.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.ActivityLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Caption.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();

        _notifications.Clear();
        foreach (var item in matches) _notifications.Add(item);

        if (_allNotifications.Count == 0 && !_isLoading && !_hasMore)
        {
            StatusText.Text = "You’re all caught up. No notifications were returned.";
        }
        else if (matches.Length == 0)
        {
            StatusText.Text = _allNotifications.Count == 0
                ? "Loading notifications…"
                : $"No matches in the {_allNotifications.Count} notifications loaded so far. Load more to search older activity.";
        }
        else
        {
            StatusText.Text = matches.Length == _allNotifications.Count
                ? $"Showing {matches.Length} loaded notification{(matches.Length == 1 ? string.Empty : "s")}."
                : $"Showing {matches.Length} of {_allNotifications.Count} loaded notifications. Load more to search older activity.";
        }
    }

    private async void NotificationsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not NotificationViewModel item)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.StatusId)
            && ((App)Application.Current).MainWindow is MainWindow mainWindow)
        {
            mainWindow.OpenNotificationPost(item.StatusId);
            return;
        }

        if (!Uri.TryCreate(item.TargetUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http")) return;
        await Launcher.LaunchUriAsync(uri);
    }
}

public sealed class NotificationViewModel
{
    public NotificationViewModel(PixelfedNotification notification)
    {
        Id = notification.Id;
        DisplayName = notification.DisplayName;
        Username = notification.Username;
        ActivityLabel = notification.Description;
        Category = notification.Category;
        CreatedAtLabel = notification.CreatedAtLabel;
        Caption = notification.Caption;
        ImageUrl = notification.ImageUrl;
        if (Uri.TryCreate(ImageUrl, UriKind.Absolute, out var imageUri)
            && imageUri.Scheme == Uri.UriSchemeHttps)
        {
            try { ImageSource = new BitmapImage(imageUri); }
            catch (ArgumentException) { ImageSource = null; }
        }
        TargetUrl = notification.TargetUrl;
        StatusId = notification.StatusId;
        CaptionVisibility = string.IsNullOrWhiteSpace(Caption) ? Visibility.Collapsed : Visibility.Visible;
        ImageVisibility = ImageSource is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Username { get; }
    public string ActivityLabel { get; }
    public string Category { get; }
    public string CreatedAtLabel { get; }
    public string Caption { get; }
    public string? ImageUrl { get; }
    public BitmapImage? ImageSource { get; }
    public string? TargetUrl { get; }
    public string? StatusId { get; }
    public Visibility CaptionVisibility { get; }
    public Visibility ImageVisibility { get; }
}
