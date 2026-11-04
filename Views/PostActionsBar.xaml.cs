using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PixelfedClient.Pages;
using PixelfedClient.Services;
using Windows.Storage;
using Windows.System;

namespace PixelfedClient.Views;

public sealed partial class PostActionsBar : UserControl
{
    private readonly PixelfedPostActionsService _actionsService = new();
    private bool _isBusy;

    public static readonly DependencyProperty PostProperty = DependencyProperty.Register(
        nameof(Post),
        typeof(FeedPostViewModel),
        typeof(PostActionsBar),
        new PropertyMetadata(null, OnPostChanged));

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact),
        typeof(bool),
        typeof(PostActionsBar),
        new PropertyMetadata(false, OnCompactChanged));

    public FeedPostViewModel? Post
    {
        get => (FeedPostViewModel?)GetValue(PostProperty);
        set => SetValue(PostProperty, value);
    }

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public event RoutedEventHandler? CommentRequested;

    public PostActionsBar()
    {
        InitializeComponent();
        Loaded += PostActionsBar_Loaded;
        Unloaded += PostActionsBar_Unloaded;
    }

    private void PostActionsBar_Loaded(object sender, RoutedEventArgs e)
    {
        ClientPreferences.FeedPreferencesChanged += ClientPreferences_FeedPreferencesChanged;
        UpdateActionVisibility();
    }

    private void PostActionsBar_Unloaded(object sender, RoutedEventArgs e)
    {
        ClientPreferences.FeedPreferencesChanged -= ClientPreferences_FeedPreferencesChanged;
    }

    private void ClientPreferences_FeedPreferencesChanged(object? sender, EventArgs e) => UpdateActionVisibility();

    private void UpdateActionVisibility()
    {
        var canInteract = Post?.CanInteractWithPost ?? true;
        ActionButtonsPanel.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        CompactActionsPanel.Visibility = Compact ? Visibility.Visible : Visibility.Collapsed;
        LikeButton.Visibility = canInteract ? Visibility.Visible : Visibility.Collapsed;
        ShareButton.Visibility = canInteract && ClientPreferences.AllowReblog ? Visibility.Visible : Visibility.Collapsed;
        ReplyButton.Visibility = canInteract ? Visibility.Visible : Visibility.Collapsed;
        BookmarkButton.Visibility = canInteract ? Visibility.Visible : Visibility.Collapsed;
        CompactLikeButton.Visibility = canInteract ? Visibility.Visible : Visibility.Collapsed;
        CompactShareButton.Visibility = canInteract && ClientPreferences.AllowReblog ? Visibility.Visible : Visibility.Collapsed;
        CompactReplyButton.Visibility = canInteract ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OnPostChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (PostActionsBar)sender;
        control.UpdateActionVisibility();
    }

    private static void OnCompactChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((PostActionsBar)sender).UpdateActionVisibility();
    }

    private async void Like_Click(object sender, RoutedEventArgs e)
    {
        if (Post is null)
        {
            return;
        }

        var desiredValue = !Post.IsFavourited;
        await RunActionAsync(async (instance, credentials) =>
        {
            var state = await _actionsService.SetFavouriteAsync(
                instance,
                credentials.AccessToken,
                Post,
                desiredValue);
            Post.ApplyActionState(state);
        });
    }

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (Post is null)
        {
            return;
        }

        var desiredValue = !Post.IsReblogged;
        await RunActionAsync(async (instance, credentials) =>
        {
            var state = await _actionsService.SetReblogAsync(
                instance,
                credentials.AccessToken,
                Post,
                desiredValue);
            Post.ApplyActionState(state);
        });
    }

    private async void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (Post is null)
        {
            return;
        }

        var desiredValue = !Post.IsBookmarked;
        await RunActionAsync(async (instance, credentials) =>
        {
            var state = await _actionsService.SetBookmarkAsync(
                instance,
                credentials.AccessToken,
                Post,
                desiredValue);
            Post.ApplyActionState(state);
        });
    }

    private async void OpenWeb_Click(object sender, RoutedEventArgs e)
    {
        if (Post is null || !Uri.TryCreate(Post.PostUrl, UriKind.Absolute, out var postUri)
            || postUri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            if (!await Launcher.LaunchUriAsync(postUri))
            {
                await ShowMessageAsync("Couldn’t open post", "Windows could not open this Pixelfed post in your browser.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            await ShowMessageAsync("Couldn’t open post", ex.Message);
        }
    }

    private async void Reply_Click(object sender, RoutedEventArgs e)
    {
        if (Post is null || !Post.CanReply)
        {
            return;
        }

        if (CommentRequested is not null)
        {
            CommentRequested(this, new RoutedEventArgs());
            return;
        }

        var editor = new TextBox
        {
            AcceptsReturn = true,
            MaxLength = 500,
            MinHeight = 120,
            PlaceholderText = "Write a comment…",
            TextWrapping = TextWrapping.Wrap,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Comment on {Post.Username}’s post",
            Content = editor,
            PrimaryButtonText = "Post comment",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var comment = editor.Text.Trim();
        if (comment.Length == 0)
        {
            await ShowMessageAsync("Comment is empty", "Write a comment before posting it.");
            return;
        }

        var replied = false;
        await RunActionAsync(async (instance, credentials) =>
        {
            await _actionsService.ReplyAsync(instance, credentials.AccessToken, Post, comment);
            replied = true;
        });

        if (replied)
        {
            Post.MarkReplyPosted();
        }
    }

    private async Task RunActionAsync(Func<Uri, CredentialVault.SavedPixelfedCredentials, Task> action)
    {
        if (_isBusy)
        {
            return;
        }

        var context = await GetWriteContextAsync();
        if (context is null)
        {
            return;
        }

        _isBusy = true;
        SetActionButtonsEnabled(false);
        try
        {
            await action(context.Value.Instance, context.Value.Credentials);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            await ShowMessageAsync("Couldn’t update this post", ex.Message);
        }
        finally
        {
            _isBusy = false;
            SetActionButtonsEnabled(true);
        }
    }

    private void SetActionButtonsEnabled(bool enabled)
    {
        LikeButton.IsEnabled = enabled;
        CompactLikeButton.IsEnabled = enabled;
        ShareButton.IsEnabled = enabled;
        CompactShareButton.IsEnabled = enabled;
        ReplyButton.IsEnabled = enabled && (Post?.CanReply ?? false);
        CompactReplyButton.IsEnabled = enabled && (Post?.CanReply ?? false);
        BookmarkButton.IsEnabled = enabled;
    }

    private async Task<(Uri Instance, CredentialVault.SavedPixelfedCredentials Credentials)?> GetWriteContextAsync()
    {
        var instanceUrl = (string?)ApplicationData.Current.LocalSettings.Values["pixelfed.instance"];
        if (!Uri.TryCreate(instanceUrl, UriKind.Absolute, out var instance))
        {
            await ShowMessageAsync("Not connected", "Choose a Pixelfed server and sign in from Settings first.");
            return null;
        }

        var credentials = CredentialVault.GetCredentialsFor(instance.Host);
        if (credentials is null || !CredentialVault.HasWriteScope(instance.Host))
        {
            var dialog = new ConnectDialog
            {
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
            credentials = CredentialVault.GetCredentialsFor(instance.Host);
        }

        if (credentials is null)
        {
            await ShowMessageAsync("Sign in required", "Sign in to your Pixelfed account to use post actions.");
            return null;
        }

        if (!CredentialVault.HasWriteScope(instance.Host))
        {
            await ShowMessageAsync(
                "Permission needed",
                "Post actions require Pixelfed’s write permission. Reconnect from Settings and approve the access shown on Pixelfed’s consent page.");
            return null;
        }

        return (instance, credentials);
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
}
