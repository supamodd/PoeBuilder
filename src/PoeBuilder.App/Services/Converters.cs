using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PoeBuilder.App.Services;

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
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
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
