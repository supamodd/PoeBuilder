using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Text;
using PoeBuilder.App.Services;
using PoeBuilder.App.Views;
using PoeBuilder.Core.Localization;
using System.Text.Json;
using Localization = PoeBuilder.App.Services.Localization;
using PoeBuilder.Core.Calculation;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Models;
using PoeBuilder.Core.Storage;
using PoeBuilder.Core.Tree;

namespace PoeBuilder.App.ViewModels;

public sealed class NavItem(string key, string icon, Localization localization) : Observable
{
    public string Key { get; } = key;
    public string Icon { get; } = icon;
    public string Label => localization[Key];
    /// <summary>
    /// A drawn PNG for this tab, if the owner dropped <c>assets\NavIcons\{Key}.png</c> into the project.
    /// It is packed as a WPF resource, so it only exists once the file is there — hence the probe instead of
    /// a direct load, which would throw at startup for every tab that still has no artwork. The sidebar falls
    /// back to <see cref="Icon"/>'s path geometry when this is null.
    /// </summary>
    public ImageSource? Glyph { get; } = LoadGlyph(key);
    public bool HasGlyph => Glyph is not null;
    public void Refresh() => Raise(nameof(Label));

    private static ImageSource? LoadGlyph(string key)
    {
        var uri = new Uri($"pack://application:,,,/PoeBuilder;component/Assets/NavIcons/{key}.png", UriKind.Absolute);
        // GetResourceStream throws for an absent part rather than returning null, and until the artwork lands
        // every one of the ten tabs is absent — so the probe has to swallow that, not just read a null.
        try
        {
            var probe = Application.GetResourceStream(uri);
            if (probe?.Stream is null) return null;
            probe.Stream.Close();
            var bitmap = new BitmapImage(uri);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
public sealed record LanguageChoice(string Code, string DisplayName);

public sealed class MainViewModel : Observable
{
    public Localization L { get; } = new();
    public TreeViewModel Tree { get; }
    public EquipmentViewModel Equipment { get; }
    public JewelsViewModel Jewels { get; private set; } = null!;
    public SkillsViewModel Skills { get; }
    public CharacterViewModel Character { get; }
    /// <summary>The Quest Rewards tab's table and the Configuration tab's conditions — both live before
    /// the Character sheet, exactly like in PoB2.</summary>
    public QuestRewardsViewModel QuestRewards { get; }
    public ConfigViewModel Config { get; }
    /// <summary>The RegEx tab: a standalone trade-filter builder. It reads game data only, never the open
    /// build, so it stays usable with nothing loaded — which is when a trader is looking for a filter.</summary>
    public RegexViewModel RegEx { get; }
    /// <summary>The Filter tab: a loot-filter editor with its own library. A filter belongs to no character,
    /// so this one too works with nothing open. Named LootFilter because <c>Filter</c> is already the build
    /// library's own filtering method.</summary>
    public FilterViewModel LootFilter { get; }
    public string Version => "0.9.7 · unaccounted list closed (0 lines), PoB2-verified classification";
    public string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoeBuilder", "Native");
    private readonly BuildRepository _builds;
    private readonly SettingsRepository _settingsRepository;
    private AppSettings _settings = new();
    private List<BuildDocument> _library = [];
    private BuildEditor? _editor;
    private string _search = "", _status = "";
    private bool _isBusy, _initialized;
    private bool _navExpanded;
    private NavItem _selectedNav;
    private LanguageChoice _selectedLanguage;

    public ObservableCollection<NavItem> Navigation { get; }
    public ObservableCollection<BuildDocument> VisibleBuilds { get; } = [];
    public IReadOnlyList<LanguageChoice> Languages { get; } = [new("ru", "Русский"), new("en", "English")];
    // Real PoE2 patch releases per GGG public patch notes (major versions, their letters and
    // hotfix chains; 0.5.5b/c per the user's Steam client). Listed choices, no free-text entry.
    public IReadOnlyList<string> GameVersions { get; } =
    [
        "0.1.0", "0.1.0b", "0.1.0c", "0.1.0d", "0.1.0e", "0.1.0f",
        "0.1.1", "0.1.1b", "0.1.1c", "0.1.1d", "0.1.1e", "0.1.1f", "0.1.1g",
        "0.2.0", "0.2.0b", "0.2.0c", "0.2.0d", "0.2.0e", "0.2.0f", "0.2.0g", "0.2.0h",
        "0.2.1", "0.2.1b", "0.2.1c",
        "0.3.0", "0.3.0b", "0.3.0c", "0.3.1",
        "0.4.0", "0.4.0b", "0.4.0c", "0.4.0d", "0.4.0e", "0.4.0f", "0.4.0g", "0.4.0h", "0.4.0i",
        "0.5.0", "0.5.0b", "0.5.1", "0.5.2", "0.5.3", "0.5.4", "0.5.5", "0.5.5b", "0.5.5c"
    ];
    public BuildEditor? Editor => _editor;
    public bool HasBuild => _editor is not null;
    public bool HasNoBuild => !HasBuild;
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); Raise(nameof(CanWork)); CommandManager.InvalidateRequerySuggested(); } }
    public bool CanWork => !IsBusy;
    public bool HasNoVisibleBuilds => VisibleBuilds.Count == 0;
    public string EmptyTitle => _library.Count == 0 ? L["EmptyLibrary"] : L["NoResults"];
    public string BuildCount => L.Format("BuildCount", _library.Count);
    public string CurrentName => Editor?.Name ?? L["NoBuild"];
    public string SaveState => Editor is null ? L["NoBuild"] : Editor.IsDirty ? L["Unsaved"] : L["Saved"];
    public string TargetGameVersion => _settings.TargetGameVersion;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Search { get => _search; set { if (Set(ref _search, value)) Filter(); } }
    // The sidebar starts folded down to its icons and slides its tab names out while the pointer (or the
    // keyboard) is on it. Kept here so the fold survives a language switch and a build reload.
    public bool NavExpanded { get => _navExpanded; private set => Set(ref _navExpanded, value); }
    public void SetNavExpanded(bool expanded) => NavExpanded = expanded;

    public NavItem SelectedNav { get => _selectedNav; set { if (value is not null && Set(ref _selectedNav, value)) { Raise(nameof(Page)); Raise(nameof(PageTitle)); } } }
    public string Page => SelectedNav.Key;
    public string PageTitle => SelectedNav.Label;
    public LanguageChoice SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is not null && Set(ref _selectedLanguage, value) && _initialized)
                _ = RunSafeAsync(() => ApplyLanguageAsync(value.Code));
        }
    }

    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand AddStageCommand { get; }
    public ICommand RenameStageCommand { get; }
    public ICommand DeleteStageCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand EditBuildCommand { get; }
    public ICommand ImportCodeCommand { get; }
    public ICommand ImportFileCommand { get; }
    public ICommand ExportBuildCommand { get; }
    public GameCatalog? Catalog { get; private set; }

    public MainViewModel()
    {
        Tree = new(L);
        // The class chosen on the Configuration page drives both the tree start and the build label.
        Tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not nameof(TreeViewModel.SelectedClass) || _editor is null || Tree.SelectedClass is null) return;
            if (_editor.CharacterClass != Tree.SelectedClass.Name) _editor.CharacterClass = Tree.SelectedClass.Name;
        };
        Equipment = new(L);
        Skills = new(L);
        Jewels = new(L);
        _builds = new(Path.Combine(DataDirectory, "Builds"));
        _settingsRepository = new(Path.Combine(DataDirectory, "settings.json"));
        Navigation = [
            new("Builds", "M3,3 L9,3 9,9 3,9 Z M13,3 L19,3 19,9 13,9 Z M3,13 L9,13 9,19 3,19 Z M13,13 L19,13 19,19 13,19 Z", L),
            new("Tree", "M11,2 L11,7 M4,18 L4,12 18,12 18,18 M11,7 L11,17 M8,2 L14,2 14,7 8,7 Z M1,18 L7,18 7,22 1,22 Z M15,18 L21,18 21,22 15,22 Z", L),
            new("Items", "M11,2 L20,6 19,15 11,22 3,15 2,6 Z M11,6 L11,17", L),
            new("Jewels", "M12,2 L20,7 20,17 12,22 4,17 4,7 Z M12,7 L16,9.5 16,14.5 12,17 8,14.5 8,9.5 Z", L),
            new("Skills", "M12,1 L4,13 10,13 8,23 20,9 13,9 Z", L),
            new("QuestRewards", "M4,2 L16,2 21,7 21,22 4,22 Z M16,2 L16,7 21,7 M7,12 L9,14 13,10 M7,17 L9,19 13,15", L),
            new("Configuration", "M12,3 A3,3 0 1,1 12,9 A3,3 0 1,1 12,3 M3,6 L9,6 M15,6 L21,6 M3,18 L9,18 M15,18 L21,18 M12,15 A3,3 0 1,1 12,21 A3,3 0 1,1 12,15", L),
            new("Character", "M8,2 L17,2 17,7 8,7 Z M4,10 L20,10 20,14 4,14 Z M8,17 L17,17 17,22 8,22 Z", L),
            new("Notes", "M4,2 L16,2 21,7 21,22 4,22 Z M16,2 L16,7 21,7 M8,11 L17,11 M8,15 L17,15 M8,19 L14,19", L),
            // RegEx sits just before Settings: it is a standalone tool like Notes, and a player looking for a
            // trade filter wants it near the bottom rather than between the planning tabs.
            new("RegEx", "M4,4 L20,4 M9,4 L9,9 C9,13 4,13 4,17 C4,21 9,21 9,21 M4,9 L20,9 M4,21 L20,21", L),
            // The loot-filter editor follows the search-string generator: both are standalone tools a player
            // reaches for outside a build.
            new("Filter", "M3,5 L21,5 L21,19 L3,19 Z M3,9 L21,9 M8,13 L12,13 M8,16 L16,16 M17,13 L19,13", L),
            new("Settings", "M3,6 L21,6 M3,17 L21,17 M8,2 L8,10 M16,13 L16,21", L)
        ];
        Character = new(L, this);
        QuestRewards = new(L);
        Config = new(L);
        RegEx = new(L);
        LootFilter = new(L, Path.Combine(DataDirectory, "Filters"));
        _selectedNav = Navigation[0]; _selectedLanguage = Languages[0];
        NewCommand = Command(async _ =>
        {
            if (!await ConfirmSwitchAsync()) return;
            var previousId = Editor?.Id;
            SetEditor(BuildDocument.Create(L["NewName"], _settings.TargetGameVersion), true);
            var window = new BuildEditWindow(this) { Owner = Application.Current.MainWindow };
            if (window.ShowDialog() == true) { Navigate("Tree"); Status = L.Format("NewStarted", Editor!.Name); }
            else
            {
                if (previousId is Guid id) SetEditor(await BuildRepository.ReadDocumentAsync(_builds.PathFor(id)));
                else ClearEditor();
            }
        });
        SaveCommand = Command(_ => SaveCurrentAsync(), () => Editor?.IsValid == true);
        AddStageCommand = new ActionCommand(_ => AddStage(), () => Editor?.CanChangeStage == true && Editor.Stages.Count < 32);
        RenameStageCommand = new ActionCommand(_ => RenameStage(), () => Editor is not null);
        DeleteStageCommand = new ActionCommand(_ => DeleteStage(), () => Editor is { CanChangeStage: true } editor && editor.Stages.Count > 1);
        ImportCommand = Command(_ => ImportAsync());
        ExportCommand = Command(_ => ExportAsync(), () => Editor?.IsValid == true);
        OpenCommand = Command(async arg => { if (arg is BuildDocument build && await ConfirmSwitchAsync()) { SetEditor(await BuildRepository.ReadDocumentAsync(_builds.PathFor(build.Id))); Navigate("Tree"); Status = L.Format("OpenedStatus", build.Name); } });
        DuplicateCommand = Command(async arg =>
        {
            if (arg is not BuildDocument build || !await ConfirmSwitchAsync()) return;
            var source = await BuildRepository.ReadDocumentAsync(_builds.PathFor(build.Id));
            var name = L.Format("CopyName", source.Name[..Math.Min(65, source.Name.Length)]);
            var copy = await _builds.DuplicateAsync(source, name);
            await RefreshLibraryAsync(); SetEditor(copy); Navigate("Tree");
        });
        DeleteCommand = Command(async arg =>
        {
            if (arg is not BuildDocument build) return;
            if (Editor?.Id == build.Id && !await ConfirmSwitchAsync()) return;
            if (ThemedDialog.Show(L.Format("DeleteQuestion", build.Name), L["Confirm"], MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await _builds.MoveToTrashAsync(build.Id);
            if (Editor?.Id == build.Id) ClearEditor();
            await RefreshLibraryAsync(); Status = L["DeletedStatus"];
        });
        OpenFolderCommand = Command(_ =>
        {
            Directory.CreateDirectory(DataDirectory);
            Process.Start(new ProcessStartInfo { FileName = DataDirectory, UseShellExecute = true });
            return Task.CompletedTask;
        });
        SettingsCommand = new ActionCommand(_ => Navigate("Settings"));
        EditBuildCommand = Command(async arg => await EditBuildAsync(arg as BuildDocument), () => Tree.Catalog is not null);
        ImportCodeCommand = Command(async _ => await ImportExchangeAsync(null), () => Tree.Catalog is not null);
        ImportFileCommand = Command(async _ => await ImportExchangeAsync("file"), () => Tree.Catalog is not null);
        ExportBuildCommand = Command(async arg => await ExportBuildAsync(arg as BuildDocument), () => Tree.Catalog is not null);
        RefreshCommand = Command(_ => RefreshLibraryAsync());
    }
    private ICommand Command(Func<object?, Task> action, Func<bool>? extra = null) =>
        new AsyncCommand(arg => RunSafeAsync(() => action(arg)), () => CanWork && (extra?.Invoke() ?? true));

    public async Task InitializeAsync() => await RunSafeAsync(async () =>
    {
        var read = await _settingsRepository.ReadAsync();
        _settings = read.Settings; L.SetLanguage(_settings.Language);
        _selectedLanguage = Languages.First(x => x.Code == _settings.Language); Raise(nameof(SelectedLanguage));
        IconService.Instance.Initialize();
        RefreshLanguage(); await RefreshLibraryAsync(); await Tree.InitializeAsync();
        // The filter library is read on its own: a filter belongs to no character, so the tab has to be ready
        // whether or not a build is open.
        await LootFilter.LoadLibraryAsync();
        try
        {
            var catalog = await Task.Run(() => GameCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "catalog.json")));
            Catalog = catalog;
            Equipment.SetCatalog(catalog); Skills.SetCatalog(catalog); Jewels.SetCatalog(catalog); QuestRewards.SetIndex(catalog.QuestRewards);
            RegEx.SetCatalog(catalog);
            // Russian game text, loaded once and shared. It is a separate file from the pinned English
            // data so the ids the calculation works on never change; only what is displayed does.
            await LoadGameStringsAsync();
            var statMap = await Task.Run(() => GameStatMap.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "statmap.json")));
            await Task.Run(() => PoeBuilder.Core.Calculation.ReverseStatTextMatcher.UseFile(
                Path.Combine(AppContext.BaseDirectory, "Data", "Game", "stat_text_reverse.json")));
            Character.SetData(Tree.Catalog, statMap, catalog);
            // PoB2-style hover tooltips: ask the character sheet what a hovered node would change.
            Tree.NodeImpactProvider = id => Character.NodeImpact(id);
            // PoB2 shows the socketed jewel (and its radius) when hovering a jewel socket.
            Tree.SocketInfoProvider = id => Jewels.SocketInfo(id);
            Tree.JewelRadiusProvider = id => Jewels.SocketBand(id);
            Tree.JewelIconProvider = id => Jewels.SocketIcon(id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        { Status = L["CatalogMissing"] + "\n" + e.Message; }
        if (read.RecoveredFromError) Status = L["SettingsRecovered"];
        else if (string.IsNullOrEmpty(Status)) Status = L["Ready"];
        _initialized = true;
    });

    public void Navigate(string key) => SelectedNav = Navigation.First(x => x.Key == key);

    /// <summary>The active game-text locale, exposed so the UI can report coverage and provenance.</summary>
    public GameLocale? GameLocale { get; private set; }

    /// <summary>Loads the Russian game text and hands it to every tab that shows a game name. A missing
    /// file is not an error: the tabs keep showing the pinned English names.</summary>
    private async Task LoadGameStringsAsync()
    {
        var strings = await Task.Run(() =>
        {
            try { return GameStrings.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "locale-ru.json")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return GameStrings.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "does-not-exist.json")); }
        });
        var locale = new GameLocale(strings, L);
        GameLocale = locale;
        Skills.Locale = locale; Equipment.Locale = locale; Jewels.Locale = locale;
        // The RegEx tab shows Russian affix text but is fed the English templates, so the string it builds
        // is the one the game itself would match against whatever language the client is in.
        RegEx.Locale = locale;
        // Tree.Strings hands itself to the ascendancy tree nested inside it, so one assignment covers both.
        Tree.Strings = strings;
        Raise(nameof(GameLocale));
    }
    private async Task ApplyLanguageAsync(string language)
    {
        // Persist first: a failed write does not falsely report a saved preference.
        var next = _settings with { Language = language };
        try
        {
            await _settingsRepository.SaveAsync(next);
            _settings = next; L.SetLanguage(language); RefreshLanguage(); Status = L["Ready"];
        }
        catch
        {
            _selectedLanguage = Languages.First(x => x.Code == _settings.Language);
            Raise(nameof(SelectedLanguage)); throw;
        }
    }
    private void RefreshLanguage()
    {
        foreach (var nav in Navigation) nav.Refresh();
        Raise(nameof(PageTitle)); Raise(nameof(CurrentName)); Raise(nameof(SaveState)); Raise(nameof(BuildCount)); Raise(nameof(EmptyTitle)); Raise(nameof(TargetGameVersion));
    }
    private void SetEditor(BuildDocument build, bool isNew = false)
    {
        ErrorLog.Mark("SetEditor " + (isNew ? "new" : "open") + " · " + build.Name);
        if (_editor is not null) { _editor.PropertyChanged -= EditorChanged; _editor.StageActivated -= EditorStageActivated; }
        _editor = new(build, isNew); _editor.PropertyChanged += EditorChanged; _editor.StageActivated += EditorStageActivated;
        BindEditorModules();
        RaiseEditorProperties();
    }
    private void BindEditorModules()
    {
        // One misbehaving module must not abort the others: bind in isolation and report.
        BindModule("Items", () => Equipment.BindEditor(_editor));
        BindModule("Skills", () => Skills.BindEditor(_editor));
        BindModule("Jewels", () => Jewels.BindEditor(_editor, Tree));
        BindModule("Tree", () => Tree.BindEditor(_editor));
        BindModule("QuestRewards", () => QuestRewards.BindEditor(_editor));
        BindModule("Config", () => Config.BindEditor(_editor));
        BindModule("Character", () => Character.BindEditor(_editor));
    }
    private void EditorStageActivated() { BindEditorModules(); RaiseEditorProperties(); }
    private void BindModule(string module, Action action)
    {
        try { action(); }
        catch (Exception e)
        {
            ErrorLog.Append(e, "Bind:" + module);
            Status = L["Error"] + " · " + module + ": " + e.Message;
        }
    }
    private void ClearEditor()
    {
        if (_editor is not null) { _editor.PropertyChanged -= EditorChanged; _editor.StageActivated -= EditorStageActivated; }
        _editor = null; Tree.BindEditor(null); Equipment.BindEditor(null); Skills.BindEditor(null); Jewels.BindEditor(null, null);
        QuestRewards.BindEditor(null); Config.BindEditor(null); Character.BindEditor(null); RaiseEditorProperties();
    }
    private void RaiseEditorProperties()
    {
        Raise(nameof(Editor)); Raise(nameof(HasBuild)); Raise(nameof(HasNoBuild)); Raise(nameof(CurrentName)); Raise(nameof(SaveState));
        CommandManager.InvalidateRequerySuggested();
    }
    private void EditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        Raise(nameof(CurrentName)); Raise(nameof(SaveState)); CommandManager.InvalidateRequerySuggested();
        // The character sheet subscribes to the same editor itself (CharacterViewModel.BindEditor), so
        // calling Character.Recalculate() here would run the whole calculation — and rebuild the whole
        // sheet — twice per keystroke.
    }
    private void AddStage()
    {
        if (Editor is null) return;
        string? name = PromptStageName(L["AddStage"], L["StageDefaultNew"]);
        if (name is null) return;
        if (!Editor.AddStage(name)) { ThemedDialog.Show(L["StageNameConflict"], L["AddStage"], MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Status = L.Format("StageAdded", name.Trim());
    }
    private void RenameStage()
    {
        if (Editor is null) return;
        string? name = PromptStageName(L["RenameStage"], Editor.CurrentStageName);
        if (name is null) return;
        if (!Editor.RenameStage(Editor.CurrentStageId, name)) { ThemedDialog.Show(L["StageNameConflict"], L["RenameStage"], MessageBoxButton.OK, MessageBoxImage.Information); return; }
    }
    private void DeleteStage()
    {
        if (Editor is null || ThemedDialog.Show(L["DeleteStageQuestion"], L["Confirm"], MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (Editor.RemoveStage(Editor.CurrentStageId)) Status = L["StageDeleted"];
    }
    private string? PromptStageName(string title, string initial)
    {
        var dialog = new Window
        {
            Title = title, Width = 390, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current.MainWindow,
            Background = new SolidColorBrush(Color.FromRgb(21, 28, 34)), Foreground = Brushes.White,
            ShowInTaskbar = false
        };
        var input = new TextBox { Text = initial, MaxLength = 40, MinHeight = 30, Margin = new Thickness(0, 6, 0, 14), Padding = new Thickness(8, 5, 8, 5) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var accept = new Button { Content = L["Save"], IsDefault = true, MinWidth = 86, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
        var cancel = new Button { Content = L["Cancel"], IsCancel = true, MinWidth = 86, Padding = new Thickness(12, 6, 12, 6) };
        accept.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(accept); buttons.Children.Add(cancel);
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = L["StageNamePrompt"] }); content.Children.Add(input); content.Children.Add(buttons);
        dialog.Content = content;
        ThemedWindowChrome.Apply(dialog);
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }
    private async Task RefreshLibraryAsync()
    {
        var result = await _builds.ReadLibraryAsync(); _library = result.Builds.ToList(); Filter();
        Status = result.UnreadableFiles.Count > 0 ? L.Format("SkippedFiles", result.UnreadableFiles.Count) : L["Ready"];
    }
    private void Filter()
    {
        VisibleBuilds.Clear(); var query = Search.Trim();
        foreach (var item in _library.Where(b => query.Length == 0 || b.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || b.CharacterClass.Contains(query, StringComparison.OrdinalIgnoreCase)))
            VisibleBuilds.Add(item);
        Raise(nameof(HasNoVisibleBuilds)); Raise(nameof(EmptyTitle)); Raise(nameof(BuildCount));
    }
    private async Task SaveCurrentAsync()
    {
        if (Editor is null) return;
        if (!Editor.IsValid) throw new BuildFormatException(L["NameError"]);
        var saved = await _builds.SaveAsync(Editor.ToDocument()); Editor.AcceptSaved(saved);
        await RefreshLibraryAsync(); Status = L.Format("SavedStatus", saved.Name);
    }
    private async Task<bool> ConfirmSwitchAsync()
    {
        if (Editor?.IsDirty != true) return true;
        var result = ThemedDialog.Show(L["UnsavedQuestion"], L["Confirm"], MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) await SaveCurrentAsync();
        return true;
    }
    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog { Filter = L["NativeFilter"], Title = L["ImportTitle"], CheckFileExists = true };
        if (dialog.ShowDialog() != true || !await ConfirmSwitchAsync()) return;
        var imported = await _builds.ImportAsNewAsync(dialog.FileName);
        await RefreshLibraryAsync(); SetEditor(imported); Navigate("Tree"); Status = L.Format("ImportedStatus", imported.Name);
    }
    private async Task ExportAsync()
    {
        if (Editor?.IsValid != true) return;
        var dialog = new SaveFileDialog { Filter = L["NativeFilter"], FileName = "PoeBuilder-build.poebuild", DefaultExt = ".poebuild", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog() != true) return;
        // Avoid exporting over internal library files: use Save for those instead.
        var full = Path.GetFullPath(dialog.FileName);
        var internalRoot = Path.GetFullPath(DataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.StartsWith(internalRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose an export destination outside the internal PoeBuilder data directory.");
        await BuildRepository.WriteDocumentAsync(full, Editor.ToDocument() with { UpdatedUtc = DateTimeOffset.UtcNow });
        Status = L["ExportedStatus"];
    }
    // ---------- build-card edit & format interop (0.7.0) ----------
    private async Task EditBuildAsync(BuildDocument? card)
    {
        if (Tree.Catalog is null) return;
        var previousId = Editor?.Id;
        var doc = card is null ? Editor?.ToDocument() : await BuildRepository.ReadDocumentAsync(_builds.PathFor(card.Id));
        if (doc is null || !await ConfirmSwitchAsync()) return;
        if (card is not null && doc.Id != previousId) SetEditor(doc);
        var window = new BuildEditWindow(this) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true)
        {
            if (previousId is Guid id && Editor?.Id != id) SetEditor(await BuildRepository.ReadDocumentAsync(_builds.PathFor(id)));
            return;
        }
        var saved = await _builds.SaveAsync(Editor!.ToDocument());
        Editor.AcceptSaved(saved);
        await RefreshLibraryAsync();
        Status = L.Format("SavedStatus", saved.Name);
    }

    private async Task ImportExchangeAsync(string? mode)
    {
        if (Tree.Catalog is null || !await ConfirmSwitchAsync()) return;
        string json; string kind; string source;
        if (mode == "file")
        {
            var dialog = new OpenFileDialog { Filter = L["ExchangeFilter"], Title = L["ImportDialogTitle"], CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            json = await File.ReadAllTextAsync(dialog.FileName); kind = "json"; source = Path.GetFileName(dialog.FileName);
        }
        else
        {
            var window = new ImportCodeWindow(this) { Owner = Application.Current.MainWindow };
            if (window.ShowDialog() != true || window.JsonPayload is null) return;
            json = window.JsonPayload; kind = window.PayloadKind; source = window.SourceLabel;
        }
        ImportedBuild imported;
        try
        {
            imported = kind == "pob"
                ? BuildInterop.ParsePobCode(json, Catalog!, Tree.Catalog)
                : BuildInterop.ParseBuildJson(json, Catalog!, Tree.Catalog);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or FormatException or KeyNotFoundException or BuildFormatException)
        { Status = L["ImportFailed"] + "\n" + e.Message; return; }
        var saved = await _builds.SaveAsync(imported.Document);
        await RefreshLibraryAsync(); SetEditor(saved); Navigate("Tree");
        ShowImportReport(imported.Report, source);
    }

    private void ShowImportReport(ImportReport report, string source)
    {
        var unknown = report.UnknownIds.Count == 0 ? "" : "\n" + L.Format("ImportUnknownList", Math.Min(12, report.UnknownIds.Count)) +
            "\n" + string.Join("\n", report.UnknownIds.Take(12)) + (report.UnknownIds.Count > 12 ? "\n…" : "");
        // Where the build came from (a link, a file name or a pasted payload), so a downloaded build is
        // never anonymous in the report.
        var origin = string.IsNullOrWhiteSpace(source) ? "" : "\n" + L.Format("ImportSource", source);
        ThemedDialog.Show(Application.Current.MainWindow,
            L.Format("ImportReport", report.PassivesMatched, report.PassivesUnknown, report.AscendancyNodesMatched, report.SkillsMatched, report.SupportsMatched, report.GemsUnknown,
                report.EquipmentMatched, report.JewelsImported, report.UniquesImported, report.EquipmentSkippedLines) + origin + unknown,
            "PoeBuilder", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task ExportBuildAsync(BuildDocument? card)
    {
        if (Tree.Catalog is null) return;
        var doc = card is null ? Editor?.ToDocument() : await BuildRepository.ReadDocumentAsync(_builds.PathFor(card.Id));
        if (doc is null) return;
        var dialog = new SaveFileDialog { Filter = L["PlannerFilter"], FileName = SafeFileName(doc.Name) + ".build", DefaultExt = ".build", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog() != true) return;
        var json = BuildInterop.ExportBuildJson(doc, Catalog!, Tree.Catalog);
        await File.WriteAllTextAsync(dialog.FileName, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Status = L.Format("ExportDone", Path.GetFileName(dialog.FileName));
    }

    internal static string SafeFileName(string name)
    {
        var clean = new string(name.Select(c => char.IsWhiteSpace(c) ? ' ' : Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "build" : clean[..Math.Min(60, clean.Length)];
    }

    public async Task<bool> PrepareCloseAsync()
    {
        if (IsBusy) return false;
        IsBusy = true;
        try { return await ConfirmSwitchAsync(); }
        catch (Exception e) { ShowError(e); return false; }
        finally { IsBusy = false; }
    }
    private async Task RunSafeAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); }
        catch (Exception e) { ShowError(e); }
        finally { IsBusy = false; }
    }
    private void ShowError(Exception exception)
    {
        Status = L["Error"];
        ThemedDialog.Show(L["ErrorText"] + "\n\n" + exception.Message, L["Error"], MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
