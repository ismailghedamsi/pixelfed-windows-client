// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Controls;
using PixelfedClient.Services;
using PixelfedClient.Views;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PixelfedClient.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly PixelfedInstanceService _instanceService = new();

    public SettingsPage()
    {
        InitializeComponent();
        var settings = ApplicationData.Current.LocalSettings.Values;
        DefaultFeedSelector.SelectedIndex = ReadOption(settings["feed.defaultKind"], 1);
        DefaultAccountTypeSelector.SelectedIndex = ReadOption(settings["feed.defaultAccountType"], 1);
        AllowReblogCheckBox.IsChecked = ClientPreferences.AllowReblog;
        LimitFrequentPostersCheckBox.IsChecked = ClientPreferences.LimitFrequentPostersToOnePostPer24Hours;
        ThemeSelector.SelectedIndex = AppThemeService.SelectedTheme switch
        {
            Microsoft.UI.Xaml.ElementTheme.Light => 1,
            Microsoft.UI.Xaml.ElementTheme.Dark => 2,
            _ => 0,
        };
        ThemeSelector.SelectionChanged += ThemeSelector_SelectionChanged;
        InstanceAddressTextBox.Text =
            (string?)ApplicationData.Current.LocalSettings.Values["pixelfed.instance"] ?? string.Empty;
    }

    private static int ReadOption(object? value, int fallback) =>
        value is int selected && selected is >= 0 and <= 2 ? selected : fallback;

    private void DefaultFeed_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (DefaultFeedSelector.SelectedIndex < 0)
        {
            return;
        }

        ApplicationData.Current.LocalSettings.Values["feed.defaultKind"] = DefaultFeedSelector.SelectedIndex;
        FeedDefaultsStatusText.Text = "Feed defaults saved.";
    }

    private void DefaultAccountType_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (DefaultAccountTypeSelector.SelectedIndex < 0)
        {
            return;
        }

        ApplicationData.Current.LocalSettings.Values["feed.defaultAccountType"] = DefaultAccountTypeSelector.SelectedIndex;
        FeedDefaultsStatusText.Text = "Feed defaults saved.";
    }

    private void AllowReblog_Changed(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ClientPreferences.SetAllowReblog(AllowReblogCheckBox.IsChecked == true);
        FeedDefaultsStatusText.Text = "Feed defaults saved.";
    }

    private void LimitFrequentPosters_Changed(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ClientPreferences.SetLimitFrequentPostersToOnePostPer24Hours(
            LimitFrequentPostersCheckBox.IsChecked == true);
        FeedDefaultsStatusText.Text = "Feed defaults saved.";
    }

    private void ThemeSelector_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (ThemeSelector.SelectedIndex < 0) return;
        AppThemeService.SetTheme(ThemeSelector.SelectedIndex switch
        {
            1 => Microsoft.UI.Xaml.ElementTheme.Light,
            2 => Microsoft.UI.Xaml.ElementTheme.Dark,
            _ => Microsoft.UI.Xaml.ElementTheme.Default,
        });
        FeedDefaultsStatusText.Text = "Appearance saved.";
    }

    private async void CheckInstance_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        CheckInstanceButton.IsEnabled = false;
        CheckInstanceButton.Content = "Checking…";
        InstanceStatusText.Text = string.Empty;

        try
        {
            var instance = await _instanceService.GetInstanceAsync(InstanceAddressTextBox.Text);
            ApplicationData.Current.LocalSettings.Values["pixelfed.instance"] = instance.BaseUrl;
            InstanceAddressTextBox.Text = instance.Domain;
            InstanceStatusText.Text = instance.OAuthEnabled
                ? $"Connected to {instance.Title} ({instance.Domain}), version {instance.Version}. OAuth is available."
                : $"Found {instance.Title} ({instance.Domain}), but this server has OAuth disabled.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException or TaskCanceledException)
        {
            InstanceStatusText.Text = ex.Message;
        }
        finally
        {
            CheckInstanceButton.Content = "Check instance";
            CheckInstanceButton.IsEnabled = true;
        }
    }

    private async void SignIn_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var dialog = new ConnectDialog
        {
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
