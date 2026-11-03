using Microsoft.UI.Xaml;
using Windows.Storage;

namespace PixelfedClient.Services;

public static class AppThemeService
{
    private const string ThemeKey = "appearance.theme";

    public static event EventHandler? ThemeChanged;

    public static ElementTheme SelectedTheme =>
        ApplicationData.Current.LocalSettings.Values[ThemeKey] is string value
        && Enum.TryParse<ElementTheme>(value, out var theme)
            ? theme
            : ElementTheme.Default;

    public static void SetTheme(ElementTheme theme)
    {
        if (SelectedTheme == theme) return;
        ApplicationData.Current.LocalSettings.Values[ThemeKey] = theme.ToString();
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }
}
