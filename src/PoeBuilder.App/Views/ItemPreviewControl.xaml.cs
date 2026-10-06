namespace PoeBuilder.App.Views;

using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

/// <summary>
/// Renders a single dropped item exactly as it appears on the game floor: item frame, icon,
/// type line, rarity line, modifier list. All style properties are bound so the control reacts
/// to the currently selected filter rule in real time.
/// </summary>
public partial class ItemPreviewControl : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(PoeBuilder.Core.Filters.LootItem), typeof(ItemPreviewControl),
            new PropertyMetadata(null, OnItemChanged));

    public static readonly DependencyProperty ApplyStyleProperty =
        DependencyProperty.Register(nameof(ApplyStyle), typeof(bool), typeof(ItemPreviewControl),
            new PropertyMetadata(true));

    public static readonly DependencyProperty FontSizeProperty =
        DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(ItemPreviewControl),
            new PropertyMetadata(18.0, OnFontSizeChanged));

    public static readonly DependencyProperty FontColorProperty =
        DependencyProperty.Register(nameof(FontColor), typeof(Color), typeof(ItemPreviewControl),
            new PropertyMetadata(Colors.White));

    public static readonly DependencyProperty BackgroundColorProperty =
        DependencyProperty.Register(nameof(BackgroundColor), typeof(Color), typeof(ItemPreviewControl),
            new PropertyMetadata(Colors.Transparent));

    public static readonly DependencyProperty BorderColorProperty =
        DependencyProperty.Register(nameof(BorderColor), typeof(Color), typeof(ItemPreviewControl),
            new PropertyMetadata(Colors.Transparent));

    public static readonly DependencyProperty BorderThicknessProperty =
        DependencyProperty.Register(nameof(BorderThickness), typeof(double), typeof(ItemPreviewControl),
            new PropertyMetadata(1.0));

    public static readonly DependencyProperty FlareProperty =
        DependencyProperty.Register(nameof(Flare), typeof(bool), typeof(ItemPreviewControl),
            new PropertyMetadata(false));

    public static readonly DependencyProperty PlayEffectSoundProperty =
        DependencyProperty.Register(nameof(PlayEffectSound), typeof(bool), typeof(ItemPreviewControl),
            new PropertyMetadata(false));

    private readonly Border _itemBorder;
    private readonly Rectangle _itemIcon;
    private readonly TextBlock _itemType;
    private readonly TextBlock _itemRarity;
    private readonly StackPanel _itemModifiers;

    public PoeBuilder.Core.Filters.LootItem? Item
    {
        get => (LootItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public bool ApplyStyle
    {
        get => (bool)GetValue(ApplyStyleProperty);
        set => SetValue(ApplyStyleProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public Color FontColor
    {
        get => (Color)GetValue(FontColorProperty);
        set => SetValue(FontColorProperty, value);
    }

    public Color BackgroundColor
    {
        get => (Color)GetValue(BackgroundColorProperty);
        set => SetValue(BackgroundColorProperty, value);
    }

    public Color BorderColor
    {
        get => (Color)GetValue(BorderColorProperty);
        set => SetValue(BorderColorProperty, value);
    }

    public double BorderThickness
    {
        get => (double)GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public bool Flare
    {
        get => (bool)GetValue(FlareProperty);
        set => SetValue(FlareProperty, value);
    }

    public bool PlayEffectSound
    {
        get => (bool)GetValue(PlayEffectSoundProperty);
        set => SetValue(PlayEffectSoundProperty, value);
    }

    private static void OnFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ItemPreviewControl control && e.NewValue is double)
        {
            control.ApplyFontSize();
        }
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ItemPreviewControl control)
        {
            control.ApplyItem();
        }
    }

    public ItemPreviewControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        var root = new Grid();
        root.Margin = new Thickness(0);

        _itemBorder = new Border
        {
            CornerRadius = new CornerRadius(1),
            Margin = new Thickness(2),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = Top
        };

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _itemIcon = new Rectangle
        {
            Height = 64,
            Width = 64,
            Fill = Brushes.Gray,
            Margin = new Thickness(2)
        };

        var iconGrid = new Grid();
        iconGrid.Children.Add(_itemIcon);

        _itemType = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeight.Parse("SemiBold"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2)
        };

        _itemRarity = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.Parse("SemiBold"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = Brushes.Gray,
            Margin = new Thickness(2)
        };

        _itemModifiers = new StackPanel
        {
            Margin = new Thickness(2),
            VerticalAlignment = Top,
            Spacing = 3
        };

        outer.Children.Add(iconGrid);
        outer.Children.Add(_itemType);
        outer.Children.Add(_itemRarity);
        outer.Children.Add(_itemModifiers);

        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = outer
        };

        root.Children.Add(scrollViewer);
        this.Content = root;

        ApplyItem();
    }

    private void ApplyItem()
    {
        var item = Item;
        if (item == null)
        {
            _itemType.Text = "";
            _itemRarity.Text = "";
            _itemModifiers.Children.Clear();
            return;
        }

        if (ApplyStyle)
        {
            ApplyFontSize();
            _itemType.Foreground = new SolidColorBrush(FontColor);
            _itemRarity.Foreground = new SolidColorBrush(FontColor);
            foreach (var modifier in _itemModifiers.Children.OfType<TextBlock>())
            {
                modifier.Foreground = new SolidColorBrush(FontColor);
            }
            _itemBorder.Background = new SolidColorBrush(BackgroundColor);
            _itemBorder.BorderBrush = new SolidColorBrush(BorderColor);
            _itemBorder.BorderThickness = new Thickness(BorderThickness);
        }

        var iconFill = GetRarityColour(item.Rarity);
        _itemIcon.Fill = new SolidColorBrush(iconFill);
        _itemIcon.Width = 64;
        _itemIcon.Height = 64;

        _itemType.Text = item.BaseType;
        _itemRarity.Text = item.Rarity;

        _itemModifiers.Children.Clear();
        if (item.Modifiers.Count > 0)
        {
            var prefix = new TextBlock { Text = " ", Foreground = Brushes.Gray };
            _itemModifiers.Children.Add(prefix);
            foreach (var modifier in item.Modifiers)
            {
                _itemModifiers.Children.Add(new TextBlock { Text = modifier });
            }
        }

        ApplyFontSize();
        SetFlareAnimation();
    }

    private void ApplyFontSize()
    {
        _itemType.FontSize = FontSize;
        _itemRarity.FontSize = Math.Min(FontSize * 0.85, 16);
        foreach (var modifier in _itemModifiers.Children.OfType<TextBlock>())
        {
            modifier.FontSize = Math.Max(FontSize * 0.85, 13);
        }
    }

    private void SetFlareAnimation()
    {
        if (!Flare)
        {
            _itemBorder.ClearValue(Border.OpacityProperty);
            return;
        }

        var storyboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true
        };
        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.3,
            Duration = TimeSpan.FromMilliseconds(800),
            EasingFunction = new ElasticEase { Oscillations = 2, SpringLength = 0.3 }
        };
        Storyboard.SetTarget(anim, _itemBorder);
        Storyboard.SetTargetProperty(anim, new PropertyPath(Border.OpacityProperty));
        storyboard.Children.Add(anim);
        _itemBorder.BeginStoryboard(storyboard);
    }

    private static Color GetRarityColour(string rarity)
    {
        return rarity.ToLowerInvariant() switch
        {
            "normal" => Color.FromArgb(255, 150, 150, 150),
            "magic" => Color.FromArgb(255, 100, 200, 255),
            "rare" => Color.FromArgb(255, 255, 255, 100),
            "unique" => Color.FromArgb(255, 255, 200, 100),
            "currency" => Color.FromArgb(255, 255, 120, 240),
            "gem" => Color.FromArgb(255, 150, 150, 255),
            "relic" => Color.FromArgb(255, 200, 200, 150),
            "fragment" => Color.FromArgb(255, 120, 120, 255),
            "quest" => Color.FromArgb(255, 100, 255, 100),
            _ => Colors.White
        };
    }
}