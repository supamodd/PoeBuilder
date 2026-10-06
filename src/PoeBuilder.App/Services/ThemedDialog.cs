using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PoeBuilder.App.Services;

public static class ThemedDialog
{
    public static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None) =>
        Show(Application.Current?.MainWindow, message, caption, buttons, image, defaultResult);

    public static MessageBoxResult Show(Window? owner, string message, string caption, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dialog = new Window
        {
            Title = caption,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 760,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
            Background = GetBrush("Bg", "#0D1116"),
            Foreground = GetBrush("Text", "#EAE8E2"),
            FontFamily = Application.Current?.TryFindResource("AppFont") as FontFamily ?? new FontFamily("Jost Medieval"),
            WindowStyle = WindowStyle.None
        };
        var iconColor = image is MessageBoxImage.Error or MessageBoxImage.Warning ? GetBrush("Danger", "#DE8C83") : GetBrush("Gold", "#D6B47A");
        var content = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        content.Children.Add(new Border { Height = 2, Background = iconColor, Margin = new Thickness(0, 0, 0, 14) });
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 600,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 22, LineHeight = 34 }
        });
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        MessageBoxResult selected = defaultResult;
        foreach (var (result, label) in GetButtons(buttons))
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 116,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = result == (defaultResult == MessageBoxResult.None ? GetDefault(buttons) : defaultResult),
                IsCancel = result == MessageBoxResult.Cancel,
                Style = result == MessageBoxResult.Yes || result == MessageBoxResult.OK
                    ? Application.Current?.TryFindResource("PrimaryButton") as Style
                    : Application.Current?.TryFindResource("BaseButton") as Style
            };
            button.Click += (_, _) => { selected = result; dialog.DialogResult = true; };
            buttonPanel.Children.Add(button);
        }
        content.Children.Add(buttonPanel);
        dialog.Content = content;
        ThemedWindowChrome.Apply(dialog);
        dialog.ShowDialog();
        return selected == MessageBoxResult.None ? GetDefault(buttons) : selected;
    }

    private static SolidColorBrush GetBrush(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as SolidColorBrush ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));

    private static MessageBoxResult GetDefault(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.YesNo => MessageBoxResult.No,
        MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
        MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
        _ => MessageBoxResult.OK
    };

    private static IReadOnlyList<(MessageBoxResult Result, string Label)> GetButtons(MessageBoxButton buttons)
    {
        var russian = (Application.Current?.MainWindow?.DataContext as PoeBuilder.App.ViewModels.MainViewModel)?.L.Language == "ru"
            || Application.Current?.MainWindow?.DataContext is null && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
        var yes = russian ? "Да" : "Yes";
        var no = russian ? "Нет" : "No";
        var ok = russian ? "ОК" : "OK";
        var cancel = russian ? "Отмена" : "Cancel";
        return buttons switch
        {
            MessageBoxButton.YesNo => [(MessageBoxResult.Yes, yes), (MessageBoxResult.No, no)],
            MessageBoxButton.YesNoCancel => [(MessageBoxResult.Yes, yes), (MessageBoxResult.No, no), (MessageBoxResult.Cancel, cancel)],
            MessageBoxButton.OKCancel => [(MessageBoxResult.OK, ok), (MessageBoxResult.Cancel, cancel)],
            _ => [(MessageBoxResult.OK, ok)]
        };
    }
}