using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PixelfedClient.Services;
using PixelfedClient.Views;
using System.Collections.ObjectModel;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PixelfedClient.Pages;

public sealed partial class CreatePostPage : Page
{
    private readonly PixelfedPostCreationService _postCreationService = new();
    private readonly PixelfedCollectionsService _collectionsService = new();
    private readonly ObservableCollection<PixelfedCollection> _collections = new();
    private readonly ObservableCollection<PixelfedCollection> _collectionSuggestions = new();
    private Uri? _instanceUri;
    private CredentialVault.SavedPixelfedCredentials? _credentials;
    private StorageFile? _selectedPhoto;
    private bool _isPublishing;

    public CreatePostPage()
    {
        InitializeComponent();
        CollectionSelector.ItemsSource = _collections;
        CollectionSearchBox.ItemsSource = _collectionSuggestions;
        Loaded += CreatePostPage_Loaded;
    }

    private async void CreatePostPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= CreatePostPage_Loaded;
        await RefreshAccountAsync();
    }

    private async Task RefreshAccountAsync()
    {
        var instance = ApplicationData.Current.LocalSettings.Values["pixelfed.instance"] as string;
        if (!Uri.TryCreate(instance, UriKind.Absolute, out _instanceUri))
        {
            StatusText.Text = "Choose your Pixelfed server in Settings before creating a post.";
            return;
        }

        _credentials = CredentialVault.GetCredentialsFor(_instanceUri.Host);
        if (_credentials is null)
        {
            StatusText.Text = "Sign in to your Pixelfed account to create posts.";
            return;
        }

        if (!CredentialVault.HasWriteScope(_instanceUri.Host))
        {
            StatusText.Text = "Your current session does not have permission to publish. Use the button below to reconnect with write access.";
            return;
        }

        await LoadCollectionsAsync();
        StatusText.Text = $"Publishing to {_instanceUri.Host}.";
    }

    private async Task<bool> EnsureWriteAccessAsync()
    {
        if (_instanceUri is not null
            && _credentials is not null
            && CredentialVault.HasWriteScope(_instanceUri.Host))
        {
            return true;
        }

        if (_instanceUri is null)
        {
            var instance = ApplicationData.Current.LocalSettings.Values["pixelfed.instance"] as string;
            if (!Uri.TryCreate(instance, UriKind.Absolute, out _instanceUri))
            {
                StatusText.Text = "Choose your Pixelfed server in Settings first.";
                return false;
            }
        }

        var dialog = new ConnectDialog { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
        _credentials = CredentialVault.GetCredentialsFor(_instanceUri.Host);
        if (_credentials is null || !CredentialVault.HasWriteScope(_instanceUri.Host))
        {
            StatusText.Text = "Publishing requires a signed-in session with write access.";
            return false;
        }

        await LoadCollectionsAsync();
        return true;
    }

    private async Task LoadCollectionsAsync()
    {
        if (_instanceUri is null || _credentials is null || !CredentialVault.HasWriteScope(_instanceUri.Host))
        {
            return;
        }

        try
        {
            var selectedId = (CollectionSelector.SelectedItem as PixelfedCollection)?.Id;
            var collections = await _collectionsService.GetOwnCollectionsAsync(_instanceUri, _credentials.AccessToken);
            _collections.Clear();
            foreach (var collection in collections)
            {
                _collections.Add(collection);
            }

            CollectionSelector.SelectedItem = _collections.FirstOrDefault(collection => collection.Id == selectedId);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            StatusText.Text = $"Could not load collections: {ex.Message}";
        }
    }

    private void CollectionSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _collectionSuggestions.Clear();
        var query = sender.Text.Trim();
        if (query.Length == 0)
        {
            return;
        }

        foreach (var collection in _collections.Where(collection =>
                     collection.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || collection.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(12))
        {
            _collectionSuggestions.Add(collection);
        }
    }

    private void CollectionSearch_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is PixelfedCollection collection)
        {
            CollectionSelector.SelectedItem = collection;
            sender.Text = collection.Title;
        }
    }

    private async void ChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".heic" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        var window = ((App)Application.Current).MainWindow;
        if (window is null)
        {
            return;
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        _selectedPhoto = await picker.PickSingleFileAsync();
        if (_selectedPhoto is null)
        {
            return;
        }

        PhotoNameText.Text = _selectedPhoto.Name;
        using var stream = await _selectedPhoto.OpenAsync(FileAccessMode.Read);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        PhotoPreviewImage.Source = bitmap;
        StatusText.Text = "Photo ready to publish.";
    }

    private async void CreateCollection_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureWriteAccessAsync() || _instanceUri is null || _credentials is null)
        {
            return;
        }

        try
        {
            var existingIds = _collections.Select(collection => collection.Id).ToHashSet(StringComparer.Ordinal);
            var dialog = new CollectionCreationDialog(_instanceUri) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await LoadCollectionsAsync();
            var created = _collections.FirstOrDefault(collection => !existingIds.Contains(collection.Id));
            if (created is not null)
            {
                CollectionSelector.SelectedItem = created;
                CollectionSearchBox.Text = created.Title;
                StatusText.Text = $"“{created.Title}” is selected. Your new post will be added to it when published.";
            }
            else
            {
                StatusText.Text = "Collections refreshed. Select a collection for the new post.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            StatusText.Text = $"Could not create or load collections: {ex.Message}";
        }
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (_isPublishing)
        {
            return;
        }

        if (_selectedPhoto is null)
        {
            StatusText.Text = "Choose a photo before publishing.";
            return;
        }

        if (!await EnsureWriteAccessAsync() || _instanceUri is null || _credentials is null)
        {
            return;
        }

        var visibility = (VisibilitySelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "public";
        _isPublishing = true;
        PublishButton.IsEnabled = false;
        PublishButton.Content = "Publishing…";
        ChoosePhotoButton.IsEnabled = false;
        CreateCollectionButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Uploading photo and publishing your post…";
            var post = await _postCreationService.CreatePhotoPostAsync(
                _instanceUri,
                _credentials.AccessToken,
                _selectedPhoto,
                CaptionTextBox.Text.Trim(),
                AltTextBox.Text.Trim(),
                visibility);

            var collection = CollectionSelector.SelectedItem as PixelfedCollection;
            if (collection is null)
            {
                StatusText.Text = "Post published successfully.";
            }
            else
            {
                try
                {
                    await _collectionsService.AddPostAsync(_instanceUri, _credentials.AccessToken, collection.Id, post.Id);
                    StatusText.Text = $"Post published and added to “{collection.Title}”.";
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
                {
                    StatusText.Text = $"Post published, but couldn’t add it to “{collection.Title}”: {ex.Message}";
                }
            }

            if (Uri.TryCreate(post.Url, UriKind.Absolute, out var postUri) && postUri.Scheme == Uri.UriSchemeHttps)
            {
                StatusText.Text += $"  {postUri}";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or IOException)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _isPublishing = false;
            PublishButton.IsEnabled = true;
            PublishButton.Content = "Publish photo";
            ChoosePhotoButton.IsEnabled = true;
            CreateCollectionButton.IsEnabled = true;
        }
    }
}
