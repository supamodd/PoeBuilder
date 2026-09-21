using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using PoeBuilder.App.Views;
using Localization = PoeBuilder.App.Services.Localization;
using PoeBuilder.App.Services;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Models;

namespace PoeBuilder.App.ViewModels;

public sealed record JewelRow(Guid Id, string Name, string Summary, string Detail);
public sealed record JewelSocketChoice(int NodeId, string Label, Guid? JewelId) { public override string ToString() => Label; }

/// <summary>Jewels tab: jewel inventory (imported or hand-made), jewel creation and
/// socketing into allocated tree jewel sockets. Jewels use the jewel affix pool; PoE2
/// jewel bases are absent from the pinned base list, so items stay baseless on purpose.</summary>
public sealed class JewelsViewModel : Observable
{
    public Localization L { get; }
    public GameCatalog? Catalog { get; private set; }
    private BuildEditor? _editor;
    private EquipmentPlan _plan = new();
    private TreeViewModel? _tree;
    private string _search = "", _status = "";

    public JewelsViewModel(Localization l) => L = l;

    public ObservableCollection<JewelRow> Rows { get; } = [];
    public ObservableCollection<JewelSocketChoice> FreeSockets { get; } = [];
    public ObservableCollection<JewelSocketChoice> FilledSockets { get; } = [];
    public JewelRow? SelectedRow { get => _selectedRow; set { if (Set(ref _selectedRow, value)) Raise(nameof(SocketCanExecute)); } }
    private JewelRow? _selectedRow;
    public JewelSocketChoice? SelectedFreeSocket { get => _selectedFreeSocket; set { if (Set(ref _selectedFreeSocket, value)) Raise(nameof(SocketCanExecute)); } }
    private JewelSocketChoice? _selectedFreeSocket;
    public JewelSocketChoice? SelectedFilledSocket { get => _selectedFilledSocket; set { if (Set(ref _selectedFilledSocket, value)) Raise(nameof(UnsocketCanExecute)); } }
    private JewelSocketChoice? _selectedFilledSocket;
    public string Search { get => _search; set { if (Set(ref _search, value)) RefreshRows(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool SocketCanExecute => Catalog is not null && SelectedRow is not null && SelectedFreeSocket is not null && CanEdit;
    public bool UnsocketCanExecute => Catalog is not null && SelectedFilledSocket is not null && CanEdit;
    public bool CanEdit => _editor is not null;

    public ICommand CreateCommand { get; private set; } = new ActionCommand(_ => { }, () => false);
    public ICommand SocketCommand { get; private set; } = new ActionCommand(_ => { }, () => false);
    public ICommand UnsocketCommand { get; private set; } = new ActionCommand(_ => { }, () => false);

    public void SetCatalog(GameCatalog? catalog) { Catalog = catalog; RecreateCommands(); RefreshAll(); }
    public void BindEditor(BuildEditor? editor, TreeViewModel? tree)
    {
        if (_tree is not null) _tree.PlanChanged -= TreeChanged;
        _editor = editor; _tree = tree;
        _plan = editor?.EquipmentSnapshot ?? new();
        if (_tree is not null) _tree.PlanChanged += TreeChanged;
        RecreateCommands();
        RefreshAll();
    }
    private void RecreateCommands()
    {
        CreateCommand = new ActionCommand(_ => Create(), () => Catalog is not null && CanEdit);
        SocketCommand = new ActionCommand(_ => Run(() =>
        {
            if (SelectedRow is null || SelectedFreeSocket is null || _tree is null) return;
            if (!_tree.SocketJewel(SelectedRow.Id, SelectedFreeSocket.NodeId)) { Status = L["JewelSocketFailed"]; return; }
            Status = "";
        }), () => SocketCanExecute);
        UnsocketCommand = new ActionCommand(_ => Run(() =>
        {
            if (SelectedFilledSocket is null || _tree is null) return;
            _ = _tree.UnsocketJewel(SelectedFilledSocket.NodeId);
        }), () => UnsocketCanExecute);
        Raise(nameof(CreateCommand)); Raise(nameof(SocketCommand)); Raise(nameof(UnsocketCommand));
        Raise(nameof(SocketCanExecute)); Raise(nameof(UnsocketCanExecute));
    }
    private void TreeChanged() { try { _plan = _editor?.EquipmentSnapshot ?? _plan; } catch { } RefreshSockets(); }
    private void Run(Action action) { try { action(); Status = ""; } catch (Exception e) { ErrorLog.Append(e, "Jewels"); Status = L["Error"] + ": " + e.Message; } }

    private bool IsJewelItem(GearItem i)
    {
        if (i.BaseId.Length != 0) return false; // based items are always gear; jewel bases are not pinned
        if (i.Rarity is "magic" or "rare") return true; // baseless rolled items are jewels by the validation law
        if (i.Rarity == "unique" && Catalog is not null && Catalog.Uniques.TryGetValue(i.Name, out var u))
            return u.ItemClass.Equals("Jewel", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private void RefreshAll() { RefreshRows(); RefreshSockets(); }

    private void RefreshRows()
    {
        Guid? selected = SelectedRow?.Id; Rows.Clear();
        if (Catalog is null) { SelectedRow = null; return; }
        foreach (var item in _plan.Items.Where(IsJewelItem))
        {
            string name = item.Name.Length > 0 ? item.Name : L["JewelUnnamed"];
            if (Search.Length > 0 && !name.Contains(Search, StringComparison.OrdinalIgnoreCase)) continue;
            string summary = $"{L[RarityLabel(item.Rarity)]} · {L["ItemLevelShort"]} {item.ItemLevel} · {L["ItemQuality"]} {item.Quality}";
            var mods = new List<string>();
            foreach (var roll in item.Mods)
            {
                var m = Catalog.JewelMods.FirstOrDefault(x => x.Id == roll.Id);
                if (m is null) continue;
                int k = 0;
                mods.Add(System.Text.RegularExpressions.Regex.Replace(m.Text, "#", _ => k < roll.Values.Length ? roll.Values[k++].ToString(CultureInfo.InvariantCulture) : "#"));
            }
            string detail = item.Notes.Length > 0 ? item.Notes : string.Join("\n", mods);
            Rows.Add(new(item.Id, name, summary, detail));
        }
        SelectedRow = Rows.FirstOrDefault(r => r.Id == selected);
        Raise(nameof(SocketCanExecute));
    }

    private static string RarityLabel(string rarity) => rarity switch
    {
        "magic" => "RarityMagic", "unique" => "RarityUnique", "normal" => "RarityNormal", _ => "RarityRare"
    };

    private void RefreshSockets()
    {
        var freeSel = SelectedFreeSocket?.NodeId; var filledSel = SelectedFilledSocket?.NodeId;
        FreeSockets.Clear(); FilledSockets.Clear();
        if (_tree is null) { SelectedFreeSocket = null; SelectedFilledSocket = null; return; }
        foreach (var (nodeId, label, jewelId) in _tree.JewelSockets)
        {
            if (jewelId is null) FreeSockets.Add(new(nodeId, label, null));
            else
            {
                var item = _plan.Items.FirstOrDefault(i => i.Id == jewelId);
                FilledSockets.Add(new(nodeId, label + " → " + (item is null || item.Name.Length == 0 ? L["JewelUnnamed"] : item.Name), jewelId));
            }
        }
        SelectedFreeSocket = FreeSockets.FirstOrDefault(s => s.NodeId == freeSel);
        SelectedFilledSocket = FilledSockets.FirstOrDefault(s => s.NodeId == filledSel);
        Raise(nameof(SocketCanExecute)); Raise(nameof(UnsocketCanExecute));
    }

    private void Create()
    {
        if (Catalog is null || _editor is null) return;
        var draft = new JewelDraftViewModel(L, Catalog);
        var window = new JewelEditorWindow(draft) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        if (!draft.Accepted) return;
        try
        {
            var next = EquipmentRules.Put(Catalog, _plan, draft.ToItem(), null);
            _plan = next; _editor.SetEquipment(next);
            Status = "";
        }
        catch (PlanningException e) { Status = L[e.Code]; return; }
        catch (Exception e) { ErrorLog.Append(e, "Jewels:Put"); Status = L["Error"] + ": " + e.Message; return; }
        RefreshAll();
    }
}

/// <summary>Compact draft for a hand-made jewel: rarity, name (optional), level, quality and
/// affixes from the pinned jewel pool. Uniques pick a pinned unique name; the catalog carries
/// no unique modifiers, so their text is entered by hand (disclosed in the window).</summary>
public sealed class JewelDraftViewModel : Observable
{
    public Localization L { get; }
    private readonly GameCatalog _catalog;
    private string _rarity = "magic", _name = "", _level = "80", _quality = "0", _affixSearch = "", _uniqueSearch = "", _error = "", _notes = "";
    private ItemMod? _selectedAffix;
    private bool _accepted, _dirty;

    public JewelDraftViewModel(Localization l, GameCatalog catalog) { L = l; _catalog = catalog; }

    public ObservableCollection<ModDraft> Mods { get; } = [];
    public bool Accepted { get => _accepted; private set => Set(ref _accepted, value); }
    public bool IsDirty { get => _dirty; private set => Set(ref _dirty, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string Notes { get => _notes; set { if (Set(ref _notes, value)) Touch(); } }

    public IReadOnlyList<NamedOption> Rarities { get; } = [new("magic", "Magic"), new("rare", "Rare"), new("unique", "Unique")];
    public string RarityText { get => _rarity; set { if (Set(ref _rarity, value)) { Touch(); Raise(nameof(AvailableAffixes)); } } }
    public string Name { get => _name; set { if (Set(ref _name, value)) Touch(); } }
    public string ItemLevel { get => _level; set { if (Set(ref _level, value)) Touch(); } }
    public string Quality { get => _quality; set { if (Set(ref _quality, value)) Touch(); } }
    public string AffixSearch { get => _affixSearch; set { if (Set(ref _affixSearch, value)) Raise(nameof(AvailableAffixes)); } }
    public string UniqueSearch { get => _uniqueSearch; set { if (Set(ref _uniqueSearch, value)) Raise(nameof(UniqueNames)); } }
    public ItemMod? SelectedAffix { get => _selectedAffix; set => Set(ref _selectedAffix, value); }

    public IEnumerable<string> UniqueNames => _catalog.Uniques.Values
        .Where(u => u.ItemClass.Equals("Jewel", StringComparison.OrdinalIgnoreCase) && (UniqueSearch.Length == 0 || u.Name.Contains(UniqueSearch, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(u => u.Name).Select(u => u.Name).Take(200);
    private string? _selectedUnique;
    public string? SelectedUnique
    {
        get => _selectedUnique;
        set
        {
            if (!Set(ref _selectedUnique, value) || value is null) return;
            RarityText = "unique"; Name = value;
            if (Notes.Length == 0) Notes = L["UniqueNotesHint"];
        }
    }

    public IEnumerable<ItemMod> AvailableAffixes
    {
        get
        {
            int cap = RarityText == "magic" ? 1 : 3;
            var kinds = new Dictionary<string, int>();
            foreach (var m in Mods) kinds[m.Definition.Kind] = kinds.GetValueOrDefault(m.Definition.Kind) + 1;
            var groups = Mods.SelectMany(m => m.Definition.Groups).ToHashSet();
            return _catalog.JewelMods
                .Where(m => !m.Groups.Any(groups.Contains) && kinds.GetValueOrDefault(m.Kind) < cap && (AffixSearch.Length == 0 || m.Text.Contains(AffixSearch, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(m => m.Text).Take(200);
        }
    }

    public ICommand AddAffixCommand => new ActionCommand(_ =>
    {
        if (SelectedAffix is null) return;
        Mods.Add(new ModDraft(SelectedAffix, SelectedAffix.Stats.Select(s => s.Max).ToArray(), () => Touch(), m => { Mods.Remove(m); Touch(); Raise(nameof(AvailableAffixes)); }));
        SelectedAffix = null; Touch(); Raise(nameof(AvailableAffixes));
    }, () => SelectedAffix is not null);
    public ICommand SaveCommand => new ActionCommand(_ => Save(), () => true);
    public event Action? Saved;

    private void Touch() { IsDirty = true; }
    public GearItem ToItem() => new()
    {
        Name = _name.Trim(),
        Rarity = _rarity,
        ItemLevel = int.TryParse(_level, out int il) ? Math.Clamp(il, 1, 100) : 1,
        Quality = int.TryParse(_quality, out int q) ? Math.Clamp(q, 0, 20) : 0,
        Mods = [.. Mods.Select(m => m.ToRoll())],
        Notes = _notes
    };
    private void Save()
    {
        try { EquipmentRules.ValidateItem(_catalog, ToItem()); Error = ""; }
        catch (PlanningException e) { Error = L[e.Code]; return; }
        catch (Exception e) { Error = e.Message; return; }
        Accepted = true; Saved?.Invoke();
    }
}
