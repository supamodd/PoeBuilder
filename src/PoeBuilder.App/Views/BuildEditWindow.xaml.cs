using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using PoeBuilder.App.Services;
using PoeBuilder.App.ViewModels;
using PoeBuilder.Core.Tree;
using Localization = PoeBuilder.App.Services.Localization;

namespace PoeBuilder.App.Views;

/// <summary>Edits the identity fields of the active build: name, level, stage, game version, class and ascendancy.
/// Class and ascendancy go through TreeViewModel, so all guards (allocation reset confirmations) apply unchanged.
/// Replaces the removed Configuration tab (remark 5): same data, opened from the build card via "Изменить".</summary>
public partial class BuildEditWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _main;
    private string _errorText = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    public Localization L => _main.L;
    public BuildEditor? Editor => _main.Editor;
    public TreeViewModel Tree => _main.Tree;
    public IReadOnlyList<string> GameVersions => _main.GameVersions;
    public string ErrorText { get => _errorText; private set { _errorText = value; Raise(); } }

    public BuildEditWindow(MainViewModel main)
    {
        _main = main;
        _ = main.Tree.Catalog ?? throw new InvalidOperationException("Tree catalog is not loaded.");
        InitializeComponent();
        DataContext = this;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (_main.Editor is null || string.IsNullOrWhiteSpace(_main.Editor.Name)) { ErrorText = _main.L["NameError"]; return; }
        DialogResult = true;
    }
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
