using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PoeBuilder.App.Views;
using Localization = PoeBuilder.App.Services.Localization;
using PoeBuilder.App.Services;
using PoeBuilder.Core.Calculation;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Models;

namespace PoeBuilder.App.ViewModels;

public sealed record JewelRow(Guid Id, string Name, string Summary, string Detail, ImageSource? Icon);
public sealed record JewelSocketChoice(int NodeId, string Label, Guid? JewelId) { public override string ToString() => Label; }
public sealed record JewelModOption(string Text, string Details, ItemMod? Affix, UniqueTextLine? UniqueLine);

public sealed class JewelUniqueModDraft
{
    public string Text { get; }
    public string Details { get; }
    public string Template { get; }
    public ICommand RemoveCommand { get; }

    public JewelUniqueModDraft(UniqueTextLine line, string details, Action<JewelUniqueModDraft> remove)
    {
        Text = line.Resolved;
        Details = details;
        Template = line.Text;
        RemoveCommand = new ActionCommand(_ => remove(this));
    }
}

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
    /// <summary>Display language for jewel names and mod lines. Null keeps everything English.</summary>
    public GameLocale? Locale { get; set; }
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
            // A radius jewel that changes allocation (From Nothing / Intuitive Leap) hands its rule to the
            // tree plan: its socket then reaches the nodes inside that radius with no edge at all, exactly
            // like PoB2's jewelData does.
            var item = _plan.Items.FirstOrDefault(i => i.Id == SelectedRow.Id);
            var rule = JewelRadius.AllocationRule(item?.Notes);
            if (!_tree.SocketJewel(SelectedRow.Id, SelectedFreeSocket.NodeId, rule)) { Status = L["JewelSocketFailed"]; return; }
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
        // A jewel that carries its base (every jewel the editor creates now) is told apart by that base's
        // item class; a baseless magic/rare item is a jewel by the validation law (imports from PoB2 keep
        // that shape), and a baseless unique jewel by its own pinned identity.
        if (i.BaseId.Length != 0)
            return Catalog is not null && Catalog.Bases.TryGetValue(i.BaseId, out var based) && GameCatalog.IsJewel(based);
        if (i.Rarity is "magic" or "rare") return true;
        if (i.Rarity == "unique" && Catalog is not null && Catalog.Uniques.TryGetValue(i.Name, out var u))
            return u.ItemClass.Equals("Jewel", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private void RefreshAll() { RefreshRows(); RefreshSockets(); }

    /// <summary>The radius band (1-based, PoB2's <c>jewelRadiusIndex</c>) of the jewel socketed in a tree
    /// socket, read from that jewel's own text; 0 when the socket is empty or the jewel names no band.</summary>
    public int SocketBand(int nodeId)
    {
        if (Catalog is null || _tree is null) return 0;
        var socket = _tree.JewelSockets.FirstOrDefault(s => s.NodeId == nodeId);
        if (socket.JewelId is not Guid jewelId) return 0;
        var item = _plan.Items.FirstOrDefault(i => i.Id == jewelId);
        if (item is null) return 0;
        return JewelRadius.IndexForItemText(item.Notes);
    }

    /// <summary>The picture of the jewel socketed in a tree socket, so the tree can draw the jewel's real
    /// icon inside the socket (the same art the Jewels tab's list shows). Null when the socket is empty or
    /// no verified artwork is known for the item.</summary>
    public ImageSource? SocketIcon(int nodeId)
    {
        if (Catalog is null || _tree is null) return null;
        var socket = _tree.JewelSockets.FirstOrDefault(s => s.NodeId == nodeId);
        if (socket.JewelId is not Guid jewelId) return null;
        var item = _plan.Items.FirstOrDefault(i => i.Id == jewelId);
        if (item is null) return null;
        return IconService.Instance.ForItem(Catalog, item, Catalog.Bases.GetValueOrDefault(item.BaseId));
    }

    /// <summary>Tooltip text for a jewel-socket node on the tree: the socketed jewel's name and its
    /// affixes, or the honest "socket is empty" note. Radius affixes are flagged, because the
    /// counted-radius model (nodes inside the jewel's radius) is still pending.</summary>
    public string? SocketInfo(int nodeId)
    {
        if (Catalog is null || _tree is null) return null;
        var socket = _tree.JewelSockets.FirstOrDefault(s => s.NodeId == nodeId);
        if (socket.JewelId is not Guid jewelId) return L["TreeSocketEmpty"];
        var item = _plan.Items.FirstOrDefault(i => i.Id == jewelId);
        if (item is null) return L["TreeSocketEmpty"];
        var lines = new List<string> { item.Name.Length > 0 ? item.Name : L["JewelUnnamed"] };
        bool radius = false;
        foreach (var roll in item.Mods)
        {
            var m = Catalog.JewelMods.FirstOrDefault(x => x.Id == roll.Id);
            if (m is null) continue;
            int k = 0;
            string text = System.Text.RegularExpressions.Regex.Replace(m.Text, "#",
                _ => k < roll.Values.Length ? roll.Values[k++].ToString(CultureInfo.InvariantCulture) : "#");
            if (roll.Id.StartsWith("JewelRadius", StringComparison.Ordinal)) { radius = true; text += " " + L["TreeSocketRadiusFlag"]; }
            lines.Add(text);
        }
        if (item.Notes.Length > 0 && lines.Count == 1) lines.Add(item.Notes);
        if (radius) lines.Add(L["TreeSocketRadiusNote"]);
        // A jewel that changes ALLOCATION says so in its own words, so the tooltip repeats the rule PoB2
        // stores as jewelData.fromNothingKeystone / intuitiveLeapLike.
        if (JewelRadius.AllocationRule(item.Notes) is { } rule)
            lines.Add(rule.FromKeystone ? L.Format("TreeRadiusRuleKeystone", rule.KeystoneName) : L["TreeRadiusRuleSocket"]);
        return string.Join("\n", lines);
    }

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
            ImageSource? icon = IconService.Instance.ForItem(Catalog, item, Catalog.Bases.GetValueOrDefault(item.BaseId));
            Rows.Add(new(item.Id, name, summary, detail, icon));
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

/// <summary>Compact draft for a hand-made jewel: its base or unique identity, rarity, level, quality and
/// base-specific affixes. Unique jewels use their own pinned modifier lines and artwork.</summary>
public sealed class JewelDraftViewModel : Observable
{
    public Localization L { get; }
    private readonly GameCatalog _catalog;
    private string _rarity = "magic", _name = "", _level = "80", _quality = "0", _affixSearch = "", _jewelSearch = "", _error = "", _notes = "";
    private JewelModOption? _selectedAffix;
    private bool _accepted, _dirty;
    private UniqueData? _uniqueData;
    private readonly HashSet<string> _uniquePresentTemplates = new(StringComparer.Ordinal);
    private ItemChoice? _selectedJewel;

    public JewelDraftViewModel(Localization l, GameCatalog catalog)
    {
        L = l; _catalog = catalog;
        // A jewel always has a base in game. Diamond is the one that accepts every attribute's affixes,
        // so it is what an unnamed jewel starts as; any other base is one click away.
        _selectedBase = _catalog.JewelBases.FirstOrDefault(b => b.Name.Equals("Diamond", StringComparison.OrdinalIgnoreCase))
            ?? _catalog.JewelBases.FirstOrDefault();
        _selectedJewel = JewelChoices.FirstOrDefault(choice => choice.Base?.Id == _selectedBase?.Id);
    }

    public ObservableCollection<ModDraft> Mods { get; } = [];
    public ObservableCollection<JewelUniqueModDraft> UniqueMods { get; } = [];
    public bool Accepted { get => _accepted; private set => Set(ref _accepted, value); }
    public bool IsDirty { get => _dirty; private set => Set(ref _dirty, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string Notes { get => _notes; set { if (Set(ref _notes, value)) Touch(); } }

    public IReadOnlyList<NamedOption> Rarities { get; } = [new("magic", "Magic"), new("rare", "Rare"), new("unique", "Unique")];
    public string RarityText
    {
        get => _rarity;
        set
        {
            if (_uniqueData is not null && value != "unique") return;
            if (Set(ref _rarity, value)) { Touch(); Raise(nameof(AvailableMods)); }
        }
    }
    public string Name { get => _name; set { if (Set(ref _name, value)) Touch(); } }
    public string ItemLevel { get => _level; set { if (Set(ref _level, value)) { Touch(); Raise(nameof(AvailableMods)); } } }
    public string Quality { get => _quality; set { if (Set(ref _quality, value)) Touch(); } }
    public string AffixSearch { get => _affixSearch; set { if (Set(ref _affixSearch, value)) Raise(nameof(AvailableMods)); } }
    public string JewelSearch { get => _jewelSearch; set { if (Set(ref _jewelSearch, value)) Raise(nameof(JewelChoices)); } }
    /// <summary>Display language for jewel and mod names. Null keeps everything English.</summary>
    public GameLocale? Locale { get; set; }
    private string GameName(string? english) => Locale?.Name(english) ?? english ?? "";
    // Search always covers both languages; a query typed in the game's other language still finds the row.
    private bool Matches(params string?[] texts)
    {
        if (JewelSearch.Length == 0) return true;
        if (Locale is null)
            return texts.Any(t => !string.IsNullOrWhiteSpace(t) && t.Contains(JewelSearch, StringComparison.OrdinalIgnoreCase));
        Locale.Search = JewelSearch;
        return Locale.Matches(texts);
    }
    public JewelModOption? SelectedAffix { get => _selectedAffix; set => Set(ref _selectedAffix, value); }

    // --- One list of jewel bases and unique jewels, each with its own artwork.
    private ItemBase? _selectedBase;
    public IEnumerable<ItemChoice> JewelChoices => _catalog.JewelBases
        .Where(b => JewelSearch.Length == 0 || Matches(b.Name, b.Tags.FirstOrDefault("")))
        .Select(b => new ItemChoice(GameName(b.Name), b.ItemClass, b, null, IconService.Instance.ForBase(b)))
        .Concat(_catalog.Uniques.Values
            .Where(u => u.ItemClass.Equals("Jewel", StringComparison.OrdinalIgnoreCase) && Matches(u.Name))
            .Select(u => new ItemChoice(GameName(u.Name), u.ItemClass, null, u, IconService.Instance.ForUnique(_catalog, u.Name))))
        .OrderBy(choice => choice.Name);
    public ItemChoice? SelectedJewel
    {
        get => _selectedJewel;
        set
        {
            if (value is null || (value.Base?.Id == _selectedBase?.Id && value.Unique?.Name == _uniqueData?.Name)) return;
            _selectedJewel = value;
            Mods.Clear();
            UniqueMods.Clear();
            _uniquePresentTemplates.Clear();
            if (value.Unique is { } unique)
            {
                _selectedBase = null;
                _rarity = "unique";
                _name = unique.Name;
                Raise(nameof(RarityText)); Raise(nameof(Name));
                LoadUnique(unique.Name);
            }
            else if (value.Base is { } picked)
            {
                bool wasUnique = _uniqueData is not null || _rarity == "unique";
                _uniqueData = null;
                _selectedBase = picked;
                if (wasUnique)
                {
                    _rarity = "magic";
                    _name = "";
                    _notes = "";
                    Raise(nameof(RarityText)); Raise(nameof(Name)); Raise(nameof(Notes));
                }
            }
            Touch();
            Raise(nameof(SelectedJewel)); Raise(nameof(ItemIcon)); Raise(nameof(AvailableMods));
        }
    }
    /// <summary>The jewel's picture: its base's art, or the unique's own jewel art by name.</summary>
    public ImageSource? ItemIcon => _selectedBase is not null ? IconService.Instance.ForBase(_selectedBase)
        : _uniqueData is not null ? IconService.Instance.ForUnique(_catalog, _uniqueData.Name) : null;

    public IEnumerable<JewelModOption> AvailableMods
    {
        get
        {
            if (_uniqueData is not null)
                return UniqueItemText.PersonalMods(_uniqueData, _uniquePresentTemplates)
                    .Where(line => AffixSearch.Length == 0 || line.Resolved.Contains(AffixSearch, StringComparison.OrdinalIgnoreCase))
                    .Select(line => new JewelModOption(line.Resolved,
                        UniqueItemText.VariantLabel(_uniqueData, line) is { Length: > 0 } variant
                            ? L.Format("JewelUniqueVariant", variant) : L["JewelUniqueModifier"],
                        null, line));
            if (RarityText == "unique") return [];
            var (prefixCap, suffixCap) = Caps;
            int prefixes = Mods.Count(m => m.Definition.Kind == "prefix"), suffixes = Mods.Count(m => m.Definition.Kind == "suffix");
            var groups = Mods.SelectMany(m => m.Definition.Groups).ToHashSet();
            // With a base selected the pool is that base's own (spawn tags of the pinned affix table, which
            // is where jewel affixes carry their kind and level). Without one the catalog's whole jewel pool
            // answers, exactly as an imported baseless jewel needs.
            var pool = _selectedBase is not null && int.TryParse(ItemLevel, out int level)
                ? _catalog.ModsFor(_selectedBase, level)
                : _catalog.JewelMods;
            return pool
                .Where(m => (m.Groups.Length == 0 || !m.Groups.Any(groups.Contains))
                    && (m.Kind == "prefix" ? prefixes < prefixCap : m.Kind == "suffix" ? suffixes < suffixCap : false)
                    && (AffixSearch.Length == 0 || m.DisplayName.Contains(AffixSearch, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(m => m.Kind).ThenBy(m => m.Level).ThenBy(m => m.Text)
                .Select(m => new JewelModOption(m.Text, $"{m.Kind} · ilvl {m.Level} · {m.Name}", m, null));
        }
    }
    /// <summary>Affix room of this jewel: magic takes one of each kind, rare what the base's own class rule
    /// allows (PoB2's jewel rule — 2 prefixes and 2 suffixes).</summary>
    private (int Prefixes, int Suffixes) Caps => RarityText == "magic" ? (1, 1)
        : _selectedBase is null ? (2, 2) : EquipmentRules.AffixCaps(_selectedBase);

    public ICommand AddAffixCommand => new ActionCommand(_ =>
    {
        if (SelectedAffix?.Affix is { } affix && _uniqueData is null)
        {
            Mods.Add(new ModDraft(affix, affix.Stats.Select(s => s.Max).ToArray(), () => Touch(), m => { Mods.Remove(m); Touch(); Raise(nameof(AvailableMods)); }));
        }
        else if (SelectedAffix?.UniqueLine is { } uniqueLine && _uniqueData is not null)
        {
            var line = uniqueLine with { Kind = UniqueLineKind.Modifier };
            _uniquePresentTemplates.Add(line.Text);
            UniqueMods.Add(new(line, SelectedAffix.Details, RemoveUniqueMod));
            RebuildUniqueNotes();
        }
        else return;
        SelectedAffix = null; Touch(); Raise(nameof(AvailableMods));
    }, () => SelectedAffix is not null);
    public ICommand SaveCommand => new ActionCommand(_ => Save(), () => true);
    public event Action? Saved;

    private void LoadUnique(string name)
    {
        _uniqueData = _catalog.UniqueData.For(name);
        UniqueMods.Clear();
        _uniquePresentTemplates.Clear();
        if (_uniqueData is null)
        {
            _notes = L["UniqueNotesHint"];
            Raise(nameof(Notes));
            Raise(nameof(AvailableMods));
            return;
        }
        int variant = UniqueItemText.CurrentVariant(_uniqueData);
        foreach (var line in UniqueItemText.Lines(_uniqueData, variant))
        {
            if (line.Kind is not (UniqueLineKind.Implicit or UniqueLineKind.Modifier)) continue;
            _uniquePresentTemplates.Add(line.Text);
            UniqueMods.Add(new(line, UniqueItemText.VariantLabel(_uniqueData, line), RemoveUniqueMod));
        }
        RebuildUniqueNotes();
        Raise(nameof(AvailableMods));
    }

    private void RemoveUniqueMod(JewelUniqueModDraft mod)
    {
        if (!UniqueMods.Remove(mod)) return;
        _uniquePresentTemplates.Remove(mod.Template);
        RebuildUniqueNotes();
        Touch();
        Raise(nameof(AvailableMods));
    }

    private void RebuildUniqueNotes()
    {
        if (_uniqueData is null) return;
        var lines = new List<string> { UniqueItemText.RarityHeader, _uniqueData.Name, _uniqueData.BaseType };
        int implicits = UniqueMods.Count(mod => UniqueItemText.Lines(_uniqueData, UniqueItemText.CurrentVariant(_uniqueData))
            .Any(line => line.Kind == UniqueLineKind.Implicit && line.Text == mod.Template));
        if (implicits > 0) lines.Add("Implicits: " + implicits);
        lines.AddRange(UniqueMods.Select(mod => mod.Text));
        _notes = string.Join('\n', lines);
        Raise(nameof(Notes));
    }

    private void Touch() { IsDirty = true; Raise(nameof(ItemIcon)); }
    public GearItem ToItem() => new()
    {
        BaseId = _selectedBase?.Id ?? "",
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
