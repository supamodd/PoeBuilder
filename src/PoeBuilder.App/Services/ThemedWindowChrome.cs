using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PoeBuilder.App.Services;

public static class ThemedWindowChrome
{
    public static void Apply(Window window)
    {
        if (window.Content is not UIElement content) return;

        var body = new ContentPresenter { Content = content };
        var header = new Grid { Height = 52, Background = Brush("Panel", "#131920") };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Margin = new Thickness(18, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });
        header.Children.Add(title);

        var close = new Button
        {
            Content = "×",
            Width = 56,
            Height = 46,
            MinHeight = 0,
            Padding = new Thickness(0),
            ToolTip = "Close"
        };
        close.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
        close.Click += (_, _) => window.Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (!e.Handled && e.ButtonState == MouseButtonState.Pressed)
                window.DragMove();
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(body, 1);
        layout.Children.Add(header);
        layout.Children.Add(body);

        var root = new Border
        {
            Background = window.Background,
            BorderBrush = Brush("Line", "#2A343F"),
            BorderThickness = new Thickness(1),
            Child = layout
        };
        window.Content = root;
        window.WindowStyle = WindowStyle.None;
        ConstrainToWorkArea(window);
        FadeIn(root);
    }

    /// <summary>Every chrome-wrapped window (editors, pickers, message boxes) gets a soft entrance instead of a
    /// hard pop: its content rises from a barely-there 0.35 to full opacity over a fraction of a second. Starting
    /// from 0.35 rather than 0 is deliberate — if this time-based animation runs before the window's first frame
    /// (it is created, wired, then shown), the clock can stall and a 0 start leaves the window dark.</summary>
    private static void FadeIn(FrameworkElement root)
    {
        root.Opacity = 0.35;
        var fade = new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) => root.BeginAnimation(UIElement.OpacityProperty, null);
        root.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public static void ConstrainToWorkArea(Window window)
    {
        var workArea = SystemParameters.WorkArea;
        window.MaxWidth = Math.Max(480, workArea.Width - 16);
        window.MaxHeight = Math.Max(360, workArea.Height - 16);
        if (window.SizeToContent != SizeToContent.Manual) return;
        // The editors are sized in XAML for a comfortable desktop, but a 150% scaled 1366x768 laptop hands
        // WPF a much smaller work area than that number, and a window wider than the screen puts its right-hand
        // column (the fields and the "add" buttons) off the desktop edge, where it cannot be reached at all.
        // Clamping to the work area here keeps every panel on screen; MinWidth/MinHeight keep it above the
        // point where the layout would start to overlap.
        if (window.Width > window.MaxWidth) window.Width = window.MaxWidth;
        if (window.Height > window.MaxHeight) window.Height = window.MaxHeight;
        if (window.MinWidth > window.Width) window.MinWidth = window.Width;
        if (window.MinHeight > window.Height) window.MinHeight = window.Height;
    }

    private static Brush Brush(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
}