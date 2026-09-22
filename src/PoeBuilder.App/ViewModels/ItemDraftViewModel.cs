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
    public NamedOption? Rarity { get => _rarity; set { if (value is not null && Set(ref _rarity, value)) { Touch(); Raise(nameof(AvailableMods)); } } }
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
                Raise(nameof(SelectedBase)); Raise(nameof(BaseIcon));
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
    public ImageSource? BaseIcon => _selectedBase is null ? null : IconService.Instance.ForBase(_selectedBase);
    public IEnumerable<ItemMod> AvailableMods
    {
        get
        {
            if (SelectedBase is null || !int.TryParse(ItemLevel, out int level)) return [];
            int cap = Rarity?.Id == "normal" ? 0 : Rarity?.Id == "magic" ? 1 : 3;
            var groups = Mods.SelectMany(m => m.Definition.Groups).ToHashSet();
            return _catalog.ModsFor(SelectedBase, level).Where(m => !m.Groups.Any(groups.Contains) && Mods.Count(x => x.Definition.Kind == m.Kind) < cap && (ModSearch.Length == 0 || m.DisplayName.Contains(ModSearch, StringComparison.OrdinalIgnoreCase))).OrderBy(m => m.Text).Take(200);
        }
    }
    public IEnumerable<Augment> AvailableAugments => SelectedBase is null ? [] : _catalog.Augments.Values.Where(a => (a.Limit.Length == 0 || a.Limit == "1") && _catalog.AugmentEffect(SelectedBase, a).Length > 0 && (AugmentSearch.Length == 0 || (a.Name + " " + a.Kind + " " + _catalog.AugmentEffect(SelectedBase, a)).Contains(AugmentSearch, StringComparison.OrdinalIgnoreCase))).OrderBy(a => a.Name);

    // --- Unique picker: expose every pinned unique identity, including jewels. Picking one
    // switches the draft to the unique rarity. Their modifiers are not pinned — text by hand. ---
    private string _uniqueFilter = "";
    private string? _selectedUniqueName;
    public string UniqueFilter { get => _uniqueFilter; set { if (Set(ref _uniqueFilter, value)) Raise(nameof(UniqueNames)); } }
    public IEnumerable<string> UniqueNames => _catalog.Uniques.Values
        .Where(u => _uniqueFilter.Length == 0 || u.Name.Contains(_uniqueFilter, StringComparison.OrdinalIgnoreCase))
        .OrderBy(u => u.Name).Select(u => u.Name);
    public string? SelectedUnique
    {
        get => _selectedUniqueName;
        set
        {
            if (!Set(ref _selectedUniqueName, value) || value is null) return;
            Rarity = Rarities.First(r => r.Id == "unique");
            Name = value;
            Mods.Clear(); CorruptedList.Clear();
            if (Notes.Length == 0) Notes = L["UniqueNotesHint"];
        }
    }
    public ItemMod? SelectedMod { get => _selectedMod; set => Set(ref _selectedMod, value); }
    public Augment? SelectedAugment { get => _selectedAugment; set { Set(ref _selectedAugment, value); Raise(nameof(AugmentPreview)); } }
    public string AugmentPreview => SelectedBase is not null && SelectedAugment is not null ? _catalog.AugmentEffect(SelectedBase, SelectedAugment) : "";

    /// <summary>Game-like tooltip preview built from the pinned base data and the current rolls.</summary>
    public IReadOnlyList<PreviewLine> Preview => BuildPreview();
    private PreviewLine[] BuildPreview()
    {
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
    private Brush RarityBrush() => Rarity?.Id switch { "normal" => NameNormal, "magic" => NameMagic, "rare" => NameRare, "unique" => NameUnique, _ => NameNormal };
    private static Brush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze(); return brush;
    }

    public ICommand AddModCommand { get; }
    public ICommand AddCorruptCommand { get; }
    public ICommand AddAugmentCommand { get; }
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
        foreach (var roll in item?.Mods ?? []) Mods.Add(new(catalog.Mods[roll.Id], roll.Values, Touch, m => { Mods.Remove(m); Touch(); Raise(nameof(AvailableMods)); }));
        foreach (var roll in item?.CorruptedMods ?? []) CorruptedList.Add(new(catalog.Mods[roll.Id], roll.Values, Touch, m => { CorruptedList.Remove(m); Touch(); Raise(nameof(AvailableCorrupted)); }));
        foreach (var id in item?.Augments ?? []) Augments.Add(new(id, catalog.Augments[id].Name, catalog.AugmentEffect(_selectedBase!, catalog.Augments[id]), a => { Augments.Remove(a); Touch(); }));
        AddModCommand = new ActionCommand(_ =>
        {
            if (SelectedMod is null || !AvailableMods.Any(m => m.Id == SelectedMod.Id)) return;
            Mods.Add(new(SelectedMod, SelectedMod.Stats.Select(s => s.Max).ToArray(), Touch, m => { Mods.Remove(m); Touch(); Raise(nameof(AvailableMods)); })); SelectedMod = null; Touch(); Raise(nameof(AvailableMods));
        }, () => SelectedMod is not null && Mods.Count < 6);
        AddCorruptCommand = new ActionCommand(_ =>
        {
            if (SelectedCorrupted is null || CorruptedList.Count >= 1) return;
            CorruptedList.Add(new(SelectedCorrupted, SelectedCorrupted.Stats.Select(st => st.Max).ToArray(), Touch, m => { CorruptedList.Remove(m); Touch(); Raise(nameof(AvailableCorrupted)); }, corrupted: true));
            Corrupted = true; SelectedCorrupted = null; Touch(); Raise(nameof(AvailableCorrupted));
        }, () => SelectedCorrupted is not null && CorruptedList.Count < 1);
        AddAugmentCommand = new ActionCommand(_ =>
        {
            if (SelectedAugment is null || SelectedBase is null) return;
            if (!int.TryParse(Capacity, out int capacity) || Augments.Count >= capacity || capacity > 6) { Error = L["PlanSockets"]; return; }
            Augments.Add(new(SelectedAugment.Id, SelectedAugment.Name, AugmentPreview, a => { Augments.Remove(a); Touch(); })); Touch();
        }, () => SelectedAugment is not null);
        SaveCommand = new ActionCommand(_ => Save()); _loading = false;
    }
    private void Touch()
    {
        if (!_loading) IsDirty = true;
        Error = "";
        Raise(nameof(Preview)); Raise(nameof(BaseIcon));
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
