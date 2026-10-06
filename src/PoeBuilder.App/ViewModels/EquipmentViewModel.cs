using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PoeBuilder.App.Services;
using PoeBuilder.App.Views;
using PoeBuilder.Core.Equipment;
using Localization = PoeBuilder.App.Services.Localization;

namespace PoeBuilder.App.ViewModels;

public sealed record SlotChoice(string Id, string Name) { public override string ToString() => Name; }
public sealed record GearRow(Guid Id, string Name, string Summary, ImageSource? Icon, string Detail);
/// <summary>One equipment slot on the board. <see cref="Border"/> is its border brush: the item's rarity
/// colour normally, a highlight colour while a dragged item can legally be dropped here.</summary>
public sealed class InventorySlot : Observable
{
    private static readonly Brush Highlight = Freeze("#FFD97B");
    private static Brush Freeze(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    public string Id { get; }
    public string Label { get; }
    public string ItemName { get; }
    public ImageSource? Icon { get; }
    public ImageSource? GhostIcon { get; }
    public bool Occupied { get; }
    public Brush Accent { get; }
    public string Detail { get; }
    private bool _isHighlight;
    /// <summary>True while a drag hovers this slot and the dragged item fits it — the view paints a
    /// golden border so the player sees the legal drop targets instead of guessing.</summary>
    public bool IsHighlight { get => _isHighlight; set { if (Set(ref _isHighlight, value)) Raise(nameof(Border)); } }
    public Brush Border => _isHighlight ? Highlight : Accent;
    public InventorySlot(string id, string label, string itemName, ImageSource? icon, ImageSource? ghostIcon,
        bool occupied, Brush accent, string detail)
    {
        Id = id; Label = label; ItemName = itemName; Icon = icon; GhostIcon = ghostIcon;
        Occupied = occupied; Accent = accent; Detail = detail;
    }
}

public sealed class EquipmentViewModel : Observable
{
    public Localization L { get; }
    public GameCatalog? Catalog { get; private set; }
    private BuildEditor? _editor;
    private EquipmentPlan _plan = new();
    private readonly PlanHistory<EquipmentPlan> _history = new(p => p.Copy());
    private string _warning = "", _status = "", _search = "";
    private SlotChoice? _selectedSlot;
    private GearRow? _selectedItem;
    public bool CanEdit => _editor is not null && Catalog is not null && _warning.Length == 0;
    public string Warning => _editor is null ? L["NoBuildText"] : Catalog is null ? L["CatalogMissing"] : _warning;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Search { get => _search; set { if (Set(ref _search, value)) { if (Locale is not null) Locale.Search = value; RefreshItems(); } } }
    /// <summary>Display language for base and unique names. Null keeps everything English.</summary>
    public GameLocale? Locale { get; set; }
    public int WeaponSet => _plan.WeaponSet;
    public ObservableCollection<SlotChoice> SlotChoices { get; } = [];
    public ObservableCollection<InventorySlot> Slots { get; } = [];
    public ObservableCollection<GearRow> Items { get; } = [];
    public InventorySlot? HelmetSlot => FindSlot("Helmet");
    public InventorySlot? AmuletSlot => FindSlot("Amulet");
    public InventorySlot? GlovesSlot => FindSlot("Gloves");
    public InventorySlot? BodySlot => FindSlot("Body");
    public InventorySlot? Ring1Slot => FindSlot("Ring1");
    public InventorySlot? BeltSlot => FindSlot("Belt");
    public InventorySlot? Ring2Slot => FindSlot("Ring2");
    public InventorySlot? BootsSlot => FindSlot("Boots");
    public InventorySlot? Main1Slot => FindSlot("Main1");
    public InventorySlot? Off1Slot => FindSlot("Off1");
    public InventorySlot? Main2Slot => FindSlot("Main2");
    public InventorySlot? Off2Slot => FindSlot("Off2");
    public InventorySlot? LifeFlaskSlot => FindSlot("LifeFlask");
    public InventorySlot? ManaFlaskSlot => FindSlot("ManaFlask");
    public InventorySlot? Charm1Slot => FindSlot("Charm1");
    public InventorySlot? Charm2Slot => FindSlot("Charm2");
    public InventorySlot? Charm3Slot => FindSlot("Charm3");
    public SlotChoice? SelectedSlot { get => _selectedSlot; set => Set(ref _selectedSlot, value); }
    public GearRow? SelectedItem { get => _selectedItem; set { Set(ref _selectedItem, value); CommandManager.InvalidateRequerySuggested(); } }
    public ICommand OpenSlotCommand { get; }
    public ICommand NewItemCommand { get; }
    public ICommand EditItemCommand { get; }
    public ICommand EquipCommand { get; }
    public ICommand UnequipCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand SetOneCommand { get; }
    public ICommand SetTwoCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand ResetCommand { get; }
    public EquipmentViewModel(Localization l)
    {
        L = l;
        OpenSlotCommand = new ActionCommand(arg =>
        {
            if (arg is not InventorySlot slot) return;
            SelectedSlot = SlotChoices.First(s => s.Id == slot.Id);
            Edit(_plan.Slots.TryGetValue(slot.Id, out var id) ? _plan.Items.Single(i => i.Id == id) : null, slot.Id);
        }, () => CanEdit);
        NewItemCommand = new ActionCommand(_ => Edit(null, null), () => CanEdit);
        EditItemCommand = new ActionCommand(_ => Edit(CurrentItem(), null), () => CanEdit && SelectedItem is not null);
        EquipCommand = new ActionCommand(_ => Run(() => Apply(EquipmentRules.Put(Catalog!, _plan, CurrentItem(), SelectedSlot!.Id))), () => CanEdit && SelectedItem is not null && SelectedSlot is not null);
        UnequipCommand = new ActionCommand(_ => Run(() => Apply(EquipmentRules.Unequip(Catalog!, _plan, SelectedSlot!.Id))), () => CanEdit && SelectedSlot is not null && _plan.Slots.ContainsKey(SelectedSlot.Id));
        DeleteCommand = new ActionCommand(_ =>
        {
            if (Confirm(L["DeleteItemQuestion"])) Run(() => Apply(EquipmentRules.Delete(Catalog!, _plan, CurrentItem().Id)));
        }, () => CanEdit && SelectedItem is not null);
        DuplicateCommand = new ActionCommand(_ => Run(() => Apply(EquipmentRules.Put(Catalog!, _plan, CurrentItem().Copy() with { Id = Guid.NewGuid() }, null))), () => CanEdit && SelectedItem is not null && _plan.Items.Length < 250);
        SetOneCommand = new ActionCommand(_ => { if (_plan.WeaponSet != 1) Apply(_plan with { WeaponSet = 1 }); }, () => CanEdit);
        SetTwoCommand = new ActionCommand(_ => { if (_plan.WeaponSet != 2) Apply(_plan with { WeaponSet = 2 }); }, () => CanEdit);
        UndoCommand = new ActionCommand(_ => Restore(_history.Undo(_plan)), () => _editor is not null && _history.CanUndo);
        RedoCommand = new ActionCommand(_ => Restore(_history.Redo(_plan)), () => _editor is not null && _history.CanRedo);
        ResetCommand = new ActionCommand(_ => { if (Confirm(L["ResetEquipmentQuestion"])) Apply(new()); }, () => _editor is not null);
        L.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Localization.Language)) Refresh(); };
    }
    /// <summary>Drag-and-drop support: whether a dragged item may be dropped into this slot (its base or
    /// unique class fits). No side effects — the drop layer uses it to highlight legal targets.</summary>
    public bool CanEquipItem(string itemId, string slotId)
    {
        if (Catalog is not null && Guid.TryParse(itemId, out var id) && _plan.Items.FirstOrDefault(i => i.Id == id) is { } item)
            return EquipmentRules.ItemFits(Catalog, item, slotId);
        return false;
    }
    /// <summary>Drag-and-drop support: drop a dragged inventory item into a slot (displacing anything
    /// already there, which returns to the inventory). The slot match was shown during the drag.</summary>
    public void EquipDraggedItem(string itemId, string slotId)
    {
        if (!Guid.TryParse(itemId, out var id) || _plan.Items.FirstOrDefault(i => i.Id == id) is not { } item) return;
        Run(() => Apply(EquipmentRules.Put(Catalog!, _plan, item, slotId)));
    }
    public void SetCatalog(GameCatalog catalog) { Catalog = catalog; Validate(); Refresh(); }
    public void BindEditor(BuildEditor? editor) { _editor = editor; _plan = editor?.EquipmentSnapshot ?? new(); _history.Clear(); Validate(); Refresh(); }
    private GearItem CurrentItem() => _plan.Items.Single(i => i.Id == SelectedItem!.Id);
    private void Edit(GearItem? item, string? slot)
    {
        var draft = new ItemDraftViewModel(L, Catalog!, item, slot, gear =>
        {
            var next = EquipmentRules.Put(Catalog!, _plan, gear, slot); Apply(next);
            SelectedItem = Items.FirstOrDefault(i => i.Id == gear.Id);
        })
        // The editor lists bases, uniques and modifiers, so it needs the same game locale as the tab behind it.
        { Locale = Locale };
        new ItemEditorWindow(draft) { Owner = Application.Current.MainWindow }.ShowDialog();
    }
    private void Apply(EquipmentPlan next) { _history.Record(_plan); Restore(next); }
    private void Restore(EquipmentPlan next) { _plan = next.Copy(); _editor?.SetEquipment(_plan); Validate(); Refresh(); }
    private void Validate()
    {
        _warning = "";
        if (Catalog is null) return;
        try { EquipmentRules.Validate(Catalog, _plan); }
        catch (PlanningException e) { _warning = L[e.Code]; }
    }
    private void RefreshItems()
    {
        Guid? selected = SelectedItem?.Id; Items.Clear();
        foreach (var item in _plan.Items)
        {
            var b = Catalog?.Bases.GetValueOrDefault(item.BaseId);
            // Only the game's own names are translated. A name the user typed (or that an import brought
            // in verbatim) is theirs and is shown untouched, so imported builds keep their exact text.
            string baseName = Locale?.Name(b?.Name) ?? b?.Name ?? "";
            string name = item.Name.Length > 0 ? item.Name : baseName.Length > 0 ? baseName : item.BaseId;
            if (Search.Length > 0 && !(Locale is null ? (name + " " + b?.Name).Contains(Search, StringComparison.OrdinalIgnoreCase) : Locale.Matches(name, b?.Name))) continue;
            var slots = _plan.Slots.Where(s => s.Value == item.Id).Select(s => L["Slot" + s.Key]);
            // PoE2 jewels are absent from the pinned base list: say so instead of an empty base id.
            string baseText = baseName.Length > 0 ? baseName : (item.BaseId.Length > 0 ? item.BaseId : L["JewelNoBase"]);
            Items.Add(new(item.Id, name, $"{baseText} · {L["ItemLevelShort"]} {item.ItemLevel} · {string.Join(", ", slots)}",
                ItemIcon(item, b), DescribeItem(item)));
        }
        SelectedItem = Items.FirstOrDefault(i => i.Id == selected); Raise(nameof(Items));
    }
    private ImageSource? ItemIcon(GearItem item, ItemBase? itemBase) =>
        Catalog is null ? null : IconService.Instance.ForItem(Catalog, item, itemBase);

    /// <summary>Tooltip text: an imported unique keeps its full verbatim text; rolled items show
    /// their affixes with the actual values substituted into the pinned templates, and any socketed
    /// runes/soul cores follow with their name and effect. A roll whose id
    /// no mod dictionary knows (a synthetic stat-id fallback) still shows its readable stand-in instead
    /// of silently disappearing.</summary>
    private string DescribeItem(GearItem item)
    {
        if (item.Notes.Length > 0) return item.Notes;
        if (item.Mods.Length == 0 && item.Augments.Length == 0) return "";
        if (Catalog is null) return "";
        var lines = new List<string>();
        foreach (var roll in item.Mods)
        {
            int i = 0;
            var mod = ItemModResolver.For(Catalog, roll);
            // The Russian line carries the SAME numbers as the English template, so the rolled values are
            // substituted into it exactly as into the English one. A modifier with no translation falls
            // back to the English text rather than to an invented Russian one.
            var template = Locale?.Mod(mod.Text) ?? mod.Text;
            lines.Add(System.Text.RegularExpressions.Regex.Replace(template, "#", _ => i < roll.Values.Length ? roll.Values[i++].ToString(System.Globalization.CultureInfo.InvariantCulture) : "#"));
        }
        if (item.Augments.Length > 0 && Catalog.Bases.TryGetValue(item.BaseId, out var augmentBase))
        {
            foreach (var augmentId in item.Augments)
            {
                if (!Catalog.Augments.TryGetValue(augmentId, out var augment) ||
                    Catalog.AugmentEffect(augmentBase, augment) is not { Length: > 0 } effect) continue;
                lines.Add(augment.Name);
                lines.Add(effect);
            }
        }
        return string.Join("\n", lines);
    }
    private void Refresh()
    {
        var selected = SelectedSlot?.Id ?? "Body"; SlotChoices.Clear();
        foreach (var id in EquipmentRules.SlotIds) SlotChoices.Add(new(id, L["Slot" + id]));
        SelectedSlot = SlotChoices.FirstOrDefault(s => s.Id == selected);
        RefreshItems(); Slots.Clear();
        Add("Helmet"); Add("Amulet"); Add("Gloves"); Add("Body"); Add("Ring1"); Add("Belt");
        Add("Ring2"); Add("Boots"); Add("Main1"); Add("Off1"); Add("Main2"); Add("Off2");
        Add("LifeFlask"); Add("ManaFlask"); Add("Charm1"); Add("Charm2"); Add("Charm3");
        foreach (var name in new[]
        {
            nameof(HelmetSlot), nameof(AmuletSlot), nameof(GlovesSlot), nameof(BodySlot), nameof(Ring1Slot),
            nameof(BeltSlot), nameof(Ring2Slot), nameof(BootsSlot), nameof(Main1Slot), nameof(Off1Slot),
            nameof(Main2Slot), nameof(Off2Slot), nameof(LifeFlaskSlot), nameof(ManaFlaskSlot),
            nameof(Charm1Slot), nameof(Charm2Slot), nameof(Charm3Slot)
        }) Raise(name);
        foreach (var name in new[] { nameof(CanEdit), nameof(Warning), nameof(WeaponSet) }) Raise(name);
        CommandManager.InvalidateRequerySuggested();
    }
    private InventorySlot? FindSlot(string id) => Slots.FirstOrDefault(slot => slot.Id == id);
    private void Add(string id)
    {
        var item = _plan.Slots.TryGetValue(id, out var key) ? _plan.Items.FirstOrDefault(i => i.Id == key) : null;
        var b = item is null ? null : Catalog?.Bases.GetValueOrDefault(item.BaseId);
        string text = item is null ? L["EmptySlot"] : item.Name.Length > 0 ? item.Name : b?.Name ?? item.BaseId;
        var accent = FindBrush(item?.Rarity);
        Slots.Add(new(id, L["Slot" + id], text,
            item is null ? null : ItemIcon(item, b),
            Ghost(id, b),
            item is not null, accent,
            item is null ? "" : DescribeItem(item)));
    }
    private ImageSource? Ghost(string id, ItemBase? filled)
    {
        if (filled is not null) return null; // occupied slots show the item's own art only
        var className = id switch
        {
            "Helmet" => "Helmets", "Body" => "Body Armours", "Gloves" => "Gloves", "Boots" => "Boots", "Belt" => "Belts",
            "Amulet" => "Amulets", "Ring1" or "Ring2" => "Rings",
            "LifeFlask" => "Life Flasks", "ManaFlask" => "Mana Flasks", "Charm1" or "Charm2" or "Charm3" => "Charms",
            "Main1" or "Main2" => "One Hand Swords", "Off1" or "Off2" => "Shields",
            _ => ""
        };
        return IconService.Instance.ForClass(className);
    }
    private static Brush FindBrush(string? rarity)
    {
        var converter = new RarityBrushConverter();
        return (Brush)(converter.Convert(rarity ?? (object)"", typeof(Brush), null!, System.Globalization.CultureInfo.InvariantCulture) ?? Brushes.Transparent);
    }
    private void Run(Action action)
    {
        try { action(); Status = ""; }
        catch (PlanningException e) { Status = L[e.Code]; }
        catch (PoeBuilder.Core.Models.BuildFormatException e) { Status = L["PlanInvalid"] + "\n" + e.Message; }
        catch (Exception e) { PoeBuilder.App.Services.ErrorLog.Append(e, "VM"); Status = L["Error"] + ": " + e.Message; }
    }
    private bool Confirm(string message) => ThemedDialog.Show(message, L["Confirm"], MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
