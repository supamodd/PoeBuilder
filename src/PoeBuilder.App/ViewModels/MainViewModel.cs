using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using System.Text;
using PoeBuilder.App.Services;
using PoeBuilder.App.Views;
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
    public void Refresh() => Raise(nameof(Label));
}
public sealed record LanguageChoice(string Code, string DisplayName);

public sealed class MainViewModel : Observable
{
    public Localization L { get; } = new();
    public TreeViewModel Tree { get; }
    public EquipmentViewModel Equipment { get; }
    public SkillsViewModel Skills { get; }
    public CharacterViewModel Character { get; }
    public string Version => "0.8.1 · Gear, Uniques & Calc v4";
    public string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoeBuilder", "Native");
    private readonly BuildRepository _builds;
    private readonly SettingsRepository _settingsRepository;
    private AppSettings _settings = new();
    private List<BuildDocument> _library = [];
    private BuildEditor? _editor;
    private string _search = "", _status = "";
    private bool _isBusy, _initialized;
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
        _builds = new(Path.Combine(DataDirectory, "Builds"));
        _settingsRepository = new(Path.Combine(DataDirectory, "settings.json"));
        Navigation = [
            new("Builds", "M3,3 L9,3 9,9 3,9 Z M13,3 L19,3 19,9 13,9 Z M3,13 L9,13 9,19 3,19 Z M13,13 L19,13 19,19 13,19 Z", L),
            new("Tree", "M11,2 L11,7 M4,18 L4,12 18,12 18,18 M11,7 L11,17 M8,2 L14,2 14,7 8,7 Z M1,18 L7,18 7,22 1,22 Z M15,18 L21,18 21,22 15,22 Z", L),
            new("Items", "M11,2 L20,6 19,15 11,22 3,15 2,6 Z M11,6 L11,17", L),
            new("Skills", "M12,1 L4,13 10,13 8,23 20,9 13,9 Z", L),
            new("Character", "M8,2 L17,2 17,7 8,7 Z M4,10 L20,10 20,14 4,14 Z M8,17 L17,17 17,22 8,22 Z", L),
            new("Notes", "M4,2 L16,2 21,7 21,22 4,22 Z M16,2 L16,7 21,7 M8,11 L17,11 M8,15 L17,15 M8,19 L14,19", L),
            new("Settings", "M3,6 L21,6 M3,17 L21,17 M8,2 L8,10 M16,13 L16,21", L)
        ];
        Character = new(L, this);
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
            if (MessageBox.Show(L.Format("DeleteQuestion", build.Name), L["Confirm"], MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
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
        try
        {
            var catalog = await Task.Run(() => GameCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "catalog.json")));
            Catalog = catalog;
            Equipment.SetCatalog(catalog); Skills.SetCatalog(catalog);
            var statMap = await Task.Run(() => GameStatMap.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "statmap.json")));
            Character.SetData(Tree.Catalog, statMap, catalog);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        { Status = L["CatalogMissing"] + "\n" + e.Message; }
        if (read.RecoveredFromError) Status = L["SettingsRecovered"];
        else if (string.IsNullOrEmpty(Status)) Status = L["Ready"];
        _initialized = true;
    });

    public void Navigate(string key) => SelectedNav = Navigation.First(x => x.Key == key);
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
        if (_editor is not null) _editor.PropertyChanged -= EditorChanged;
        _editor = new(build, isNew); _editor.PropertyChanged += EditorChanged;
        // One misbehaving module must not abort the others: bind in isolation and report.
        BindModule("Tree", () => Tree.BindEditor(_editor));
        BindModule("Items", () => Equipment.BindEditor(_editor));
        BindModule("Skills", () => Skills.BindEditor(_editor));
        BindModule("Character", () => Character.BindEditor(_editor));
        RaiseEditorProperties();
    }
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
        if (_editor is not null) _editor.PropertyChanged -= EditorChanged;
        _editor = null; Tree.BindEditor(null); Equipment.BindEditor(null); Skills.BindEditor(null); Character.BindEditor(null); RaiseEditorProperties();
    }
    private void RaiseEditorProperties()
    {
        Raise(nameof(Editor)); Raise(nameof(HasBuild)); Raise(nameof(HasNoBuild)); Raise(nameof(CurrentName)); Raise(nameof(SaveState));
        CommandManager.InvalidateRequerySuggested();
    }
    private void EditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        Raise(nameof(CurrentName)); Raise(nameof(SaveState)); CommandManager.InvalidateRequerySuggested();
        // Any plan/metadata change (tree, equipment, skills, level) refreshes the character sheet.
        Character.Recalculate();
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
        var result = MessageBox.Show(L["UnsavedQuestion"], L["Confirm"], MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
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
        string json; string kind;
        if (mode == "file")
        {
            var dialog = new OpenFileDialog { Filter = L["ExchangeFilter"], Title = L["ImportDialogTitle"], CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            json = await File.ReadAllTextAsync(dialog.FileName); kind = "json";
        }
        else
        {
            var window = new ImportCodeWindow(this) { Owner = Application.Current.MainWindow };
            if (window.ShowDialog() != true || window.JsonPayload is null) return;
            json = window.JsonPayload; kind = window.PayloadKind;
        }
        ImportedBuild imported;
        try
        {
            imported = kind == "pob"
                ? BuildInterop.ParsePobCode(json, Catalog!, Tree.Catalog)
                : BuildInterop.ParseBuildJson(json, Catalog!, Tree.Catalog);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or FormatException or KeyNotFoundException)
        { Status = L["ImportFailed"] + "\n" + e.Message; return; }
        var saved = await _builds.SaveAsync(imported.Document);
        await RefreshLibraryAsync(); SetEditor(saved); Navigate("Tree");
        ShowImportReport(imported.Report);
    }

    private void ShowImportReport(ImportReport report)
    {
        var unknown = report.UnknownIds.Count == 0 ? "" : "\n" + L.Format("ImportUnknownList", Math.Min(12, report.UnknownIds.Count)) +
            "\n" + string.Join("\n", report.UnknownIds.Take(12)) + (report.UnknownIds.Count > 12 ? "\n…" : "");
        MessageBox.Show(Application.Current.MainWindow,
            L.Format("ImportReport", report.PassivesMatched, report.PassivesUnknown, report.AscendancyNodesMatched, report.SkillsMatched, report.SupportsMatched, report.GemsUnknown,
                report.EquipmentMatched, report.JewelsImported, report.UniquesImported, report.EquipmentSkippedLines) + unknown,
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
        MessageBox.Show(L["ErrorText"] + "\n\n" + exception.Message, L["Error"], MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
