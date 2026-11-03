using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace PixelfedClient.Views;

public sealed partial class CollectionCreationDialog : ContentDialog
{
    private readonly Uri _instanceBaseUri;

    public CollectionCreationDialog(Uri instanceBaseUri)
    {
        InitializeComponent();
        _instanceBaseUri = instanceBaseUri;
        Loaded += CollectionCreationDialog_Loaded;
    }

    private async void CollectionCreationDialog_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= CollectionCreationDialog_Loaded;
        try
        {
            await CollectionWebView.EnsureCoreWebView2Async();
            CollectionWebView.NavigationStarting += CollectionWebView_NavigationStarting;
            CollectionWebView.NavigationCompleted += CollectionWebView_NavigationCompleted;
            CollectionWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            CollectionWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            CollectionWebView.Source = new Uri(_instanceBaseUri, "/i/collections/create");
        }
        catch (Exception ex)
        {
            CollectionWebViewStatusText.Text = $"Could not open the Pixelfed collection page: {ex.Message}";
        }
    }

    private void CollectionWebView_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(_instanceBaseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
            CollectionWebViewStatusText.Text = "Navigation outside your Pixelfed server was blocked.";
        }
    }

    private void CollectionWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        CollectionWebViewStatusText.Text = args.IsSuccess
            ? $"Pixelfed · {_instanceBaseUri.Host}"
            : "The Pixelfed page did not load. Check your connection and try again.";
    }
}
