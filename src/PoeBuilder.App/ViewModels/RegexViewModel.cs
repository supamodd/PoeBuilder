using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PoeBuilder.App.Services;
using PoeBuilder.Core.Equipment;
using Localization = PoeBuilder.App.Services.Localization;

namespace PoeBuilder.App.ViewModels;

/// <summary>One item category the generator offers, such as Rings or Jewels. The set is not a hand-written
/// list: every entry is a class the pinned catalog really has bases for, and its pool is the union of what
/// those bases can roll — so the tab can never offer a modifier the game would not put on that category in
/// the first place.</summary>
public sealed record RegexCategory(string Id, string Name, IReadOnlyList<ItemMod> Mods);

/// <summary>One row in the tab: the modifier as the player reads it (Russian when the interface is), plus
/// the numeric bounds that narrow its first number.</summary>
public sealed class RegexModRow : Observable
{
    private static readonly Brush Inert = Brush("#7A8794"), On = Brush("#4FAE54"), Maybe = Brush("#C9A227"), No = Brush("#E05A4E");
    private readonly Action _changed;
    private RegexRequirement _requirement = RegexRequirement.None;
    private string _min = "", _max = "";
    public RegexModRow(Localization l, ItemMod mod, GameLocale? locale, Action changed)
    {
        L = l; Mod = mod; Locale = locale; _changed = changed;
    }
    public Localization L { get; }
    public ItemMod Mod { get; }
    /// <summary>The pinned modifier id, so a row can be told apart from another of the same line.</summary>
    public string Id => Mod.Id;
    public GameLocale? Locale { get; set; }
    /// <summary>The English template the generator is fed. Stored data never changes; only what the player
    /// reads is localized, exactly as in every other tab.</summary>
    public string Template => Mod.Text;
    /// <summary>Russian modifier text when the interface is Russian, the English template otherwise.</summary>
    public string Text => Locale?.Mod(Mod.Text) ?? Mod.Text;
    public string Name => Locale?.Name(Mod.Name) ?? Mod.Name;
    public string Kind => Mod.Kind;
    public int Level => Mod.Level;
    /// <summary>Only a modifier that actually carries a number can take a bound, so the boxes are only
    /// offered on the rows where they mean something.</summary>
    public bool HasNumber => System.Text.RegularExpressions.Regex.IsMatch(Mod.Text, @"\d");
    public string MinText { get => _min; set { if (Set(ref _min, value)) _changed(); } }
    public string MaxText { get => _max; set { if (Set(ref _max, value)) _changed(); } }
    /// <summary>The bound the player typed, or null when the box is empty or holds something that is not a
    /// number. A value that cannot be read narrows nothing rather than breaking the string, and
    /// <see cref="BadBound"/> says so on the row.</summary>
    public decimal? Min => Bound(_min);
    public decimal? Max => Bound(_max);
    public bool BadBound => (_min.Trim().Length > 0 && Min is null) || (_max.Trim().Length > 0 && Max is null);
    private static decimal? Bound(string text) => text.Trim().Length == 0 ? null
        : decimal.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var value) ? value
        : decimal.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) ? value
        : null;
    public RegexRequirement Requirement
    {
        get => _requirement;
        set { if (Set(ref _requirement, value)) { Raise(nameof(State)); Raise(nameof(Mark)); Raise(nameof(MarkBrush)); _changed(); } }
    }
    /// <summary>Left / Shift / Right click cycles the row between off, required, optional and excluded —
    /// the interaction the owner asked for, and the one the game filter's own "!/," grammar needs.</summary>
    public void Cycle(bool optional, bool excluded) => Requirement = excluded
        ? (_requirement == RegexRequirement.Excluded ? RegexRequirement.None : RegexRequirement.Excluded)
        : optional
            ? (_requirement == RegexRequirement.Optional ? RegexRequirement.None : RegexRequirement.Optional)
            : (_requirement == RegexRequirement.Required ? RegexRequirement.None : RegexRequirement.Required);
    public string State => _requirement switch
    {
        RegexRequirement.Required => L["RegexRequired"],
        RegexRequirement.Optional => L["RegexOptional"],
        RegexRequirement.Excluded => L["RegexExcluded"],
        _ => L["RegexOff"]
    };
    public string Mark => _requirement switch
    {
        RegexRequirement.Required => "И",
        RegexRequirement.Optional => "?",
        RegexRequirement.Excluded => "✕",
        _ => "+"
    };
    public Brush MarkBrush => _requirement switch
    {
        RegexRequirement.Required => On,
        RegexRequirement.Optional => Maybe,
        RegexRequirement.Excluded => No,
        _ => Inert
    };
    public RegexClause Clause => new(Mod.Text, Min, Max, _requirement);
    /// <summary>Re-reads the localized text after the interface language changed. The row itself did not
    /// change, only what it shows, so the tab asks for it from here rather than rebuilding its list.</summary>
    public void Relocalized(GameLocale? locale)
    {
        Locale = locale;
        Raise(nameof(Text)); Raise(nameof(Name)); Raise(nameof(State));
    }
    private static Brush Brush(string hex)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>How the picked modifiers combine, as the three radio buttons the tab offers.</summary>
public sealed record RegexLogicOption(string Id, RegexLogic Logic, string Name);

/// <summary>
/// The Regex tab: it builds the search string the game's own item filter understands, so a player can find
/// an item by its modifiers on a trader, in a stash or on a map device.
/// <para>
/// Everything the tab offers comes from the pinned catalog: a category exists because the catalog has bases
/// of that class, and a modifier is listed because those bases can roll it. The string itself is assembled
/// by <see cref="ItemFilterRegex"/>, so this class only holds the selection, the search box and the
/// character counter.
/// </para>
/// <para>
/// The tab needs no open build — it reads game data, not the player's plan — so it stays usable while
/// nothing is loaded, which is exactly when a trader is looking for a filter.
/// </para>
/// </summary>
public sealed class RegexViewModel : Observable
{
    /// <summary>Classes grouped into the handful a player actually searches, so the picker is not thirty
    /// near-identical weapon entries. The last group is a catch-all and is dropped when it has no bases.</summary>
    private static readonly (string Id, string[] Classes)[] Groups =
    [
        ("Armor", ["Body Armour", "Helmet", "Gloves", "Boots", "Shield", "Buckler"]),
        ("Weapons", ["One Hand Mace", "Two Hand Mace", "One Hand Sword", "Two Hand Sword", "One Hand Axe",
            "Two Hand Axe", "Quarterstaves", "Warstaff", "Staff", "Spear", "Bow", "Crossbow", "Dagger", "Flail",
            "Wand", "Sceptre", "Claw", "Stave", "Talisman"]),
        ("Jewel", ["Jewel"]),
        ("Ring", ["Ring"]),
        ("Amulet", ["Amulet"]),
        ("Belt", ["Belt"]),
        ("Flask", ["LifeFlask", "ManaFlask", "UtilityFlask", "Life Flask", "Mana Flask"]),
        ("Other", ["Quiver", "Foci", "Focus", "Charm"])
    ];

    public Localization L { get; }
    private GameCatalog? _catalog;
    private GameLocale? _locale;
    private readonly Dictionary<string, RegexModRow> _rows = new(StringComparer.Ordinal);
    private RegexCategory? _selectedCategory;
    private string _search = "";
    private RegexLogic _logic = RegexLogic.Mixed;
    private RegexResult _result = ItemFilterRegex.Build([], RegexLogic.Mixed);
    private string _status = "";
    private readonly ObservableCollection<RegexModRow> _visible = [];
    public RegexViewModel(Localization l)
    {
        L = l;
        Logics =
        [
            new("Mixed", RegexLogic.Mixed, l["RegexLogicMixed"]),
            new("All", RegexLogic.All, l["RegexLogicAll"]),
            new("Any", RegexLogic.Any, l["RegexLogicAny"])
        ];
        ClearCommand = new ActionCommand(_ => Clear(), () => _result.Length > 0);
        CopyCommand = new ActionCommand(_ => Copy(), () => _result.Text.Length > 0);
        l.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Localization.Language)) Relocalize(); };
    }
    /// <summary>Mutable so a language switch can re-read the three labels in place; the items are records,
    /// so the radio buttons bound to them refresh when the collection is re-raised.</summary>
    public ObservableCollection<RegexLogicOption> Logics { get; }
    public ObservableCollection<RegexCategory> Categories { get; } = [];
    public ObservableCollection<RegexModRow> Visible => _visible;
    public ICommand ClearCommand { get; }
    public ICommand CopyCommand { get; }
    public string Search { get => _search; set { if (Set(ref _search, value)) Refresh(); } }
    public RegexLogic Logic { get => _logic; set { if (Set(ref _logic, value)) Rebuild(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public RegexResult Result => _result;
    /// <summary>The finished string, ready to paste into the game's search box.</summary>
    public string Output => _result.Text;
    public int Length => _result.Length;
    public int Limit => _result.Limit;
    public bool Overflow => _result.Overflow;
    /// <summary>True when the tab has nothing to offer — no catalog, which is a load failure the shell has
    /// already reported elsewhere.</summary>
    public bool Empty => Categories.Count == 0;
    public GameLocale? Locale { get => _locale; set { if (!ReferenceEquals(_locale, value)) { _locale = value; Relocalize(); } } }

    /// <summary>The rows the player has actually picked, whatever category they came from. Kept across a
    /// category change: building one filter out of, say, a belt and a ring is normal, and dropping half of it
    /// on a click would lose work silently.</summary>
    public IEnumerable<RegexModRow> Picked => _rows.Values.Where(row => row.Requirement != RegexRequirement.None);
    public int PickedCount => Picked.Count();
    public RegexCategory? SelectedCategory
    {
        get => _selectedCategory;
        set { if (Set(ref _selectedCategory, value)) Refresh(); }
    }
    /// <summary>How many rows one category may show at once, so a pool of a thousand modifiers cannot lock
    /// the tab. The cut is reported by <see cref="Truncated"/> instead of being silent.</summary>
    public const int MaxRows = 400;

    public void SetCatalog(GameCatalog catalog)
    {
        _catalog = catalog;
        Categories.Clear();
        var byClass = catalog.Bases.Values
            .GroupBy(b => b.ItemClass, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var group in Groups)
        {
            var bases = group.Classes.SelectMany(c => byClass.TryGetValue(c, out var list) ? list : []).ToList();
            if (bases.Count == 0) continue;
            // Every modifier any base of the group can roll, at any item level. A trader searches for the
            // affix itself, not for one tier of it, so the whole pool is offered and the level stays on the row.
            var mods = bases.SelectMany(b => catalog.ModsFor(b, int.MaxValue))
                .GroupBy(m => m.Id).Select(g => g.First())
                .OrderBy(m => m.Text, StringComparer.Ordinal).ToList();
            if (mods.Count == 0) continue;
            Categories.Add(new(group.Id, L["RegexCat" + group.Id], mods));
        }
        Raise(nameof(Empty));
        SelectedCategory = Categories.FirstOrDefault();
    }

    private RegexModRow RowFor(ItemMod mod) =>
        _rows.TryGetValue(mod.Id, out var row) ? row : _rows[mod.Id] = new RegexModRow(L, mod, _locale, Rebuild);

    private void Refresh()
    {
        Visible.Clear();
        if (_selectedCategory is null) return;
        var query = _search.Trim();
        foreach (var mod in _selectedCategory.Mods)
        {
            if (query.Length > 0 && !Matches(mod, query)) continue;
            Visible.Add(RowFor(mod));
            if (Visible.Count >= MaxRows) break;
        }
        Raise(nameof(Truncated));
    }
    /// <summary>The list was cut short by the row cap, so the search box narrows it down.</summary>
    public bool Truncated => _selectedCategory is not null && _selectedCategory.Mods.Count > Visible.Count;
    public string TruncatedText => L.Format("RegExTruncated", MaxRows);

    private bool Matches(ItemMod mod, string query)
    {
        if (mod.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || mod.Text.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (_locale is null) return false;
        // Both languages are searched, as everywhere else: a Russian interface still has to find a modifier
        // by the English term a player copied out of the game client.
        var name = _locale.Name(mod.Name);
        var text = _locale.Mod(mod.Text);
        return name.Contains(query, StringComparison.OrdinalIgnoreCase) || text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void Rebuild()
    {
        _result = ItemFilterRegex.Build(Picked.Select(r => r.Clause).ToList(), _logic);
        Raise(nameof(Output)); Raise(nameof(Length)); Raise(nameof(Limit)); Raise(nameof(Overflow));
        Raise(nameof(Result)); Raise(nameof(PickedCount));
        CommandManager.InvalidateRequerySuggested();
        Status = _result.Text.Length == 0 ? ""
            : _result.Overflow ? L["RegexOverflow"]
            : _result.Length > _result.Limit - 20 ? L["RegexNearLimit"]
            : L["RegexReady"];
    }

    private void Relocalize()
    {
        foreach (var row in _rows.Values) row.Relocalized(_locale);
        // The category names and the logic labels are localized too, so both lists are rebuilt rather than
        // left showing the previous language.
        var index = _selectedCategory is null ? -1 : Categories.IndexOf(_selectedCategory);
        var id = index >= 0 ? Categories[index].Id : null;
        Categories.Clear();
        if (_catalog is not null) SetCatalog(_catalog);
        for (int i = 0; i < Logics.Count; i++) Logics[i] = Logics[i] with { Name = L["RegexLogic" + Logics[i].Id] };
        Raise(nameof(Logics));
        if (id is not null) SelectedCategory = Categories.FirstOrDefault(c => c.Id == id) ?? SelectedCategory;
        Refresh(); Rebuild();
    }

    private void Clear()
    {
        foreach (var row in _rows.Values)
        {
            row.Requirement = RegexRequirement.None;
            row.MinText = ""; row.MaxText = "";
        }
        Rebuild();
    }

    private void Copy()
    {
        if (_result.Text.Length == 0) return;
        try
        {
            Clipboard.SetText(_result.Text);
            Status = L["RegexCopied"];
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Another process can hold the clipboard open; the string stays selectable either way.
            Status = L["Error"] + ": " + e.Message;
        }
    }
}

