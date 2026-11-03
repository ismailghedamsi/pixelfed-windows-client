using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace PixelfedClient.Views;

public static class PhotoViewer
{
    public static void Show(XamlRoot xamlRoot, ImageSource source, string title)
    {
        var popup = new Popup { XamlRoot = xamlRoot };
        var backdrop = new Grid
        {
            Width = xamlRoot.Size.Width,
            Height = xamlRoot.Size.Height,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = 0.88 },
        };
        backdrop.Tapped += (_, _) => popup.IsOpen = false;

        var image = new Image
        {
            Source = source,
            Stretch = Stretch.Uniform,
            MaxWidth = Math.Max(320, xamlRoot.Size.Width - 64),
            MaxHeight = Math.Max(240, xamlRoot.Size.Height - 64),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        image.Tapped += (_, args) => args.Handled = true;
        backdrop.Children.Add(image);

        var closeButton = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(closeButton, $"Close enlarged photo: {title}");
        closeButton.Click += (_, _) => popup.IsOpen = false;
        backdrop.Children.Add(closeButton);

        popup.Child = backdrop;
        popup.IsOpen = true;
    }
}
