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
public sealed record InventorySlot(string Id, string Label, string ItemName, ImageSource? Icon, ImageSource? GhostIcon,
    double X, double Y, double Width, double Height, bool Occupied, Brush Accent, string Detail = "");

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
    public string Search { get => _search; set { if (Set(ref _search, value)) RefreshItems(); } }
    public int WeaponSet => _plan.WeaponSet;
    public ObservableCollection<SlotChoice> SlotChoices { get; } = [];
    public ObservableCollection<InventorySlot> Slots { get; } = [];
    public ObservableCollection<GearRow> Items { get; } = [];
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
    public void SetCatalog(GameCatalog catalog) { Catalog = catalog; Validate(); Refresh(); }
    public void BindEditor(BuildEditor? editor) { _editor = editor; _plan = editor?.EquipmentSnapshot ?? new(); _history.Clear(); Validate(); Refresh(); }
    private GearItem CurrentItem() => _plan.Items.Single(i => i.Id == SelectedItem!.Id);
    private void Edit(GearItem? item, string? slot)
    {
        var draft = new ItemDraftViewModel(L, Catalog!, item, slot, gear =>
        {
            var next = EquipmentRules.Put(Catalog!, _plan, gear, slot); Apply(next);
            SelectedItem = Items.FirstOrDefault(i => i.Id == gear.Id);
        });
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
            string name = item.Name.Length > 0 ? item.Name : b?.Name ?? item.BaseId;
            if (Search.Length > 0 && !(name + " " + b?.Name).Contains(Search, StringComparison.OrdinalIgnoreCase)) continue;
            var slots = _plan.Slots.Where(s => s.Value == item.Id).Select(s => L["Slot" + s.Key]);
            // PoE2 jewels are absent from the pinned base list: say so instead of an empty base id.
            string baseText = b?.Name ?? (item.BaseId.Length > 0 ? item.BaseId : L["JewelNoBase"]);
            Items.Add(new(item.Id, name, $"{baseText} · {L["ItemLevelShort"]} {item.ItemLevel} · {string.Join(", ", slots)}",
                ItemIcon(item, b), DescribeItem(item)));
        }
        SelectedItem = Items.FirstOrDefault(i => i.Id == selected); Raise(nameof(Items));
    }
    private ImageSource? ItemIcon(GearItem item, ItemBase? itemBase)
    {
        if (itemBase is not null) return IconService.Instance.ForBase(itemBase);
        if (item.Rarity == "unique" && Catalog?.Uniques.TryGetValue(item.Name, out var unique) == true)
            return IconService.Instance.ForUnique(unique);
        return null;
    }

    /// <summary>Tooltip text: an imported unique keeps its full verbatim text; rolled items show
    /// their affixes with the actual values substituted into the pinned templates.</summary>
    private string DescribeItem(GearItem item)
    {
        if (item.Notes.Length > 0) return item.Notes;
        if (item.Mods.Length == 0) return "";
        var lines = new List<string>();
        foreach (var roll in item.Mods)
        {
            var m = Catalog?.Mods.GetValueOrDefault(roll.Id) ?? Catalog?.JewelMods.FirstOrDefault(x => x.Id == roll.Id);
            if (m is null) continue;
            int i = 0;
            lines.Add(System.Text.RegularExpressions.Regex.Replace(m.Text, "#", _ => i < roll.Values.Length ? roll.Values[i++].ToString(System.Globalization.CultureInfo.InvariantCulture) : "#"));
        }
        return string.Join("\n", lines);
    }
    private void Refresh()
    {
        var selected = SelectedSlot?.Id ?? "Body"; SlotChoices.Clear();
        foreach (var id in EquipmentRules.SlotIds) SlotChoices.Add(new(id, L["Slot" + id]));
        SelectedSlot = SlotChoices.FirstOrDefault(s => s.Id == selected);
        RefreshItems(); Slots.Clear();
        // Game-inventory-like arrangement: weapon sets on the left, humanoid block in the centre,
        // flasks and charms along the bottom. 74 px slots; icons come from the bundled game art.
        Add("Helmet", 196, 14);
        Add("Amulet", 282, 14);
        Add("Gloves", 110, 102);
        Add("Body", 196, 102);
        Add("Ring1", 282, 102);
        Add("Belt", 196, 190);
        Add("Ring2", 282, 190);
        Add("Boots", 196, 278);
        Add("Main1", 14, 102);
        Add("Off1", 14, 190);
        Add("Main2", 14, 296);
        Add("Off2", 14, 384);
        Add("LifeFlask", 110, 400);
        Add("ManaFlask", 196, 400);
        Add("Charm1", 282, 400);
        Add("Charm2", 368, 400);
        Add("Charm3", 454, 400);
        foreach (var name in new[] { nameof(CanEdit), nameof(Warning), nameof(WeaponSet) }) Raise(name);
        CommandManager.InvalidateRequerySuggested();
    }
    private void Add(string id, double x, double y)
    {
        var item = _plan.Slots.TryGetValue(id, out var key) ? _plan.Items.FirstOrDefault(i => i.Id == key) : null;
        var b = item is null ? null : Catalog?.Bases.GetValueOrDefault(item.BaseId);
        string text = item is null ? L["EmptySlot"] : item.Name.Length > 0 ? item.Name : b?.Name ?? item.BaseId;
        var accent = FindBrush(item?.Rarity);
        Slots.Add(new(id, L["Slot" + id], text,
            item is null ? null : ItemIcon(item, b),
            Ghost(id, b),
            x, y, 74, 74, item is not null, accent,
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
    private bool Confirm(string message) => MessageBox.Show(message, L["Confirm"], MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
