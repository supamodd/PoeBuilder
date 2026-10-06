using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using PoeBuilder.App.Services;
using PoeBuilder.Core.Filters;
using Localization = PoeBuilder.App.Services.Localization;

namespace PoeBuilder.App.ViewModels;

/// <summary>One condition row: the keyword, its value, and the two fields a range needs.</summary>
public sealed class FilterConditionRow : Observable
{
    private readonly Action _changed;
    private string _value = "";
    private bool _useRange;
    private int _min, _max;
    public FilterConditionRow(Localization l, FilterCondition condition, Action changed)
    {
        L = l; Condition = condition; _changed = changed;
        _value = condition.Value; _useRange = condition.IsRange; _min = condition.Min; _max = condition.Max;
    }
    public Localization L { get; }
    /// <summary>The keyword's name in the game's own spelling, which is what the editor edits: an unfamiliar
    /// keyword is rejected at save time, so the UI offers nothing else.</summary>
    public FilterCondition Condition { get; private set; }
    public string Keyword => Condition.Keyword;
    public bool IsFlag => FilterCondition.IsFlag(Keyword);
    public bool IsTextual => FilterCondition.IsTextual(Keyword);
    public bool IsNumeric => FilterCondition.IsNumeric(Keyword);
    /// <summary>Numeric conditions may be read as a range instead of one figure.</summary>
    public bool UseRange { get => _useRange; set { if (Set(ref _useRange, value)) _changed(); } }
    public string Value { get => _value; set { if (Set(ref _value, value)) _changed(); } }
    public string MinText { get => _min.ToString(); set { if (int.TryParse(value, out int number) && Set(ref _min, number)) _changed(); } }
    public string MaxText { get => _max.ToString(); set { if (int.TryParse(value, out int number) && Set(ref _max, number)) _changed(); } }
    /// <summary>The rarity list doubles as the value picker for Rarity, so the player cannot type one the game
    /// does not know.</summary>
    public IReadOnlyList<string> Rarities => FilterCondition.Rarities;
    /// <summary>The keywords a rule may use, offered by the editor's own list rather than typed in: an
    /// unfamiliar keyword makes the game reject the whole filter, not just one line.</summary>
    public IReadOnlyList<string> Keywords => FilterCondition.Keywords;
    public FilterCondition ToCondition()
    {
        Condition = Condition with
        {
            // A flag carries no value and a range carries no single one; leaving either behind would write a
            // line the game rejects.
            Value = IsFlag || (IsNumeric && UseRange) ? "" : Value,
            IsRange = IsNumeric && UseRange,
            Min = IsNumeric && UseRange ? _min : 0,
            Max = IsNumeric && UseRange ? _max : 0
        };
        return Condition;
    }
    /// <summary>How the condition reads in the block's own summary line.</summary>
    public string Summary => IsFlag
        ? Keyword
        : IsTextual ? $"{Keyword} \"{Value}\""
        : UseRange ? $"{Keyword} {MinText}–{MaxText}"
        : $"{Keyword} {Value}";
}

/// <summary>One Show or Hide rule, as the editor holds it while the player works on it.</summary>
public sealed class FilterBlockRow : Observable
{
    private readonly Action _changed;
    private readonly FilterStyle _original;
    private string _comment = "";
    private bool _hide;
    private bool _disabled;
    private string? _fontColor = "";
    private string? _borderColor = "";
    private int? _fontSize;
    private int? _borderWidth;
    private string? _sound = "";
    private bool _flare;
    private int? _minimapIcon;
    public FilterBlockRow(Localization l, FilterBlock block, Action changed)
    {
        L = l; _original = block.Style; _changed = changed;
        _comment = block.Comment; _hide = block.Kind == FilterBlockKind.Hide; _disabled = block.Disabled;
        _fontColor = Channel(block.Style.FontColor);
        _borderColor = Channel(block.Style.BorderColor);
        _fontSize = block.Style.FontSize; _borderWidth = block.Style.BorderWidth;
        _sound = block.Style.Sound ?? ""; _flare = block.Style.EnableFlare;
        _minimapIcon = block.Style.MinimapIcon;
        foreach (var condition in block.Conditions) Conditions.Add(new FilterConditionRow(l, condition, changed));
        // A rule with no conditions would match everything, so a new row starts with one rather than empty.
        if (Conditions.Count == 0) AddCondition();
    }
    public Localization L { get; }
    public ObservableCollection<FilterConditionRow> Conditions { get; } = [];
    public bool Hide
    {
        get => _hide;
        set
        {
            if (!Set(ref _hide, value)) return;
            Raise(nameof(KindBadge));
            // A Hide block styles nothing, so the fields are emptied with it rather than left behind as values
            // the file cannot carry.
            if (value) { _fontColor = ""; _borderColor = ""; _fontSize = null; _borderWidth = null; _sound = ""; _flare = false; _minimapIcon = null; }
            RaiseStyle();
            _changed();
        }
    }
    public bool Disabled { get => _disabled; set { if (Set(ref _disabled, value)) _changed(); } }
    public string Comment { get => _comment; set { if (Set(ref _comment, value)) _changed(); } }
    public string? FontColor { get => _fontColor; set { if (Set(ref _fontColor, value)) { RaiseStyle(); _changed(); } } }
    public string? BorderColor { get => _borderColor; set { if (Set(ref _borderColor, value)) { RaiseStyle(); _changed(); } } }
    /// <summary>The size as text, because the game's own sizes are a closed list and an empty box means "keep
    /// the game's default".</summary>
    public string FontSize
    {
        get => _fontSize?.ToString() ?? "";
        set { if (int.TryParse(value, out int size) ? Set(ref _fontSize, size) : Set(ref _fontSize, null)) { RaiseStyle(); _changed(); } }
    }
    public string BorderWidth
    {
        get => _borderWidth?.ToString() ?? "";
        set { if (int.TryParse(value, out int width) && width > 0 ? Set(ref _borderWidth, width) : Set(ref _borderWidth, null)) { RaiseStyle(); _changed(); } }
    }
    public string? Sound { get => _sound; set { if (Set(ref _sound, value)) { RaiseStyle(); _changed(); } } }
    public bool Flare { get => _flare; set { if (Set(ref _flare, value)) { RaiseStyle(); _changed(); } } }
    public string MinimapIcon
    {
        get => _minimapIcon?.ToString() ?? "";
        set { if (int.TryParse(value, out int icon) && FilterStyle.IsMinimapIcon(icon) ? Set(ref _minimapIcon, icon) : Set(ref _minimapIcon, null)) { RaiseStyle(); _changed(); } }
    }
    /// <summary>The style fields are only offered on a Show block: a Hide block carries none, and offering
    /// them there would only produce a file the game rejects.</summary>
    public bool CanStyle => !Hide;
    /// <summary>The rule's kind as the block's badge reads it, in the game's own spelling — the file carries
    /// "Show"/"Hide", so the tab shows the same word rather than a translation of it.</summary>
    public string KindBadge => Hide ? L["FilterHideBadge"] : L["FilterShowBadge"];
    public IReadOnlyList<string> Sounds => FilterStyle.Sounds;
    public IReadOnlyList<string> Icons => FilterStyle.MinimapIcons;
    public IReadOnlyList<string> FontSizes { get; } = [.. FilterStyle.FontSizes.Select(s => s.ToString())];
    public static IReadOnlyList<string> Keywords => FilterCondition.Keywords;

    public void AddCondition() => Conditions.Add(new FilterConditionRow(L, new FilterCondition(), _changed));
    public void RemoveCondition(FilterConditionRow row) { Conditions.Remove(row); _changed(); }
    public void ChangeKeyword(FilterConditionRow row, string keyword)
    {
        // Switching the keyword resets the value, because a rarity name is not a number and a number is not an
        // item class; carrying the old value across would produce a rule that reads as nonsense.
        var index = Conditions.IndexOf(row);
        if (index < 0) return;
        Conditions[index] = new FilterConditionRow(L, new FilterCondition { Keyword = keyword }, _changed);
        Raise(nameof(Conditions));
        _changed();
    }
    private void RaiseStyle()
    {
        Raise(nameof(FontColor)); Raise(nameof(BorderColor)); Raise(nameof(FontSize)); Raise(nameof(BorderWidth));
        Raise(nameof(Sound)); Raise(nameof(Flare)); Raise(nameof(MinimapIcon)); Raise(nameof(CanStyle));
    }

    /// <summary>The row as the core's block. A colour is written as four numbers; anything unreadable is
    /// dropped rather than guessed, and the core's validation is what refuses the save.</summary>
    public FilterBlock ToBlock()
    {
        var style = Hide ? new FilterStyle()
            : _original with
            {
                FontColor = Channel(_fontColor),
                BorderColor = Channel(_borderColor),
                FontSize = _fontSize,
                BorderWidth = _borderWidth,
                Sound = string.IsNullOrWhiteSpace(_sound) ? null : _sound.Trim(),
                EnableFlare = _flare,
                MinimapIcon = _minimapIcon,
                MinimapIconName = _minimapIcon is { } icon ? FilterStyle.MinimapIcons[icon] : null
            };
        return new FilterBlock
        {
            Kind = Hide ? FilterBlockKind.Hide : FilterBlockKind.Show,
            Comment = _comment.Trim(),
            Disabled = _disabled,
            Conditions = [.. Conditions.Select(c => c.ToCondition())],
            Style = style
        };
    }
    private static int[]? Channel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var values = new List<int>();
        foreach (var part in text.Split([' ', ',', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out int value)) values.Add(value);
        return values.Count == 4 ? [.. values] : null;
    }
    private static string? Channel(int[]? color) => color is null ? null : string.Join(' ', color);
}

/// <summary>One saved filter in the library, as the list shows it.</summary>
public sealed record FilterCardVm(Guid Id, string Name, string Summary, int Blocks, DateTimeOffset Updated);

/// <summary>
/// The Filter tab: a loot-filter editor for Path of Exile 2.
/// <para>
/// It keeps its own library of filters, beside the builds and independent of them — a filter belongs to no
/// character, and the tab works with nothing open. A saved filter is written twice: as a native document the
/// tab can reopen, and as the plain <c>.filter</c> file the game reads.
/// </para>
/// <para>
/// Every change goes through <see cref="FilterValidation"/> before it is saved or written, so what reaches
/// the game's folder is a file the client can load. The preview is the same writer the export uses, so what
/// the player reads is what the game gets.
/// </para>
/// </summary>
public sealed class FilterViewModel : Observable
{
    public Localization L { get; }
    private readonly LootFilterRepository _repository;
    private string _name = "";
    private int _dropLevel;
    private bool _minimap = true;
    private string _status = "";
    private string _error = "";
    private string _preview = "";
    private string _libraryStatus = "";
    private bool _dirty;
    private Guid _id = Guid.NewGuid();
    private DateTimeOffset _created = DateTimeOffset.UtcNow;
    private readonly ObservableCollection<FilterCardVm> _cards = [];
    private readonly ObservableCollection<FilterBlockRow> _blocks = [];
    public FilterViewModel(Localization l, string rootDirectory)
    {
        L = l;
        _repository = new LootFilterRepository(rootDirectory);
        NewCommand = new ActionCommand(_ => New());
        // The three commands that write a file are async, so they go through AsyncCommand: blocking the UI
        // thread on them would deadlock the dispatcher the continuation needs to come back on.
        DuplicateCommand = new AsyncCommand(_ => DuplicateAsync(), () => Blocks.Count > 0);
        DeleteCommand = new AsyncCommand(_ => DeleteAsync(), () => Blocks.Count > 0);
        SaveCommand = new AsyncCommand(_ => SaveAsync());
        AddShowCommand = new ActionCommand(_ => AddBlock(FilterBlockKind.Show), () => Blocks.Count < FilterValidation.MaximumBlocks);
        AddHideCommand = new ActionCommand(_ => AddBlock(FilterBlockKind.Hide), () => Blocks.Count < FilterValidation.MaximumBlocks);
        CopyCommand = new ActionCommand(_ => Copy());
        ExportCommand = new ActionCommand(_ => Export());
        ImportCommand = new ActionCommand(_ => Import());
        LoadCommand = new AsyncCommand(_ => LoadLibraryAsync());
        // The library card carries its own row, so opening one is a click on the card rather than a selection
        // the player has to make first.
        OpenCardCommand = new AsyncCommand(arg =>
        {
            if (arg is FilterCardVm card) return OpenAsync(card.Id);
            return Task.CompletedTask;
        });
        ClearCommand = new ActionCommand(_ => Clear(), () => Blocks.Count > 0);
    }
    public IReadOnlyList<FilterPresets.Preset> Presets => FilterPresets.All;
    public ObservableCollection<FilterCardVm> Cards => _cards;
    public ObservableCollection<FilterBlockRow> Blocks => _blocks;
    public ICommand NewCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand AddShowCommand { get; }
    public ICommand AddHideCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand OpenCardCommand { get; }
    public ICommand ClearCommand { get; }

    /// <summary>Where the game keeps its filters: the path the game reports in its own settings.</summary>
    public string GameDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Path of Exile 2");
    public string Name { get => _name; set { if (Set(ref _name, value)) Touch(); } }
    public string DropLevel { get => _dropLevel.ToString(); set { if (int.TryParse(value, out int level) && Set(ref _dropLevel, level)) Touch(); } }
    public bool Minimap { get => _minimap; set { if (Set(ref _minimap, value)) Touch(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string LibraryStatus { get => _libraryStatus; private set => Set(ref _libraryStatus, value); }
    /// <summary>The file as the game will read it, produced by the same writer the export uses.</summary>
    public string Preview => _preview;
    public bool Dirty { get => _dirty; private set => Set(ref _dirty, value); }
    public bool Empty => Blocks.Count == 0;
    public string Id => _id.ToString("N");

    /// <summary>The document the rows describe right now. A new document every call, so nothing the caller
    /// gets can alias the rows.</summary>
    public LootFilterDocument ToDocument() => new()
    {
        Id = _id, Name = _name, DropLevel = _dropLevel, Minimap = _minimap,
        CreatedUtc = _created, UpdatedUtc = DateTimeOffset.UtcNow,
        Blocks = [.. Blocks.Select(b => b.ToBlock())]
    };

    public async Task LoadLibraryAsync()
    {
        try
        {
            var (filters, unreadable) = await _repository.ReadLibraryAsync();
            Cards.Clear();
            foreach (var filter in filters) Cards.Add(new FilterCardVm(filter.Id, filter.Name, Describe(filter), filter.Blocks.Count, filter.UpdatedUtc));
            LibraryStatus = unreadable.Count > 0
                ? L.Format("FilterLibraryUnreadable", unreadable.Count)
                : filters.Count > 0 ? L.Format("FilterLibraryCount", filters.Count) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FilterFormatException)
        {
            LibraryStatus = L["FilterLibraryFailed"] + ": " + e.Message;
        }
    }

    /// <summary>How many rules the filter shows and how many it hides, as the library card says it.</summary>
    private static string Describe(LootFilterDocument filter)
    {
        int show = filter.Blocks.Count(b => b.Kind == FilterBlockKind.Show);
        return $"{show} / {filter.Blocks.Count - show}";
    }

    /// <summary>Starts a filter from one of the presets. A preset is a working rule set, not an empty page:
    /// a filter that catches nothing is impossible to judge, and one that catches everything is worse.</summary>
    public void ApplyPreset(FilterPresets.Preset preset)
    {
        var document = preset.Build();
        _id = document.Id; _created = document.CreatedUtc; _name = document.Name;
        _dropLevel = document.DropLevel; _minimap = document.Minimap;
        Fill(document.Blocks);
        Status = L.Format("FilterPresetLoaded", preset.Name);
        Touch();
    }

    /// <summary>Swaps a condition row for a fresh one on another keyword. The row is replaced rather than
    /// re-keyed because its value belongs to its keyword: a rarity name is not a number, and carrying one
    /// across would produce a rule that reads as nonsense and a file the game refuses.</summary>
    public void Rebind(FilterConditionRow row, string keyword)
    {
        var block = Blocks.FirstOrDefault(b => b.Conditions.Contains(row));
        if (block is null || !FilterCondition.IsKnownKeyword(keyword)) return;
        block.ChangeKeyword(row, keyword);
    }

    /// <summary>Replaces the working copy with a saved filter from the library. Async for the same reason the
    /// save command is: it reads a file, and the read must not run on the UI thread.</summary>
    public Task OpenAsync(Guid id)
    {
        if (Cards.All(c => c.Id != id)) { Status = L["FilterMissing"]; return Task.CompletedTask; }
        return LoadAsync(id);
    }

    private void New()
    {
        _id = Guid.NewGuid(); _created = DateTimeOffset.UtcNow;
        _name = L["FilterNewName"]; _dropLevel = 0; _minimap = true;
        Blocks.Clear();
        Rebuild();
        Status = L["FilterNewStatus"];
        Touch();
    }
    private void Fill(IEnumerable<FilterBlock> blocks)
    {
        Blocks.Clear();
        foreach (var block in blocks) Blocks.Add(new FilterBlockRow(L, block, Touch));
        Rebuild();
    }

    private void AddBlock(FilterBlockKind kind)
    {
        Blocks.Add(new FilterBlockRow(L, new FilterBlock { Kind = kind }, Touch));
        Raise(nameof(Empty));
        Touch();
    }

    public void RemoveBlock(FilterBlockRow row)
    {
        if (!Blocks.Remove(row)) return;
        Raise(nameof(Empty));
        Touch();
    }

    /// <summary>Moves a rule one place. Order is the player's order: the client takes the first block that
    /// matches an item, so a rule moved up changes what the rules below it ever see.</summary>
    public void Move(FilterBlockRow row, int delta)
    {
        int index = Blocks.IndexOf(row);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Blocks.Count) return;
        Blocks.Move(index, target);
        Touch();
    }

    /// <summary>Anything the player edits marks the filter unsaved and rewrites the preview, so the file on
    /// screen is never a stale one.</summary>
    /// <summary>Anything the player edits marks the filter unsaved and rewrites the preview, so the file on
    /// screen is never a stale one.</summary>
    private void Touch()
    {
        Set(ref _dirty, true);
        Raise(nameof(Name)); Raise(nameof(DropLevel)); Raise(nameof(Minimap)); Raise(nameof(Dirty));
        Rebuild();
    }

    /// <summary>Rewrites the preview and reports whatever the validator objects to. The problem is shown, not
    /// thrown: the player is mid-edit, and a message beside the rule is more useful than a disabled field.</summary>
    public void Rebuild()
    {
        Raise(nameof(Empty));
        try
        {
            _preview = FilterWriter.Write(ToDocument());
            Error = "";
        }
        catch (FilterFormatException e)
        {
            _preview = "";
            Error = L["FilterInvalid"] + ": " + e.Message;
        }
        Raise(nameof(Preview));
    }

    /// <summary>Writes the filter into the library. A filter the game would refuse is not saved at all — the
    /// message says why, and the rules stay on screen to be fixed.</summary>
    public async Task SaveAsync()
    {
        try
        {
            var saved = await _repository.SaveAsync(ToDocument());
            _id = saved.Id; _created = saved.CreatedUtc; _name = saved.Name;
            Set(ref _dirty, false);
            Raise(nameof(Dirty)); Raise(nameof(Name)); Raise(nameof(Id));
            Status = L.Format("FilterSaved", saved.Name);
            await LoadLibraryAsync();
        }
        catch (Exception e) when (e is FilterFormatException or IOException or UnauthorizedAccessException)
        {
            Status = L["FilterSaveFailed"] + ": " + e.Message;
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task LoadAsync(Guid id)
    {
        try
        {
            var filter = await LootFilterRepository.ReadAsync(_repository.PathFor(id));
            _id = filter.Id; _created = filter.CreatedUtc; _name = filter.Name;
            _dropLevel = filter.DropLevel; _minimap = filter.Minimap;
            Fill(filter.Blocks);
            Set(ref _dirty, false);
            Raise(nameof(Name)); Raise(nameof(DropLevel)); Raise(nameof(Minimap)); Raise(nameof(Dirty)); Raise(nameof(Id));
            Status = L.Format("FilterOpened", filter.Name);
        }
        catch (Exception e) when (e is FilterFormatException or IOException or UnauthorizedAccessException)
        {
            Status = L["FilterLoadFailed"] + ": " + e.Message;
        }
    }

    private async Task DuplicateAsync()
    {
        var copy = ToDocument() with { Id = Guid.NewGuid(), Name = _name + " " + L["FilterCopySuffix"], CreatedUtc = DateTimeOffset.UtcNow };
        try
        {
            var saved = await _repository.SaveAsync(copy);
            Status = L.Format("FilterDuplicated", saved.Name);
            await LoadLibraryAsync();
        }
        catch (Exception e) when (e is FilterFormatException or IOException or UnauthorizedAccessException)
        {
            Status = L["FilterSaveFailed"] + ": " + e.Message;
        }
    }

    private async Task DeleteAsync()
    {
        try
        {
            await _repository.MoveToTrashAsync(_id);
            Status = L["FilterDeleted"];
            New();
            await LoadLibraryAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = L["FilterDeleteFailed"] + ": " + e.Message;
        }
    }

    private void Clear()
    {
        Blocks.Clear();
        Raise(nameof(Empty));
        Touch();
        Status = L["FilterCleared"];
    }

    private void Copy()
    {
        if (_preview.Length == 0) return;
        try
        {
            Clipboard.SetText(_preview);
            Status = L["FilterCopied"];
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Another process can hold the clipboard open; the preview stays selectable either way.
            Status = L["Error"] + ": " + e.Message;
        }
    }

    /// <summary>Writes the file into the game's own folder — where the client looks for filters, and the
    /// folder the game itself reports in its settings. The editor never guesses another install.</summary>
    public void Export()
    {
        if (_preview.Length == 0) { Status = L["FilterExportNothing"]; return; }
        try
        {
            var fileName = SanitizeFileName(_name) + ".filter";
            LootFilterRepository.ExportTo(GameDirectory, fileName, _preview);
            Status = L.Format("FilterExported", Path.Combine(GameDirectory, fileName));
        }
        catch (Exception e) when (e is FilterFormatException or IOException or UnauthorizedAccessException)
        {
            Status = L["FilterExportFailed"] + ": " + e.Message;
        }
    }

    /// <summary>The game reads a file name, not a display name: the slashes, colons and dots a title may carry
    /// would make an unusable path or a hidden file, so they are dropped rather than escaped.</summary>
    private static string SanitizeFileName(string name)
    {
        var cleaned = new string([.. name.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != '.')]).Trim();
        return cleaned.Length == 0 ? "filter" : cleaned;
    }

    /// <summary>Opens a hand-written or downloaded filter file. Rules the reader could not place are reported
    /// rather than silently dropped — a rule that vanishes on import is worse than one shown as unsupported.</summary>
    public void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L["FilterImportTitle"],
            Filter = L["FilterImportFilter"],
            DefaultExt = ".filter"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var read = FilterReader.Read(dialog.FileName);
            _id = Guid.NewGuid(); _created = DateTimeOffset.UtcNow;
            _name = read.Name.Length > 0 ? read.Name : Path.GetFileNameWithoutExtension(dialog.FileName);
            _dropLevel = read.DropLevel; _minimap = read.Minimap;
            Fill(read.Blocks);
            Status = read.Unknown.Count > 0
                ? L.Format("FilterImportedUnknown", read.Unknown.Count)
                : L.Format("FilterImported", read.Blocks.Count);
            Touch();
        }
        catch (Exception e) when (e is FilterFormatException or IOException or UnauthorizedAccessException)
        {
            Status = L["FilterImportFailed"] + ": " + e.Message;
        }
    }
}
