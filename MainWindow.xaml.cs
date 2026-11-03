using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PixelfedClient.Pages;
using PixelfedClient.Services;
using PixelfedClient.Views;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PixelfedClient;

public sealed partial class MainWindow : Window
{
    private string? _pendingNotificationPostId;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        RootGrid.RequestedTheme = AppThemeService.SelectedTheme;
        AppThemeService.ThemeChanged += AppThemeService_ThemeChanged;
        NavView.Loaded += MainWindow_Loaded;
    }

    private void AppThemeService_ThemeChanged(object? sender, EventArgs e)
    {
        RootGrid.RequestedTheme = AppThemeService.SelectedTheme;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        NavView.Loaded -= MainWindow_Loaded;
        var instanceUrl = (string?)ApplicationData.Current.LocalSettings.Values["pixelfed.instance"];
        var domain = Uri.TryCreate(instanceUrl, UriKind.Absolute, out var instanceUri)
            ? instanceUri.Host
            : string.Empty;
        if (CredentialVault.HasCredentialsFor(domain))
        {
            return;
        }

        var dialog = new ConnectDialog
        {
            XamlRoot = NavView.XamlRoot,
        };
        await dialog.ShowAsync();

        if (NavFrame.Content is HomePage homePage)
        {
            await homePage.RefreshAsync();
        }
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

    public void OpenNotificationPost(string statusId)
    {
        if (NavView.SelectedItem is NavigationViewItem selected
            && string.Equals(selected.Tag as string, "feed", StringComparison.Ordinal))
        {
            NavFrame.Navigate(typeof(HomePage), statusId);
            return;
        }

        _pendingNotificationPostId = statusId;
        var feedItem = NavView.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, "feed", StringComparison.Ordinal));
        if (feedItem is not null)
        {
            NavView.SelectedItem = feedItem;
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "feed":
                    var statusId = _pendingNotificationPostId;
                    _pendingNotificationPostId = null;
                    NavFrame.Navigate(typeof(HomePage), statusId);
                    break;
                case "notifications":
                    NavFrame.Navigate(typeof(NotificationsPage));
                    break;
                case "bookmarks":
                    NavFrame.Navigate(typeof(BookmarksPage));
                    break;
                case "create-post":
                    NavFrame.Navigate(typeof(CreatePostPage));
                    break;
                case "collections":
                    NavFrame.Navigate(typeof(CollectionsPage));
                    break;
                case "profile":
                    NavFrame.Navigate(typeof(ProfilePage));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
            }
        }
    }
}
