using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PoeBuilder.Core.Models;
using PoeBuilder.Core.Tree;

namespace PoeBuilder.App.Services;

public sealed class BuildCardArtworkConverter : IMultiValueConverter
{
    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not BuildDocument build) return null;
        var catalog = values.Length > 1 ? values[1] as TreeCatalog : null;
        var art = TreeClassArtTable.ForBuild(build.Tree, catalog, build.CharacterClass);
        if (art is null) return null;
        if (Cache.TryGetValue(art.Sprite, out var cached)) return cached;
        string path = Path.Combine(AppContext.BaseDirectory, "Data", "Tree", "Art", "class", art.Sprite + ".png");
        if (!File.Exists(path)) return null;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
        Cache[art.Sprite] = bitmap;
        return bitmap;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        [.. targetTypes.Select(_ => Binding.DoNothing)];
}

public sealed class PageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool visible = value is true;
        if (parameter?.ToString() == "Invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// The sidebar's fold, read from one animated 0..1 value instead of a boolean. The rail's width, its margins,
/// the inset that keeps an icon centred while the rail is narrow, and the fade of the tab names all come from
/// this single progress, so the panel cannot open in two steps: a boolean trigger snaps the width on one frame
/// and the names appear on another, which reads as a jerk.
/// </summary>
public sealed class SidebarFoldConverter : IValueConverter
{
    public const double Folded = 64, Expanded = 245;
    // The nav item's own padding and glyph size, from the NavList template: they decide how far a centred icon
    // has to sit off the left edge while the rail is only Folded wide.
    private const double ItemPadding = 13, Icon = 19, Mark = 37, MarkMargin = 8;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double progress = value is double p ? Math.Clamp(p, 0, 1) : 0;
        return parameter?.ToString() switch
        {
            "Width" => Folded + (Expanded - Folded) * progress,
            // No breathing room while folded, the open panel's 18 px sides and 22/18 top and bottom.
            "Margin" => new Thickness(18 * progress, 22 * progress, 18 * progress, 18 * progress),
            // Centring offset for a nav icon inside the room the folded rail leaves between the item's own
            // padding; it slides back to the plain left margin as the rail opens.
            "IconInset" => (Folded - 2 * ItemPadding - Icon) / 2 * (1 - progress),
            // Same for the wider logo mark, which the folded rail centres and the open one insets.
            "LogoInset" => (Folded - Mark) / 2 + (MarkMargin - (Folded - Mark) / 2) * progress,
            "Opacity" => progress,
            _ => 0.0
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// The sidebar fold as an animatable value: <see cref="Progress"/> runs 0 (folded) to 1 (expanded) and every
/// part of the panel reads it, so one animation drives the width, the margins, the icon centring and the fade
/// of the names together. It is a dependency object rather than a plain view-model flag because a
/// <c>DoubleAnimation</c> can only animate a dependency property.
/// </summary>
public sealed class SidebarFoldState : DependencyObject
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(SidebarFoldState), new PropertyMetadata(0.0));
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>Animates the fold to fully open or fully shut, and <c>SetNow</c> snaps it there for a layout
    /// check that cannot wait out the animation.
    /// <para>The motion is driven by a dispatcher timer rather than a <c>DoubleAnimation</c>: WPF's
    /// <c>BeginAnimation</c> lives on the internal <c>AnimationStorage</c>, so a <c>Storyboard</c> is the only
    /// public route to it and it wants a live framework element. Ticking the value ourselves keeps the state a
    /// plain object, and restarts from wherever the fold currently is when the target changes mid-flight.</para></summary>
    public void AnimateTo(bool expanded, TimeSpan duration, bool easeOut)
    {
        double target = expanded ? 1 : 0;
        if (duration <= TimeSpan.Zero || Math.Abs(Progress - target) < 0.001)
        {
            SetNow(expanded);
            return;
        }
        _from = Progress;
        _to = target;
        _seconds = Math.Max(0.001, duration.TotalSeconds);
        _easeOut = easeOut;
        _clock.Restart();
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (_, _) => Tick();
        }
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void SetNow(bool expanded)
    {
        _timer?.Stop();
        Progress = expanded ? 1 : 0;
    }

    private void Tick()
    {
        double t = Math.Min(1, _clock.Elapsed.TotalSeconds / _seconds);
        // Cubic ease-out on the way open (it leaves immediately, then settles) and ease-in-out on the way shut,
        // so neither end of the fold arrives with a jolt.
        double eased = _easeOut
            ? 1 - Math.Pow(1 - t, 3)
            : t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        Progress = _from + (_to - _from) * eased;
        if (t < 1) return;
        _timer!.Stop();
        Progress = _to;
    }

    private DispatcherTimer? _timer;
    private readonly Stopwatch _clock = new();
    private double _from, _to, _seconds;
    private bool _easeOut;
}

public sealed class WideViewportConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        ViewportSizing.Width(values.Length > 0 && values[0] is double w ? w : 0, values.Length > 1 && values[1] is double h ? h : 0);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class WideHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => ViewportSizing.Height(value is double d ? d : 1);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class EmptyVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value?.ToString()) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>String → ToolTip: empty string becomes null so no empty tooltip box appears.
/// Deliberately a plain converter, not a Style trigger: trigger-based null tooltips inside a
/// DataTemplate crash XAML load at runtime (0.8.0 startup XamlParseException).</summary>
public sealed class EmptyToNullConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value?.ToString()) ? null : value.ToString();
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Rarity → border/text colour, matching the game's loot colours.</summary>
public sealed class RarityBrushConverter : IValueConverter
{
    private static readonly Brush Normal = Freeze("#C8C8C8");
    private static readonly Brush Magic = Freeze("#8888FF");
    private static readonly Brush Rare = Freeze("#FFFF77");
    private static readonly Brush Unique = Freeze("#D9A441");
    private static readonly Brush Empty = Freeze("#3A4552");
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value?.ToString() switch
    {
        "magic" => Magic, "rare" => Rare, "normal" => Normal, "unique" => Unique, _ => Empty
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze(); return brush;
    }
}

/// <summary>Gem attribute colour (r/s = strength red, g = dexterity green, b = intelligence blue).</summary>
public sealed class GemBrushConverter : IValueConverter
{
    private static readonly Brush Str = Freeze("#C5443C");
    private static readonly Brush Dex = Freeze("#4FAE54");
    private static readonly Brush Int = Freeze("#4C7FD0");
    private static readonly Brush Other = Freeze("#7A8794");
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value?.ToString() switch
    {
        "r" or "s" => Str, "g" => Dex, "b" => Int, _ => Other
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze(); return brush;
    }
}

/// <summary>Formats a decimal as invariant text for display rows.</summary>
public sealed class DecimalTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is decimal d ? d.ToString("0.##", CultureInfo.InvariantCulture) : value?.ToString() ?? "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>ItemBase → bundled game icon.</summary>
public sealed class BaseIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is PoeBuilder.Core.Equipment.ItemBase b ? IconService.Instance.ForBase(b) : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>bool → inverted bool (radio pairs bound to one flag).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Enum value → bool, so a radio button can bind its own checked state to a shared enum property
/// (the RegEx tab's AND / OR / Mixed selector) without a code-behind round trip.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null && string.Equals(value.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter?.ToString() is { } name ? Enum.Parse(targetType, name, true) : Binding.DoNothing;
}

/// <summary>bool → FontWeight for preview headline lines.</summary>
public sealed class BoolToBoldConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Zero count → visible (empty-state hints).</summary>
public sealed class ZeroVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool zero = value is int n and 0 || value is null || (value is System.Collections.ICollection c && c.Count == 0);
        bool invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        return zero != invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Gem (or gem id string) → bundled game icon.</summary>
public sealed class GemIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        PoeBuilder.Core.Equipment.Gem g => IconService.Instance.ForGem(g.Id),
        string id => IconService.Instance.ForGem(id),
        _ => null
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
