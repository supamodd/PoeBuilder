using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PoeBuilder.App.Services;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Models;
using Localization = PoeBuilder.App.Services.Localization;

namespace PoeBuilder.App.ViewModels;

public sealed record NamedOption(string Id, string Name) { public override string ToString() => Name; }
public sealed record PreviewLine(string Text, Brush Brush, double Size, bool Bold);
public sealed record ItemChoice(string Name, string ClassName, ItemBase? Base, UniqueItem? Unique, ImageSource? Icon)
{
    public bool IsUnique => Unique is not null;
}

public sealed class ModDraft : Observable
{
    public ItemMod Definition { get; }
    private string _values;
    private readonly Action _changed;
    public ICommand RemoveCommand { get; }
    public bool Corrupted { get; }
    public string Text => Definition.Text;
    public string Details => string.Join("; ", Definition.Stats.Select(s => $"{s.Id}: {s.Min}..{s.Max}"));
    public string ValuesText { get => _values; set { if (Set(ref _values, value)) _changed(); } }
    public ModDraft(ItemMod definition, decimal[] values, Action changed, Action<ModDraft> remove, bool corrupted = false)
    {
        Definition = definition; _values = string.Join("; ", values.Select(v => v.ToString(CultureInfo.InvariantCulture))); _changed = changed; Corrupted = corrupted;
        RemoveCommand = new ActionCommand(_ => remove(this));
    }
    public ModRoll ToRoll()
    {
        var parts = ValuesText.Split(';', StringSplitOptions.TrimEntries);
        var values = new decimal[parts.Length];
        for (int i = 0; i < parts.Length; i++) if (!decimal.TryParse(parts[i].Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out values[i])) throw new PlanningException("PlanModValues");
        return new() { Id = Definition.Id, Values = values };
    }
}
/// <summary>Augment row with its own remove command; rendering keeps the game-tooltip gold.</summary>
public sealed class AugmentDraft : Observable
{
    public string Id { get; }
    public string Name { get; }
    public string Text { get; }
    public ICommand RemoveCommand { get; }
    public AugmentDraft(string id, string name, string text, Action<AugmentDraft> remove)
    { Id = id; Name = name; Text = text; RemoveCommand = new ActionCommand(_ => remove(this)); }
}
/// <summary>One editable line of a unique item's text. <see cref="Template"/> is PoB2's own line, which
/// may still carry the game's range notation ("+(30-40) to maximum Life"); <see cref="Text"/> is the
/// concrete roll shown and edited, so a player can type what their item actually rolled. A line that only
/// applies to some variants (or carries a tag) says so in <see cref="Marker"/>.</summary>
public sealed class UniqueLineDraft : Observable
{
    private string _text;
    private readonly Action _changed;
    public UniqueLineDraft(UniqueTextLine line, string marker, Action changed, Action<UniqueLineDraft>? remove = null)
    {
        Kind = line.Kind; Template = line.Text; Marker = marker; _text = line.Resolved; _changed = changed;
        RemoveCommand = new ActionCommand(_ => remove?.Invoke(this));
    }
    public UniqueLineKind Kind { get; }
    public string Template { get; }
    public string Marker { get; }
    public bool IsImplicit => Kind == UniqueLineKind.Implicit;
    public bool HasMarker => Marker.Length > 0;
    public ICommand RemoveCommand { get; }
    public string Text { get => _text; set { if (Set(ref _text, value)) _changed(); } }
}
/// <summary>
/// One of a unique's own modifier lines, offered as something the item can carry. PoB2's data lists every
/// version of a unique's modifier set (Morior Invictus has 29 of them, one per rolled mod — "Spirit",
/// "Life", "All Resistances"…), and only the live variant's lines apply by default. The option list is
/// what makes the other lines reachable: "Morior Invictus with +10 to Spirit per Socket filled" is variant
/// 3's line, and without this list the only way to it was to switch the variant and lose the rest.
/// </summary>
public sealed record UniqueLineOption(string Text, string Resolved, string Variant, int[] Variants, string[] Tags)
{
    public string DisplayName => Variant.Length == 0 ? Resolved : Resolved + " · " + Variant;
    public override string ToString() => DisplayName;
}

public sealed class ItemDraftViewModel : Observable
{
    private static readonly Brush NameNormal = Brush("#C8C8C8"), NameMagic = Brush("#8888FF"), NameRare = Brush("#FFFF77"), NameUnique = Brush("#D9A441");
    private static readonly Brush Line = Brush("#C8C8C8"), Mod = Brush("#8888FF"), Dim = Brush("#7A8794"), Gold = Brush("#A38D6D"), Corrupt = Brush("#C86B5A");
    public Localization L { get; }
    private readonly GameCatalog _catalog;
    private readonly Action<GearItem> _commit;
    private readonly Guid _id;
    private readonly string? _slot;
    private bool _loading = true;
    private ItemBase? _selectedBase;
    private ItemMod? _selectedMod;
    private Augment? _selectedAugment;
    private NamedOption? _rarity;
    private bool _corrupted;
    private ItemMod? _selectedCorrupted;
    private string _name = "", _level = "80", _quality = "0", _capacity = "0", _notes = "", _baseSearch = "", _modSearch = "", _augmentSearch = "", _corruptSearch = "", _error = "";
    public bool IsDirty { get; private set; }
    public bool Accepted { get; private set; }
    public event Action? Saved;
    public string Error { get => _error; private set => Set(ref _error, value); }
    /// <summary>A unique draft has no craftable affixes in the game. The pinned catalog carries no
    /// unique modifiers, so the list stays locked empty instead of pretending they exist.</summary>
    public bool IsUniqueDraft => (Rarity?.Id ?? "rare") == "unique";
    public string Title => _slot is null ? L["ItemEditor"] : L["ItemEditor"] + " · " + L["Slot" + _slot];
    public ObservableCollection<ModDraft> Mods { get; } = [];
    public ObservableCollection<ModDraft> CorruptedList { get; } = [];
    public ObservableCollection<AugmentDraft> Augments { get; } = [];
    public IReadOnlyList<NamedOption> Rarities { get; }
    public string Name { get => _name; set { if (Set(ref _name, value)) Touch(); } }
    public string ItemLevel { get => _level; set { if (Set(ref _level, value)) { Touch(); Raise(nameof(AvailableMods)); } } }
    public string Quality { get => _quality; set { if (Set(ref _quality, value)) Touch(); } }
    public string Capacity { get => _capacity; set { if (Set(ref _capacity, value)) Touch(); } }
    public string Notes { get => _notes; set { if (Set(ref _notes, value)) Touch(); } }
    public NamedOption? Rarity { get => _rarity; set { if (value is not null && Set(ref _rarity, value)) { if (value.Id == "unique") { Mods.Clear(); CorruptedList.Clear(); Augments.Clear(); } Touch(); Raise(nameof(AvailableMods)); } } }
    public bool Corrupted { get => _corrupted; set { if (Set(ref _corrupted, value)) Touch(); } }
    public ItemMod? SelectedCorrupted { get => _selectedCorrupted; set => Set(ref _selectedCorrupted, value); }
    public string CorruptSearch { get => _corruptSearch; set { if (Set(ref _corruptSearch, value)) Raise(nameof(AvailableCorrupted)); } }
    /// <summary>The game applies at most one corrupted implicit; the list closes once one is socketed.</summary>
    public IEnumerable<ItemMod> AvailableCorrupted => SelectedBase is null || CorruptedList.Count >= 1 ? [] :
        _catalog.CorruptedFor(SelectedBase).Where(m => CorruptSearch.Length == 0 || m.DisplayName.Contains(CorruptSearch, StringComparison.OrdinalIgnoreCase)).OrderBy(m => m.Text);
    public string BaseSearch { get => _baseSearch; set { if (Set(ref _baseSearch, value)) Raise(nameof(Bases)); } }
    public string ModSearch { get => _modSearch; set { if (Set(ref _modSearch, value)) Raise(nameof(AvailableMods)); } }
    public string AugmentSearch { get => _augmentSearch; set { if (Set(ref _augmentSearch, value)) Raise(nameof(AvailableAugments)); } }
    public IEnumerable<ItemChoice> Bases => _catalog.Bases.Values
        .Where(b => (_slot is null || EquipmentRules.Fits(_slot, b)) && (BaseSearch.Length == 0 || (b.Name + " " + b.ClassName).Contains(BaseSearch, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(b => b.Name).Take(200)
        .Select(b => new ItemChoice(b.Name, b.ClassName, b, null, IconService.Instance.ForBase(b)))
        .Concat(_catalog.Uniques.Values
            .Where(u => (_slot is null || EquipmentRules.FitsUnique(_slot, u)) && (BaseSearch.Length == 0 || (u.Name + " " + u.ItemClass).Contains(BaseSearch, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(u => u.Name).Select(u => new ItemChoice(u.Name, u.ItemClass, null, u, IconService.Instance.ForUnique(u))));
    private ItemChoice? _selectedChoice;
    public ItemChoice? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (value is null || value == _selectedChoice) return;
            _selectedChoice = value;
            if (value.Unique is not null)
            {
                SelectedUnique = value.Unique.Name;
                Rarity = Rarities.First(r => r.Id == "unique");
                Name = value.Unique.Name;
                _selectedBase = null;
                Raise(nameof(SelectedBase)); Raise(nameof(ItemIcon));
            }
            else if (value.Base is not null) SelectedBase = value.Base;
            Raise(nameof(SelectedChoice));
        }
    }
    public ItemBase? SelectedBase
    {
        get => _selectedBase;
        set
        {
            if (value is null || value.Id == _selectedBase?.Id) return;
            if (!_loading && (Mods.Count > 0 || Augments.Count > 0) && MessageBox.Show(L["BaseChangeQuestion"], L["Confirm"], MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            { _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => Raise(nameof(SelectedBase)))); return; }
            if (_name.Length == 0 || _name == _selectedBase?.Name) Name = value.Name;
            _selectedBase = value; Mods.Clear(); CorruptedList.Clear(); Augments.Clear(); Corrupted = false; Touch();
            foreach (var p in new[] { nameof(SelectedBase), nameof(AvailableMods), nameof(AvailableAugments) }) Raise(p);
        }
    }
    /// <summary>The item's icon: its base's art, or — for a unique — the unique's own art by name, with
    /// the base's art as the honest fallback.</summary>
    public ImageSource? ItemIcon => _selectedBase is not null ? IconService.Instance.ForBase(_selectedBase)
        : _selectedUniqueName is { Length: > 0 } name ? IconService.Instance.ForUnique(_catalog, name) : null;
    /// <summary>Every affix the base can still take at its item level: the catalog's class pool plus the
    /// spawn-tag pool of the pinned affix table, minus the groups already present, capped per kind by the
    /// base's own rule (a jewel takes 2 + 2, every other rare 3 + 3). No cap on the list length — the
    /// pinned table states ~1000 affixes for an armour piece and hiding all but 200 of them hid real mods.</summary>
    public IEnumerable<ItemMod> AvailableMods
    {
        get
        {
            if (IsUniqueDraft || SelectedBase is null || !int.TryParse(ItemLevel, out int level)) return [];
            var (prefixCap, suffixCap) = Caps;
            var groups = Mods.SelectMany(m => m.Definition.Groups).ToHashSet();
            int prefixes = Mods.Count(m => m.Definition.Kind == "prefix"), suffixes = Mods.Count(m => m.Definition.Kind == "suffix");
            return _catalog.ModsFor(SelectedBase, level)
                .Where(m => !m.Groups.Any(groups.Contains)
                    && (m.Kind == "prefix" ? prefixes < prefixCap : m.Kind == "suffix" && suffixes < suffixCap)
                    && (ModSearch.Length == 0 || m.DisplayName.Contains(ModSearch, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(m => m.Kind).ThenBy(m => m.Level).ThenBy(m => m.Text);
        }
    }
    /// <summary>Affix room of this draft: normal takes none, magic one of each kind, rare what the base's
    /// own class rule allows. <see cref="MaxMods"/> is the total, which is what the "add" button checks.</summary>
    private (int Prefixes, int Suffixes) Caps
    {
        get
        {
            if (Rarity?.Id == "normal") return (0, 0);
            if (Rarity?.Id == "magic") return (1, 1);
            return SelectedBase is null ? (3, 3) : EquipmentRules.AffixCaps(SelectedBase);
        }
    }
    public int MaxMods { get { var (prefixes, suffixes) = Caps; return prefixes + suffixes; } }
    public IEnumerable<Augment> AvailableAugments => IsUniqueDraft || SelectedBase is null ? [] : _catalog.Augments.Values.Where(a => (a.Limit.Length == 0 || a.Limit == "1") && _catalog.AugmentEffect(SelectedBase, a).Length > 0 && (AugmentSearch.Length == 0 || (a.Name + " " + a.Kind + " " + _catalog.AugmentEffect(SelectedBase, a)).Contains(AugmentSearch, StringComparison.OrdinalIgnoreCase))).OrderBy(a => a.Name);

    // --- Unique picker: expose every pinned unique identity, including jewels. Picking one
    // switches the draft to the unique rarity. Their modifiers are not pinned — text by hand. ---
    private string _uniqueFilter = "";
    private string? _selectedUniqueName;
    public string UniqueFilter { get => _uniqueFilter; set { if (Set(ref _uniqueFilter, value)) Raise(nameof(UniqueNames)); } }
    public IEnumerable<string> UniqueNames => _catalog.Uniques.Values
        .Where(u => (_slot is null || AllowedUniqueClasses.Contains(u.ItemClass)) && (_uniqueFilter.Length == 0 || u.Name.Contains(_uniqueFilter, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(u => u.Name).Select(u => u.Name);
    /// <summary>Item classes the current slot accepts (so a boot slot never offers Headhunter).</summary>
    private HashSet<string> AllowedUniqueClasses => _slot is null ? new HashSet<string>()
        : _catalog.Bases.Values.Where(b => EquipmentRules.Fits(_slot, b)).Select(b => b.ItemClass).ToHashSet();
    public string? SelectedUnique
    {
        get => _selectedUniqueName;
        set
        {
            if (!Set(ref _selectedUniqueName, value) || value is null) return;
            // A unique is its own item: whatever base was picked before must not stay attached, or the
            // plan would carry a unique built on an unrelated base (the pinned data names its own base
            // type, and the icon chain follows it).
            _selectedBase = null;
            Raise(nameof(SelectedBase)); Raise(nameof(ItemIcon));
            Rarity = Rarities.First(r => r.Id == "unique");
            Name = value;
            Mods.Clear(); CorruptedList.Clear();
            // PoB2's own data carries each unique's modifier lines and its variants (the pinned RePoE
            // export has identities only), so a unique chosen here arrives with real modifiers — every
            // range at its maximum roll, the same convention the pinned implicits use — instead of an
            // empty box the player has to paste text into.
            LoadUnique(value);
        }
    }

    // --- Unique variants and lines ---------------------------------------------------------------
    // A unique can have several variants (Morior Invictus: 29) and only one of them is live in the game:
    // PoB2 records it as "Selected Variant". The lines below are that variant's modifiers, editable, and
    // they are what the notes (the item's text) are built from.
    private UniqueData? _uniqueData;
    private NamedOption? _uniqueVariantChoice;
    private bool _showAllVariants;
    private int _uniqueVariant = 1;
    public ObservableCollection<UniqueLineDraft> UniqueLines { get; } = [];
    public ObservableCollection<NamedOption> UniqueVariantChoices { get; } = [];
    /// <summary>The unique's own modifier lines that the item does not carry yet — its "personal mods". PoB2
    /// lists every version of them (Morior Invictus has one per rolled modifier: "Spirit", "Life", "All
    /// Resistances"…), and only the live variant's lines apply by default. This list is what makes the rest
    /// reachable, so "+10 to Spirit per Socket filled" can be added to the item without hunting through the
    /// variant combo — exactly what the item editor's empty "available mods" list lacked for a unique.</summary>
    public ObservableCollection<UniqueLineOption> UniqueLineOptions { get; } = [];
    public bool HasUniqueLineOptions => UniqueLineOptions.Count > 0;
    private UniqueLineOption? _selectedUniqueLineOption;
    public UniqueLineOption? SelectedUniqueLineOption { get => _selectedUniqueLineOption; set => Set(ref _selectedUniqueLineOption, value); }
    public bool HasUniqueData => _uniqueData is not null;
    public bool HasUniqueLines => UniqueLines.Count > 0;
    public bool HasUniqueVariants => UniqueVariantChoices.Count > 1;
    /// <summary>Shows the lines of every variant as well, marked with the variant they belong to, so
    /// nothing of the data looks lost. Off by default: only the live variant applies to the item.</summary>
    public bool ShowAllVariants
    {
        get => _showAllVariants;
        set { if (Set(ref _showAllVariants, value)) LoadUniqueLines(); }
    }
    public NamedOption? SelectedUniqueVariant
    {
        get => _uniqueVariantChoice;
        set
        {
            if (value is null || _loading || value == _uniqueVariantChoice || !int.TryParse(value.Id, out int variant)) return;
            _uniqueVariantChoice = value; _uniqueVariant = variant;
            LoadUniqueLines();
            Raise(nameof(SelectedUniqueVariant)); Raise(nameof(UniqueVariantLabel));
        }
    }
    public string UniqueVariantLabel => _uniqueData is null ? "" : L.Format("UniqueVariantShown", UniqueItemText.VariantName(_uniqueData, _uniqueVariant));
    /// <summary>The base type of a unique: PoB2's own field, else the base the item was printed on, else
    /// the base-type line of its own text.</summary>
    public string BaseTypeText => _uniqueData?.BaseType is { Length: > 0 } fromData ? fromData
        : _selectedBase?.Name is { Length: > 0 } fromBase ? fromBase
        : UniqueItemText.Parse(_notes).FirstOrDefault(l => l.Kind == UniqueLineKind.BaseType)?.Text ?? "";

    /// <summary>Loads a unique's own data and lines. An imported item's text is authoritative, so its
    /// lines come from the text; a unique picked from the pinned list gets the data's own lines.</summary>
    private void LoadUnique(string name, string? itemText = null)
    {
        _uniqueData = _catalog.UniqueData.For(name);
        UniqueVariantChoices.Clear();
        _uniqueVariant = 1;
        if (_uniqueData is not null)
        {
            foreach (int variant in UniqueItemText.Variants(_uniqueData))
                UniqueVariantChoices.Add(new(variant.ToString(CultureInfo.InvariantCulture), UniqueItemText.VariantName(_uniqueData, variant)));
            _uniqueVariant = UniqueItemText.CurrentVariant(_uniqueData);
        }
        _uniqueVariantChoice = UniqueVariantChoices.FirstOrDefault(c => c.Id == _uniqueVariant.ToString(CultureInfo.InvariantCulture))
            ?? UniqueVariantChoices.FirstOrDefault();
        var textLines = UniqueItemText.Parse(itemText);
        if (textLines.Any(l => l.Kind is UniqueLineKind.Implicit or UniqueLineKind.Modifier)) FillUniqueLines(textLines);
        else if (_uniqueData is not null) LoadUniqueLines();
        else if (_notes.Length == 0) Notes = L["UniqueNotesHint"];
        foreach (var p in new[] { nameof(HasUniqueData), nameof(HasUniqueVariants), nameof(SelectedUniqueVariant), nameof(UniqueVariantLabel) }) Raise(p);
    }

    private void LoadUniqueLines()
    {
        if (_uniqueData is null) { UniqueLines.Clear(); Raise(nameof(HasUniqueLines)); Raise(nameof(Preview)); return; }
        FillUniqueLines(UniqueItemText.Lines(_uniqueData, _uniqueVariant, _showAllVariants));
    }

    private void FillUniqueLines(IReadOnlyList<UniqueTextLine> lines)
    {
        UniqueLines.Clear();
        foreach (var line in lines)
        {
            if (line.Kind is not (UniqueLineKind.Implicit or UniqueLineKind.Modifier)) continue;
            UniqueLines.Add(new(line, Marker(line), () => { ComposeUniqueNotes(); Touch(); }, RemoveUniqueLine));
        }
        ComposeUniqueNotes();
        RefreshUniqueLineOptions();
        Touch();
        Raise(nameof(HasUniqueLines)); Raise(nameof(UniqueVariantLabel));
    }

    /// <summary>Takes one of the unique's own lines off the item (the editor's ✕), so a wrong variant line or
    /// an unwanted roll can be dropped instead of only edited.</summary>
    private void RemoveUniqueLine(UniqueLineDraft line)
    {
        if (!UniqueLines.Remove(line)) return;
        ComposeUniqueNotes(force: true);
        RefreshUniqueLineOptions();
        Touch();
        Raise(nameof(HasUniqueLines));
    }

    /// <summary>Rebuilds the offer list of the unique's own modifier lines. Every variant is searched, so the
    /// "+X to Spirit per Socket filled" a Morior Invictus can roll is offered even while the item carries the
    /// Current variant's line; a line already on the item is not offered a second time, and the item's own
    /// text stays the single source of truth (nothing is added until the button is pressed).</summary>
    private void RefreshUniqueLineOptions()
    {
        UniqueLineOptions.Clear();
        if (_uniqueData is not null && IsUniqueDraft)
            foreach (var line in UniqueItemText.PersonalMods(_uniqueData, UniqueLines.Select(l => l.Template)))
                UniqueLineOptions.Add(new(line.Text, line.Resolved, UniqueItemText.VariantLabel(_uniqueData, line), line.Variants, line.Tags));
        _selectedUniqueLineOption = null;
        Raise(nameof(SelectedUniqueLineOption)); Raise(nameof(HasUniqueLineOptions));
    }

    /// <summary>Names what a line's filter means, so a variant-only or tagged line is never mistaken for
    /// an unconditional one.</summary>
    private string Marker(UniqueTextLine line)
    {
        var parts = new List<string>();
        if (line.IsVariantFiltered) parts.Add(L.Format("UniqueVariantOnly", string.Join(", ", line.Variants)));
        if (line.IsTagged) parts.Add(L.Format("UniqueTagged", string.Join(", ", line.Tags)));
        return string.Join(" · ", parts);
    }

    /// <summary>The notes ARE the item's text, so they are rebuilt from the lines above the moment a line
    /// changes: "Rarity: UNIQUE / name / base type / Implicits: N / lines" — the shape our own importer
    /// reads back. <paramref name="force"/> rebuilds even when no line is left, which is what the ✕ that
    /// removes the last one needs (a unique with all of its lines taken off keeps a header-only text).</summary>
    private void ComposeUniqueNotes(bool force = false)
    {
        if (UniqueLines.Count == 0 && !force) return;
        // "\n", not the platform newline: this text is stored in the build file and read back by a parser
        // that must behave the same on every system (the importer normalises newlines either way).
        var text = new System.Text.StringBuilder();
        foreach (var head in new[] { UniqueItemText.RarityHeader, Name.Length > 0 ? Name.Trim() : _uniqueData?.Name ?? "", BaseTypeText })
            text.Append(head).Append('\n');
        int implicits = UniqueLines.Count(l => l.IsImplicit);
        if (implicits > 0) text.Append("Implicits: ").Append(implicits).Append('\n');
        foreach (var line in UniqueLines) text.Append(line.Text).Append('\n');
        _notes = text.ToString().TrimEnd('\n');
        Raise(nameof(Notes)); Raise(nameof(BaseTypeText));
    }

    public ItemMod? SelectedMod { get => _selectedMod; set => Set(ref _selectedMod, value); }
    public Augment? SelectedAugment { get => _selectedAugment; set { Set(ref _selectedAugment, value); Raise(nameof(AugmentPreview)); } }
    public string AugmentPreview => SelectedBase is not null && SelectedAugment is not null ? _catalog.AugmentEffect(SelectedBase, SelectedAugment) : "";

    /// <summary>Game-like tooltip preview built from the pinned base data and the current rolls. A unique
    /// has no catalog base, so it is built from its own lines (or from its own text when it was imported
    /// and the pinned data does not know it).</summary>
    public IReadOnlyList<PreviewLine> Preview => BuildPreview();
    private PreviewLine[] BuildPreview()
    {
        if (IsUniqueDraft) return BuildUniquePreview();
        var lines = new List<PreviewLine>();
        var b = _selectedBase;
        if (b is null) return [new(L["ItemBase"], Dim, 13, false)];
        string name = _name.Length > 0 ? _name : b.Name;
        lines.Add(new(name, RarityBrush(), 18, true));
        lines.Add(new(b.ClassName, Line, 13, false));
        lines.Add(new("", Line, 4, false));
        var p = b.Props;
        if (p.IsWeapon)
        {
            lines.Add(new($"Two Handed Weapon: {b.Tags.Contains("two_hand_weapon")}", Line, 13, false));
            lines.Add(new($"Physical Damage: {p.PhysMin}–{p.PhysMax}", Line, 13, false));
            lines.Add(new($"Critical Strike Chance: {(p.CritChance ?? 0) / 100m:0.00}%", Line, 13, false));
            lines.Add(new($"Attacks per Second: {(1000m / (p.AttackTime ?? 1000)):0.00}", Line, 13, false));
            if (p.Range is int range) lines.Add(new($"Weapon Range: {range / 10m:0.0} metres", Line, 13, false));
        }
        if ((p.Armour ?? 0) > 0) lines.Add(new($"Armour: {p.Armour}", Line, 13, false));
        if ((p.Evasion ?? 0) > 0) lines.Add(new($"Evasion Rating: {p.Evasion}", Line, 13, false));
        if ((p.EnergyShield ?? 0) > 0) lines.Add(new($"Energy Shield: {p.EnergyShield}", Line, 13, false));
        if ((p.Block ?? 0) > 0) lines.Add(new($"Chance to Block: {p.Block:0.##}%", Line, 13, false));
        if ((p.MovementSpeed ?? 0) != 0) lines.Add(new($"Movement Speed: {p.MovementSpeed:0.##}%", Line, 13, false));
        if ((p.ChargesMax ?? 0) > 0)
        {
            lines.Add(new($"Charges: {p.ChargesPerUse} of {p.ChargesMax}", Line, 13, false));
            if (p.Duration is int d) lines.Add(new($"Lasts {d / 10m:0.#} Seconds", Line, 13, false));
            if ((p.LifePerUse ?? 0) > 0) lines.Add(new($"Recovers {p.LifePerUse} Life", Line, 13, false));
            if ((p.ManaPerUse ?? 0) > 0) lines.Add(new($"Recovers {p.ManaPerUse} Mana", Line, 13, false));
        }
        var reqs = new List<string> { "Requires Level " + Math.Max(p.ReqLevel ?? 0, b.DropLevel) };
        if ((p.ReqStr ?? 0) > 0) reqs.Add(p.ReqStr + " Str");
        if ((p.ReqDex ?? 0) > 0) reqs.Add(p.ReqDex + " Dex");
        if ((p.ReqInt ?? 0) > 0) reqs.Add(p.ReqInt + " Int");
        if (reqs.Count > 1 || (p.ReqLevel ?? 0) > 0) lines.Add(new(string.Join(", ", reqs), Line, 13, false));
        lines.Add(new($"Item Level: {ItemLevel}", Dim, 13, false));
        if (int.TryParse(_quality, out int q) && q > 0) lines.Add(new($"Quality: +{q}%", Mod, 13, false));
        if (int.TryParse(_capacity, out int sockets) && sockets > 0) lines.Add(new($"Sockets: {sockets}", Line, 13, false));
        if (b.Implicits.Length > 0)
        {
            lines.Add(new("", Line, 4, false));
            foreach (var implicitLine in b.Implicits) lines.Add(new(implicitLine, Mod, 13, false));
        }
        if (Mods.Count > 0)
        {
            lines.Add(new("", Line, 4, false));
            foreach (var mod in Mods) lines.Add(new(mod.Text, Mod, 13, false));
        }
        if (CorruptedList.Count > 0)
        {
            lines.Add(new("", Line, 4, false));
            foreach (var mod in CorruptedList) lines.Add(new(mod.Text, Corrupt, 13, false));
        }
        if (Corrupted) lines.Add(new(L["ItemCorrupted"], Corrupt, 13, false));
        if (Augments.Count > 0)
        {
            lines.Add(new("", Line, 4, false));
            foreach (var aug in Augments) lines.Add(new(aug.Text, Gold, 13, false));
        }
        return [.. lines];
    }
    /// <summary>The unique tooltip: its name, its base type, its variant's modifier lines in the game's
    /// own order (implicits first), and — for an imported unique whose text is all we have — that text
    /// parsed into the same coloured lines instead of an empty panel.</summary>
    private PreviewLine[] BuildUniquePreview()
    {
        var lines = new List<PreviewLine>();
        if (UniqueLines.Count == 0)
        {
            var parsed = UniqueItemText.Parse(_notes);
            if (parsed.Count == 0) return [new(L["UniqueNotesHint"], Dim, 13, false)];
            foreach (var line in parsed)
            {
                if (line.Kind == UniqueLineKind.Note) continue;
                lines.Add(new(line.Text, PreviewBrush(line.Kind), line.Kind == UniqueLineKind.Name ? 18 : 13, line.Kind == UniqueLineKind.Name));
            }
            if (Corrupted) lines.Add(new(L["ItemCorrupted"], Corrupt, 13, false));
            return [.. lines];
        }
        lines.Add(new(_name.Length > 0 ? _name : _uniqueData?.Name ?? "", NameUnique, 18, true));
        if (BaseTypeText.Length > 0) lines.Add(new(BaseTypeText, Line, 13, false));
        lines.Add(new("", Line, 4, false));
        // Implicits and explicits share the game's modifier blue; the implicit block is simply printed
        // first (which is why UniqueLines keeps the data's own order).
        foreach (var line in UniqueLines) lines.Add(new(line.Text, Mod, 13, false));
        if (Corrupted) { lines.Add(new("", Line, 4, false)); lines.Add(new(L["ItemCorrupted"], Corrupt, 13, false)); }
        return [.. lines];
    }
    private static Brush PreviewBrush(UniqueLineKind kind) => kind switch
    {
        UniqueLineKind.Name => NameUnique, UniqueLineKind.BaseType => Line,
        UniqueLineKind.Implicit or UniqueLineKind.Modifier => Mod, _ => Dim
    };
    private Brush RarityBrush() => Rarity?.Id switch { "normal" => NameNormal, "magic" => NameMagic, "rare" => NameRare, "unique" => NameUnique, _ => NameNormal };
    private static Brush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze(); return brush;
    }

    public ICommand AddModCommand { get; }
    public ICommand AddCorruptCommand { get; }
    public ICommand AddAugmentCommand { get; }
    public ICommand AddUniqueLineCommand { get; }
    public ICommand SaveCommand { get; }
    public ItemDraftViewModel(Localization l, GameCatalog catalog, GearItem? item, string? slot, Action<GearItem> commit)
    {
        L = l; _catalog = catalog; _commit = commit; _slot = slot; _id = item?.Id ?? Guid.NewGuid();
        Rarities = [new("normal", L["RarityNormal"]), new("magic", L["RarityMagic"]), new("rare", L["RarityRare"]), new("unique", L["RarityUnique"])];
        _rarity = Rarities.First(r => r.Id == (item?.Rarity ?? "rare"));
        _selectedBase = item is null ? catalog.Bases.Values.FirstOrDefault(b => b.ItemClass == "Body Armour") ?? catalog.Bases.Values.FirstOrDefault() :
            (item.BaseId.Length > 0 && catalog.Bases.TryGetValue(item.BaseId, out var existingBase) ? existingBase : null);
        _name = item?.Name ?? _selectedBase?.Name ?? ""; _level = (item?.ItemLevel ?? Math.Max(80, _selectedBase?.DropLevel ?? 1)).ToString();
        _quality = (item?.Quality ?? 0).ToString(); _capacity = (item?.SocketCapacity ?? 0).ToString(); _notes = item?.Notes ?? "";
        _corrupted = item?.Corrupted ?? false;
        // A unique opens its own block: its variant and its modifier lines. An imported item's text is
        // authoritative, so it is passed in and PoB2's data is used only when there is no text.
        if (_rarity.Id == "unique" && _name.Length > 0) LoadUnique(_name, _notes);
        foreach (var roll in item?.Mods ?? []) Mods.Add(new(catalog.Mods[roll.Id], roll.Values, Touch, m => { Mods.Remove(m); Touch(); Raise(nameof(AvailableMods)); }));
        foreach (var roll in item?.CorruptedMods ?? []) CorruptedList.Add(new(catalog.Mods[roll.Id], roll.Values, Touch, m => { CorruptedList.Remove(m); Touch(); Raise(nameof(AvailableCorrupted)); }));
        foreach (var id in item?.Augments ?? []) Augments.Add(new(id, catalog.Augments[id].Name, catalog.AugmentEffect(_selectedBase!, catalog.Augments[id]), a => { Augments.Remove(a); Touch(); }));
        AddModCommand = new ActionCommand(_ =>
        {
            if (SelectedMod is null || !AvailableMods.Any(m => m.Id == SelectedMod.Id)) return;
            Mods.Add(new(SelectedMod, SelectedMod.Stats.Select(s => s.Max).ToArray(), Touch, m => { Mods.Remove(m); Touch(); Raise(nameof(AvailableMods)); })); SelectedMod = null; Touch(); Raise(nameof(AvailableMods));
        }, () => !IsUniqueDraft && SelectedMod is not null && Mods.Count < MaxMods);
        AddCorruptCommand = new ActionCommand(_ =>
        {
            if (SelectedCorrupted is null || CorruptedList.Count >= 1 || IsUniqueDraft) return;
            CorruptedList.Add(new(SelectedCorrupted, SelectedCorrupted.Stats.Select(st => st.Max).ToArray(), Touch, m => { CorruptedList.Remove(m); Touch(); Raise(nameof(AvailableCorrupted)); }, corrupted: true));
            Corrupted = true; SelectedCorrupted = null; Touch(); Raise(nameof(AvailableCorrupted));
        }, () => SelectedCorrupted is not null && CorruptedList.Count < 1 && !IsUniqueDraft);
        AddAugmentCommand = new ActionCommand(_ =>
        {
            if (SelectedAugment is null || SelectedBase is null) return;
            if (!int.TryParse(Capacity, out int capacity) || Augments.Count >= capacity || capacity > 6) { Error = L["PlanSockets"]; return; }
            Augments.Add(new(SelectedAugment.Id, SelectedAugment.Name, AugmentPreview, a => { Augments.Remove(a); Touch(); })); Touch();
        }, () => SelectedAugment is not null);
        AddUniqueLineCommand = new ActionCommand(_ =>
        {
            if (SelectedUniqueLineOption is not { } option || !IsUniqueDraft) return;
            // The added line is an ordinary explicit modifier of the item (or an implicit when it sits in
            // the data's implicit block); it is editable afterwards and goes into the item's text, which is
            // what the calculator reads.
            var line = new UniqueTextLine(option.Text, option.Resolved, UniqueLineKind.Modifier, option.Variants, option.Tags);
            UniqueLines.Add(new(line, Marker(line), () => { ComposeUniqueNotes(); Touch(); }, RemoveUniqueLine));
            ComposeUniqueNotes();
            RefreshUniqueLineOptions();
            Touch();
            Raise(nameof(HasUniqueLines));
        }, () => SelectedUniqueLineOption is not null && IsUniqueDraft);
        SaveCommand = new ActionCommand(_ => Save()); _loading = false;
    }
    private void Touch()
    {
        if (!_loading) IsDirty = true;
        Error = "";
        Raise(nameof(Preview)); Raise(nameof(ItemIcon));
        CommandManager.InvalidateRequerySuggested();
    }
    private void Save()
    {
        try
        {
            if (!int.TryParse(ItemLevel, out int level) || !int.TryParse(Quality, out int quality) || !int.TryParse(Capacity, out int cap)) throw new PlanningException("PlanItemNumbers");
            if (Rarity?.Id != "unique" && SelectedBase is null) throw new PlanningException("PlanUnknownBase");
            var gear = new GearItem { Id = _id, BaseId = SelectedBase?.Id ?? "", Name = Name.Trim(), Rarity = Rarity!.Id, ItemLevel = level, Quality = quality, SocketCapacity = cap, Mods = Mods.Select(m => m.ToRoll()).ToArray(), Corrupted = Corrupted, CorruptedMods = CorruptedList.Select(m => m.ToRoll()).ToArray(), Augments = Augments.Select(a => a.Id).ToArray(), Notes = Notes };
            EquipmentRules.ValidateItem(_catalog, gear); _commit(gear); Accepted = true; Saved?.Invoke();
        }
        catch (PlanningException e) { Error = L[e.Code]; }
        catch (BuildFormatException e) { Error = L["PlanInvalid"] + "\n" + e.Message; }
    }
}
