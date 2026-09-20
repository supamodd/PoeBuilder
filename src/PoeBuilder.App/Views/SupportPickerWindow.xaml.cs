using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using PoeBuilder.App.Services;
using PoeBuilder.App.ViewModels;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Skills;

namespace PoeBuilder.App.Views;

/// <summary>Round-socket support picker: search + game icons, already-used supports excluded.</summary>
public sealed class SupportPickerViewModel : Observable
{
    private readonly GameCatalog _catalog;
    private readonly HashSet<string> _exclude;
    private string _search = "";
    public string Title { get; }
    public string Hint { get; }
    public string CancelLabel { get; }
    public string PickLabel { get; }
    public ObservableCollection<Gem> Items { get; } = [];
    public Gem? SelectedGem { get; set; }
    public string Search { get => _search; set { if (Set(ref _search, value)) Reload(); } }
    public SupportPickerViewModel(PoeBuilder.App.Services.Localization l, GameCatalog catalog, GemSelection[] exclude)
    {
        L = l; _catalog = catalog; _exclude = exclude.Select(s => s.GemId).ToHashSet();
        Title = l["SupportPickerTitle"];
        Hint = l["SupportPickerHint"];
        CancelLabel = l["Cancel"];
        PickLabel = l["PickSupport"];
        Reload();
    }
    public PoeBuilder.App.Services.Localization L { get; }
    private void Reload()
    {
        Items.Clear();
        foreach (var gem in _catalog.Gems.Values
            .Where(g => g.Kind == "support" && !_exclude.Contains(g.Id) &&
                (Search.Length == 0 || (g.Name + " " + g.Description).Contains(Search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(g => g.Name).Take(300))
            Items.Add(gem);
    }
}

public partial class SupportPickerWindow : Window
{
    private readonly SupportPickerViewModel _vm;
    public Gem? Selected => _vm.SelectedGem;
    public SupportPickerWindow(PoeBuilder.App.Services.Localization l, GameCatalog catalog, GemSelection[] exclude)
    {
        InitializeComponent();
        EnsureResources();
        _vm = new SupportPickerViewModel(l, catalog, exclude);
        DataContext = _vm;
    }
    private void OnPick(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGem is null) return;
        DialogResult = true;
    }
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedGem is not null) DialogResult = true;
    }
    private void OnCancel(object sender, RoutedEventArgs e) => Close();
    /// <summary>The window can be opened before the main page registers app-level converters; re-add if missing.</summary>
    private void EnsureResources()
    {
        if (TryFindResource("GemIcon") is null) Resources["GemIcon"] = new GemIconConverter();
        if (TryFindResource("GemBrush") is null) Resources["GemBrush"] = new GemBrushConverter();
    }
}
