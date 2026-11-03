using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using PixelfedClient.Services;
using System.Security.Cryptography;
using Windows.Storage;

namespace PixelfedClient.Views;

public sealed partial class ConnectDialog : ContentDialog
{
    private readonly PixelfedInstanceService _instanceService = new();
    private readonly PixelfedOAuthService _oauthService = new();
    private Uri? _instanceBaseUri;
    private string? _domain;
    private string? _expectedState;
    private PixelfedOAuthClient? _oauthClient;
    private bool _completingSignIn;

    public ConnectDialog()
    {
        InitializeComponent();
        InstanceAddressTextBox.Text =
            (string?)ApplicationData.Current.LocalSettings.Values["pixelfed.instance"] ?? string.Empty;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        ConnectButton.Content = "Checking server…";
        StatusText.Text = string.Empty;

        try
        {
            var instance = await _instanceService.GetInstanceAsync(InstanceAddressTextBox.Text);
            if (!instance.OAuthEnabled)
            {
                throw new InvalidOperationException($"{instance.Domain} has OAuth disabled. Choose another Pixelfed server or contact its administrator.");
            }

            _instanceBaseUri = instance.BaseUri;
            _domain = instance.BaseUri.Host;
            ApplicationData.Current.LocalSettings.Values["pixelfed.instance"] = instance.BaseUrl;
            var savedCredentials = CredentialVault.GetCredentialsFor(instance.BaseUri.Host);
            _oauthClient = savedCredentials is { ClientId.Length: > 0, ClientSecret.Length: > 0 }
                ? new PixelfedOAuthClient(savedCredentials.ClientId, savedCredentials.ClientSecret)
                : await _oauthService.RegisterClientAsync(instance.BaseUri);
            _expectedState = PixelfedOAuthService.CreateState();

            InstancePanel.Visibility = Visibility.Collapsed;
            LoginPanel.Visibility = Visibility.Visible;
            LoginStatusText.Text = $"Sign in to {instance.Title} ({instance.Domain}). Approve read-only access to continue.";

            await LoginWebView.EnsureCoreWebView2Async();
            LoginWebView.NavigationStarting += LoginWebView_NavigationStarting;
            LoginWebView.NavigationCompleted += LoginWebView_NavigationCompleted;
            LoginWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            LoginWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            LoginWebView.Source = _oauthService.BuildAuthorizationUri(instance.BaseUri, _oauthClient, _expectedState);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException or TaskCanceledException)
        {
            LoginPanel.Visibility = Visibility.Collapsed;
            InstancePanel.Visibility = Visibility.Visible;
            StatusText.Text = ex.Message;
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "Check server and sign in";
        }
        catch (Exception ex)
        {
            LoginPanel.Visibility = Visibility.Collapsed;
            InstancePanel.Visibility = Visibility.Visible;
            StatusText.Text = $"Could not start sign-in: {ex.Message}";
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "Check server and sign in";
        }
    }

    private async void LoginWebView_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri))
        {
            args.Cancel = true;
            return;
        }

        if (uri.Scheme.Equals("pixelfedclient", StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
            await CompleteSignInAsync(uri);
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttps
            || _instanceBaseUri is null
            || !uri.Host.Equals(_instanceBaseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
            LoginStatusText.Text = "Sign-in was redirected to an unexpected site, so navigation was blocked.";
        }
    }

    private void LoginWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess)
        {
            LoginStatusText.Text = "The sign-in page could not be loaded. Check your connection and try again.";
        }
    }

    private async Task CompleteSignInAsync(Uri callbackUri)
    {
        if (_completingSignIn)
        {
            return;
        }

        _completingSignIn = true;
        try
        {
            if (_oauthClient is null || _instanceBaseUri is null || _domain is null || _expectedState is null)
            {
                throw new InvalidOperationException("The sign-in session expired. Close this dialog and try again.");
            }

            var query = ParseQuery(callbackUri.Query);
            if (!query.TryGetValue("state", out var returnedState)
                || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(returnedState),
                    System.Text.Encoding.UTF8.GetBytes(_expectedState)))
            {
                throw new InvalidOperationException("The sign-in response could not be verified. Please try again.");
            }

            if (query.TryGetValue("error", out var oauthError))
            {
                throw new InvalidOperationException($"Pixelfed sign-in was cancelled or denied ({oauthError}).");
            }

            if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                throw new InvalidOperationException("Pixelfed returned no sign-in code. Please try again.");
            }

            LoginStatusText.Text = "Finishing sign-in…";
            var account = await _oauthService.ExchangeCodeAsync(_instanceBaseUri, _oauthClient, code);
            CredentialVault.Save(_domain, _oauthClient, account);

            LoginPanel.Visibility = Visibility.Collapsed;
            SuccessPanel.Visibility = Visibility.Visible;
            SuccessText.Text = $"Signed in as @{account.Username} on {_domain}. Your credentials are stored in Windows Credential Locker.";
        }
        catch (Exception ex)
        {
            _completingSignIn = false;
            LoginStatusText.Text = ex.Message;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = segment.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e) => Hide();
}
