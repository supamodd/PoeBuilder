using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PoeBuilder.App.Services;
using PoeBuilder.Core.Tree;
using Localization = PoeBuilder.App.Services.Localization;

namespace PoeBuilder.App.ViewModels;

public sealed record AscendancyChoice(string? Id, string Name) { public override string ToString() => Name; }

public sealed record TreeSearchResult(int Id, string Name, string Summary);

public sealed class TreeViewModel : Observable
{
    public Localization L { get; }
    private readonly TreeViewModel? _owner;
    public TreeViewModel? AscendancyTree { get; }
    private bool _showAscendancy, _synchronizing;
    private int _choicesClass = -1;
    public ObservableCollection<AscendancyChoice> AscendancyChoices { get; } = [];
    public bool HasAscendancy => Catalog?.Ascendancies.Any(a => a.Id == _plan.Ascendancy?.Id && a.ClassIndex == _plan.ClassIndex) == true;
    public TreeViewModel DisplayedTree => _showAscendancy && HasAscendancy ? AscendancyTree! : this;
    public string ViewTitle => _showAscendancy && HasAscendancy ? SelectedAscendancy?.Name ?? "" : L["MainTree"];
    public string PortraitKey => Catalog?.PortraitKey ?? (HasAscendancy ? _plan.Ascendancy!.Id : SelectedClass?.Name ?? "Warrior");
    public AscendancyChoice? SelectedAscendancy
    {
        get => AscendancyChoices.FirstOrDefault(c => c.Id == _plan.Ascendancy?.Id);
        set
        {
            if (_synchronizing || value is null || value.Id == _plan.Ascendancy?.Id || !CanModify) return;
            if (_plan.Ascendancy is not null && !Confirm(L["AscChangeQuestion"]))
            { _ = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => Raise(nameof(SelectedAscendancy)))); return; }
            Run(() => { Apply(AscendancyRules.Select(Catalog!, _plan, value.Id)); _showAscendancy = value.Id is not null; Notify(); });
        }
    }
    public ICommand ShowMainTreeCommand { get; }
    public ICommand ShowAscendancyCommand { get; }
    // Jewel-granted "Allocates" nodes are already paid for by the jewel and must not consume tree points.
    private int Spent => _engine?.Spent(_plan) ?? 0;
    private int PreviewCost => _engine?.Cost(Preview) ?? 0;
    public TreeCatalog? Catalog { get; private set; }
    private PassiveTreeEngine? _engine;
    private BuildEditor? _editor;
    private PassiveTreePlan _plan = new();
    private readonly TreeHistory _history = new();
    private readonly DispatcherTimer _searchTimer;
    private int? _selectedId;
    private string _search = "", _pointLimitText = "0", _validationCode = "", _loadError = "", _message = "";
    private int _matchCount;
    private PassiveVariant? _defaultAttribute;
    private IReadOnlyList<PassiveVariant> _attributeChoices = [];
    public event Action? StateChanged;
    public event Action<int>? FocusRequested;
    public PassiveTreePlan Plan => _plan.Copy();
    public int? SelectedId => _selectedId;
    public HashSet<int> Allocated { get; private set; } = [];
    public HashSet<int> Preview { get; private set; } = [];
    public HashSet<int> SearchMatches { get; private set; } = [];
    /// <summary>PoB2-style per-node impact line shown in the hover tooltip. Wired by the shell to the
    /// character calculator; returns null when the node cannot compute a meaningful contribution
    /// (already allocated, unsupported, or no build/open sheet).</summary>
    public Func<int, string?>? NodeImpactProvider { get; set; }
    public bool IsMainView => _owner is null;
    public bool IsReady => Catalog is not null;
    public bool CanModify => (_owner?.CanModify ?? (_editor is not null)) && IsReady && _validationCode.Length == 0;
    public bool CanReset => (_owner?.CanReset ?? (_editor is not null)) && IsReady;
    public bool HasAttribute => _selectedId is int id && Allocated.Contains(id) && Catalog?.Nodes[id].IsAttribute == true;
    /// <summary>Weapon-set allocations of this plan (node id → 1 or 2). PoB2 colours them red (set I) and
    /// green (set II) on its own tree, and a node allocated for the other set contributes nothing while the
    /// character holds the current one.</summary>
    public IReadOnlyDictionary<int, int> WeaponSetNodes => _plan.WeaponSetNodes;
    /// <summary>The weapon set the character is currently computed with: PoB2's active item set
    /// (<c>useSecondWeaponSet</c>), which the importer stored on the equipment plan.</summary>
    public int ActiveWeaponSet => _owner?.ActiveWeaponSet ?? (_editor?.EquipmentSnapshot?.WeaponSet ?? 1);
    /// <summary>True when the selected node is allocated and can therefore be moved between weapon sets.</summary>
    public bool HasSelectedAllocated => IsMainView && _selectedId is int id && Allocated.Contains(id) && Catalog?.Nodes[id].IsStart != true;
    /// <summary>Whether the floating node panel (search, the selected node, the weapon-set buttons) is on
    /// screen. The tree fills the whole workspace now, so the panel is a card over it and can be folded away
    /// to see the branches behind it.</summary>
    private bool _showNodePanel = true;
    public bool ShowNodePanel { get => _showNodePanel; private set { if (Set(ref _showNodePanel, value)) Raise(nameof(ShowNodePanel)); } }
    public ICommand TogglePanelCommand { get; private set; } = null!;
    /// <summary>Current weapon-set assignment of the selected node, as the UI prints it.</summary>
    public string SelectedWeaponSetText
    {
        get
        {
            if (_selectedId is not int id) return "";
            if (!_plan.WeaponSetNodes.TryGetValue(id, out int set)) return L["WeaponSetBoth"];
            return L["WeaponSet" + set];
        }
    }
    /// <summary>PoB2-style note for a node's tooltip: which weapon set it belongs to, and whether that set is
    /// the one currently in hand.</summary>
    public string WeaponSetNote(int nodeId) =>
        _plan.WeaponSetNodes.TryGetValue(nodeId, out int set)
            ? L.Format(set == ActiveWeaponSet ? "WeaponSetNodeActive" : "WeaponSetNodeInactive", L["WeaponSet" + set])
            : "";
    /// <summary>Moves the selected node into a weapon set (1 or 2) or back to "both" (any other value),
    /// exactly like PoB2's allocation mode lives on the node.</summary>
    public void SetWeaponSet(int? set)
    {
        if (!CanModify || _selectedId is not int id || !Allocated.Contains(id)) return;
        var nodes = new Dictionary<int, int>(_plan.WeaponSetNodes);
        if (set is 1 or 2) nodes[id] = set.Value; else nodes.Remove(id);
        Run(() => Apply(_plan with { WeaponSetNodes = nodes }));
    }
    public string LoadError => _loadError;
    public string Warning => _loadError.Length > 0 ? _loadError : _validationCode.Length > 0 ? L[_validationCode] : !(_owner?.CanReset ?? (_editor is not null)) ? L["TreeBrowseOnly"] : "";
    /// <summary>The warnings the tree reports, with a sentence the plan and the displayed graph both carry
    /// listed once — "browse mode" and a plan the pinned data refuses arrived from both, and printing the same
    /// line twice under the tree told nobody anything.</summary>
    public string WarningText
    {
        get
        {
            var lines = new List<string>();
            foreach (string text in new[] { Warning, DisplayedTree.Warning })
                if (text.Length > 0 && !lines.Contains(text)) lines.Add(text);
            return string.Join("\n", lines);
        }
    }
    public string DatasetLabel => "GGG 0.5.5 · EN · bd87e651";
    public IReadOnlyList<TreeClass> Classes => Catalog?.Classes ?? [];
    public IReadOnlyList<PassiveVariant> AttributeChoices => _attributeChoices;
    public ObservableCollection<TreeSearchResult> SearchResults { get; } = [];
    private TreeSearchResult? _selectedResult;
    public TreeSearchResult? SelectedResult
    {
        get => _selectedResult;
        set { if (Set(ref _selectedResult, value) && value is not null) { Select(value.Id); FocusRequested?.Invoke(value.Id); } }
    }
    public TreeClass? SelectedClass
    {
        get => Classes.FirstOrDefault(c => c.Index == _plan.ClassIndex);
        set
        {
            if (value is null || value.Index == _plan.ClassIndex || !CanModify) return;
            if ((Allocated.Count > 0 || _plan.Ascendancy is not null) && !Confirm(L["TreeChangeClassQuestion"]))
            {
                // Revert AFTER WPF's two-way SelectedItem source update has completed.
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => Raise(nameof(SelectedClass)))); return;
            }
            Apply(new() { DatasetId = Catalog!.DatasetId, ClassIndex = value.Index, PointLimit = _plan.PointLimit });
            Select(value.StartNodeId); FocusRequested?.Invoke(value.StartNodeId);
        }
    }
    public PassiveVariant? DefaultAttribute { get => _defaultAttribute; set { if (value is not null) Set(ref _defaultAttribute, value); } }
    public PassiveVariant? SelectedAttribute
    {
        get => _selectedId is int id && _plan.AttributeSelections.TryGetValue(id, out int choice) && Catalog!.Variants.TryGetValue(choice, out var value) ? value : null;
        set
        {
            if (!CanModify || !HasAttribute || value is null || value.Id == SelectedAttribute?.Id) return;
            Run(() => Apply(_engine!.SetAttribute(_plan, _selectedId!.Value, value.Id)));
        }
    }
    public string Search { get => _search; set { if (Set(ref _search, value)) { _searchTimer.Stop(); _searchTimer.Start(); } } }
    public string SearchCount => L.Format("TreeSearchCount", _matchCount, SearchResults.Count);
    public string PointLimitText { get => _pointLimitText; set => Set(ref _pointLimitText, value); }
    public string PointSummary => _plan.PointLimit == 0 ? L.Format("TreePoints", Spent) : L.Format("TreePointsLimited", Spent, _plan.PointLimit);
    public string Message => _message;
    public string SelectedName => _selectedId is int id && Catalog is not null ? Catalog.Describe(id, _plan).Name : L["TreeSelectNode"];
    public string SelectedStats => _selectedId is int id && Catalog is not null ? string.Join("\n", Catalog.Describe(id, _plan).Stats) : "";
    public string SelectedInfo
    {
        get
        {
            if (_selectedId is not int id || Catalog is null) return L["TreeInspectHint"];
            var node = Catalog.Nodes[id];
            if (!node.IsSupported || (node.IsStart && SelectedClass?.StartNodeId != id)) return L["TreeUnsupported"];
            if (node.IsStart) return L["TreeStartInfo"];
            if (Allocated.Contains(id))
            {
                string info = _plan.JewelAllocatedNodes.Contains(id) ? L["TreeJewelGranted"] : node.IsJewel ? L["TreeJewelInfo"] : L["TreeAllocated"];
                // The weapon set a node belongs to is part of its state, exactly like PoB2 prints it in the
                // node tooltip: a node of the other set stays allocated but contributes nothing right now.
                string weaponSet = WeaponSetNote(id);
                return weaponSet.Length == 0 ? info : info + "\n" + weaponSet;
            }
            if (_validationCode.Length > 0) return L[_validationCode];
            try { int cost = _engine!.Cost(_engine.FindPath(_plan, id)); return L.Format("TreePathCost", cost) + (_plan.PointLimit > 0 && cost + Spent > _plan.PointLimit ? "\n" + L["TreeOverBudget"] : ""); }
            catch (TreeRuleException e) { return L[e.Code]; }
        }
    }
    public ICommand AllocateCommand { get; }
    public ICommand RefundCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand ResetPlanCommand { get; }
    public ICommand ApplyLimitCommand { get; }
    public ICommand FocusClassCommand { get; }
    /// <summary>Weapon-set assignment of the selected node (PoB2's allocation mode): both sets, set I or II.
    /// Set I is drawn red and set II green on the tree, exactly like PoB2's own colours.</summary>
    public ICommand SetWeaponSetBothCommand { get; }
    public ICommand SetWeaponSetOneCommand { get; }
    public ICommand SetWeaponSetTwoCommand { get; }

    public TreeViewModel(Localization localization, TreeViewModel? owner = null)
    {
        L = localization; _owner = owner;
        if (owner is null) AscendancyTree = new TreeViewModel(localization, this);
        ShowMainTreeCommand = new ActionCommand(_ => { _showAscendancy = false; Notify(); });
        ShowAscendancyCommand = new ActionCommand(_ => { _showAscendancy = true; Notify(); }, () => HasAscendancy);
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RefreshSearch(); };
        L.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Localization.Language)) { _message = ""; _choicesClass = -1; SyncAscendancy(); RefreshSearch(); Notify(); } };
        AllocateCommand = new ActionCommand(_ => Run(() =>
        {
            if (_selectedId is not int id) return;
            var next = _engine!.Allocate(_plan, id, DefaultAttribute?.Id ?? 26297);
            Apply(next); _message = L["TreeAllocated"]; Notify();
        }), () => CanModify && Preview.Count > 0 && (_plan.PointLimit == 0 || Spent + PreviewCost <= _plan.PointLimit));
        RefundCommand = new ActionCommand(_ => Run(() =>
        {
            int target = _selectedId!.Value; var removed = _engine!.RefundSet(_plan, target);
            if (removed.Length > 1 && !Confirm(L.Format("TreeRefundQuestion", removed.Length))) return;
            Apply(_engine.Refund(_plan, target));
        }), () => CanModify && _selectedId is int id && Allocated.Contains(id));
        UndoCommand = new ActionCommand(_ => { if (_owner is null) Restore(_history.Undo(_plan)); else _owner.UndoCommand.Execute(null); }, () => _owner?.UndoCommand.CanExecute(null) ?? (CanReset && _history.CanUndo));
        RedoCommand = new ActionCommand(_ => { if (_owner is null) Restore(_history.Redo(_plan)); else _owner.RedoCommand.Execute(null); }, () => _owner?.RedoCommand.CanExecute(null) ?? (CanReset && _history.CanRedo));
        ResetPlanCommand = new ActionCommand(_ =>
        {
            if (!Confirm(L["TreeResetQuestion"])) return;
            Apply(new() { DatasetId = Catalog!.DatasetId, ClassIndex = SelectedClass?.Index ?? 6 });
        }, () => CanReset);
        ApplyLimitCommand = new ActionCommand(_ => Run(() =>
        {
            if (!int.TryParse(PointLimitText, out int limit) || limit is < 0 or > 10000) throw new TreeRuleException("TreeInvalidLimit");
            var next = _plan with { PointLimit = limit }; _engine!.Validate(next);
            if (limit != _plan.PointLimit) Apply(next);
        }), () => CanModify);
        FocusClassCommand = new ActionCommand(_ => { if (SelectedClass is not null) FocusRequested?.Invoke(SelectedClass.StartNodeId); }, () => SelectedClass is not null);
        TogglePanelCommand = new ActionCommand(_ => ShowNodePanel = !ShowNodePanel);
        SetWeaponSetBothCommand = new ActionCommand(_ => SetWeaponSet(null), () => CanModify && HasSelectedAllocated);
        SetWeaponSetOneCommand = new ActionCommand(_ => SetWeaponSet(1), () => CanModify && HasSelectedAllocated);
        SetWeaponSetTwoCommand = new ActionCommand(_ => SetWeaponSet(2), () => CanModify && HasSelectedAllocated);
    }
    public async Task InitializeAsync()
    {
        try
        {
            Catalog = await Task.Run(() => TreeCatalog.LoadPinned(Path.Combine(AppContext.BaseDirectory, "Data", "Tree", "data.json")));
            _choicesClass = -1; _engine = new(Catalog); _attributeChoices = Catalog.AttributeChoices.ToArray(); _defaultAttribute = Catalog.Variants[26297];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        { _loadError = L["TreeLoadFailed"] + "\n" + e.Message; }
        BindEditor(_editor);
    }
    public void ReportAtlasError(string detail) { _loadError = L["TreeAtlasFailed"] + "\n" + detail; Raise(nameof(Warning)); }
    public void BindEditor(BuildEditor? editor)
    {
        _showAscendancy = false; _editor = editor; _history.Clear(); _selectedId = null; _message = "";
        _plan = Adopt(editor?.TreeSnapshot ?? new()); _pointLimitText = _plan.PointLimit.ToString();
        ValidatePlan(); SyncAscendancy(); RefreshSearch(); Notify();
        if (SelectedClass is not null) FocusRequested?.Invoke(SelectedClass.StartNodeId);
    }
    public void Select(int id)
    {
        if (Catalog?.Nodes.ContainsKey(id) != true) return;
        _selectedId = id; _message = ""; RefreshPreview(); Notify();
    }

    /// <summary>
    /// The game's own tree click. A plain left click allocates an unallocated node together with the path that
    /// reaches it and refunds an allocated one — <c>Classes/PassiveTreeView.lua:411-430</c>
    /// (<c>spec:AllocNode</c> / <c>spec:DeallocNode</c>).
    /// <para>
    /// <paramref name="weaponSet"/> carries the game's modifier: <c>null</c> takes the node for BOTH sets,
    /// <c>1</c> or <c>2</c> takes it for that weapon set alone (Shift + left / Shift + right click on the
    /// tree). Only a main-tree passive can be paid for by one set — PoB2 draws the same line at
    /// <c>PassiveTreeView.lua:677-679</c> (never a keystone, a jewel socket or an ascendancy node). The path
    /// that reaches the node stays a plain allocation of both sets, which is what it is in the game too: the
    /// sockets and connectors it opens are not weapon-set specific.
    /// </para>
    /// <para>
    /// Which nodes may be allocated or refunded is decided by <see cref="TreeClickModel"/>; a click outside the
    /// point budget reports the limit instead of changing the plan (the engine throws TreeOverBudget, which
    /// <see cref="Run"/> reports).
    /// </para>
    /// </summary>
    public void ClickNode(int id, int? weaponSet = null)
    {
        if (Catalog?.Nodes.ContainsKey(id) != true) return;
        _selectedId = id; _message = "";
        if (!CanModify) { RefreshPreview(); Notify(); return; }
        switch (TreeClickModel.Resolve(_plan, Catalog, id))
        {
            case TreeClickAction.Refund:
                Run(() =>
                {
                    int[] removed = _engine!.RefundSet(_plan, id);
                    if (removed.Length > 1 && !Confirm(L.Format("TreeRefundQuestion", removed.Length))) return;
                    Apply(_engine.Refund(_plan, id));
                    _message = L.Format("TreeRefunded", removed.Length);
                });
                return;
            case TreeClickAction.Allocate:
                Run(() =>
                {
                    var next = _engine!.Allocate(_plan, id, DefaultAttribute?.Id ?? 26297);
                    bool bySet = weaponSet is 1 or 2 && CanUseWeaponSet(id);
                    Apply(bySet ? next with { WeaponSetNodes = new Dictionary<int, int>(next.WeaponSetNodes) { [id] = weaponSet!.Value } } : next);
                    _message = bySet ? L.Format("TreeAllocatedWeaponSet", L["WeaponSet" + weaponSet]) : L["TreeAllocated"];
                });
                return;
            default:
                RefreshPreview(); Notify();
                return;
        }
    }

    /// <summary>True when a node may be paid for by one weapon set: only a main-tree passive, exactly the
    /// restriction PoB2 applies (<c>Classes/PassiveTreeView.lua:677-679</c> — the ascendancy graph, a keystone
    /// and a jewel socket are never weapon-set specific).</summary>
    private bool CanUseWeaponSet(int id) =>
        IsMainView && Catalog?.Nodes.TryGetValue(id, out var node) == true &&
        node is { IsStart: false, IsKeystone: false, IsJewel: false, IsAscendancy: false };

    private void Apply(PassiveTreePlan next)
    {
        if (_owner is not null) { _owner.Apply(AscendancyRules.Update(_owner.Catalog!, _owner._plan, next)); return; }
        _history.Record(_plan); Restore(next);
    }
    /// <summary>
    /// Takes a plan that came from OUTSIDE the engine — a saved document, an imported build — and reports
    /// when the engine had to bring it back to a state its own rules accept: a node an older version stored
    /// as a plain allocation but which this engine treats as granted by an item (an anoint, a jewel's
    /// "Allocates X" socket) moves into the granted set, and whatever the pinned tree cannot place at all is
    /// given up. Left as it was, such a plan stays invalid: no hover tooltip can compute an impact on it and
    /// no click can change it (every plan walk throws TreeInvalidSaved), which is the state the imported
    /// "PoB · …" build was saved in by an older version. Nothing is invented, and the change is reported.
    /// </summary>
    private PassiveTreePlan Adopt(PassiveTreePlan saved)
    {
        if (_engine is null || !IsReady) return saved;
        var repaired = _engine.Repair(saved, out int granted, out int dropped, out int choices);
        if (!ReferenceEquals(repaired, saved)) _message = L.Format("TreeRepaired", granted, dropped, choices);
        return repaired;
    }
    private void SyncAscendancy()
    {
        if (_owner is not null || AscendancyTree is null) return;
        _synchronizing = true;
        try
        {
        if (_choicesClass != _plan.ClassIndex)
        {
            _choicesClass = _plan.ClassIndex; AscendancyChoices.Clear();
            AscendancyChoices.Add(new(null, L["NoAscendancy"]));
            foreach (var definition in Catalog?.Ascendancies.Where(a => a.ClassIndex == _plan.ClassIndex) ?? [])
                AscendancyChoices.Add(new(definition.Id, definition.Name));
        }
        }
        finally { _synchronizing = false; }
        var chosen = Catalog?.Ascendancies.FirstOrDefault(a => a.Id == _plan.Ascendancy?.Id && a.ClassIndex == _plan.ClassIndex);
        var child = AscendancyTree;
        bool changed = child.Catalog != chosen?.Graph;
        child.Catalog = chosen?.Graph; child._engine = child.Catalog is null ? null : new(child.Catalog);
        child._plan = chosen is null ? new() : chosen.ToGraphPlan(_plan.Ascendancy!);
        child._pointLimitText = child._plan.PointLimit.ToString();
        if (changed) { child._selectedId = null; child._message = ""; child._loadError = ""; }
        child.ValidatePlan(); child.RefreshSearch(); child.Notify();
        if (chosen is null) _showAscendancy = false;
    }
    /// <summary>Notified after every accepted plan change so the Jewels tab can re-read sockets.</summary>
    public event Action? PlanChanged;
    public PassiveTreePlan PlanSnapshot => _plan;

    /// <summary>Allocated jewel-socket nodes with human labels for the Jewels tab.</summary>
    /// <summary>PoB2-style jewel tooltips on the tree: the socketed jewel's name and affixes for a
    /// jewel-socket node. Wired by the shell to the Jewels tab, which owns the jewel inventory.</summary>
    public Func<int, string?>? SocketInfoProvider { get; set; }
    /// <summary>The socketed jewel's radius band for a tree socket (PoB2's jewelRadiusIndex), so the tree can
    /// draw the same radius circle PoB2 does. 0 means "no band to draw".</summary>
    public Func<int, int>? JewelRadiusProvider { get; set; }

    public IReadOnlyList<(int NodeId, string Label, Guid? JewelId)> JewelSockets =>
        Catalog is null ? [] : _plan.AllocatedNodes
            .Where(id => Catalog.Nodes[id].IsJewel)
            .OrderBy(id => id)
            .Select(id => (id, L.Format("TreeSocketLabel", id), _plan.Jewels.TryGetValue(id, out var g) ? (Guid?)g : null))
            .ToList();

    /// <summary>Places a jewel in an allocated tree socket. A jewel that changes ALLOCATION — From Nothing's
    /// "Passives in radius of X can be Allocated without being connected to your tree" (PoB2's
    /// <c>fromNothingKeystone</c>) or Intuitive Leap's "Passives in radius can be allocated…"
    /// (<c>intuitiveLeapLike</c>) — also records its rule, because the socket then reaches those nodes with
    /// no edge and <see cref="PassiveTreeEngine"/> has to know it.</summary>
    public bool SocketJewel(Guid jewelItemId, int nodeId, RadiusAllocationRule? rule = null)
    {
        if (Catalog is null || !Catalog.Nodes.TryGetValue(nodeId, out var node) || !node.IsJewel || !_plan.AllocatedNodes.Contains(nodeId)) return false;
        var jewels = new Dictionary<int, Guid>(_plan.Jewels) { [nodeId] = jewelItemId };
        var rules = new Dictionary<int, RadiusAllocationRule>(_plan.RadiusJewels);
        if (rule is null) rules.Remove(nodeId); else rules[nodeId] = rule;
        ApplyClosed(_plan with { Jewels = jewels, RadiusJewels = rules });
        return true;
    }

    public bool UnsocketJewel(int nodeId)
    {
        if (!_plan.Jewels.ContainsKey(nodeId)) return false;
        var jewels = new Dictionary<int, RadiusAllocationRule>(_plan.RadiusJewels);
        jewels.Remove(nodeId);
        var socketed = new Dictionary<int, Guid>(_plan.Jewels);
        socketed.Remove(nodeId);
        ApplyClosed(_plan with { Jewels = socketed, RadiusJewels = jewels });
        return true;
    }

    /// <summary>
    /// Applies a socket change and then drops whatever the change left unreachable: a socket that loses a
    /// radius jewel (or a jewel whose rule is replaced) takes with it exactly the nodes that rule used to
    /// reach, which is what the game refunds. The count is reported so the change is never silent.
    /// </summary>
    private void ApplyClosed(PassiveTreePlan next)
    {
        if (_owner is not null) { _owner.ApplyClosed(next); return; }
        var closed = _engine?.KeepReachable(next) ?? next;
        int dropped = next.AllocatedNodes.Length - closed.AllocatedNodes.Length;
        Apply(closed);
        if (dropped > 0) _message = L.Format("TreeRadiusDropped", dropped);
    }

    private void Restore(PassiveTreePlan next)
    {
        bool classChanged = _plan.ClassIndex != next.ClassIndex;
        _plan = next.Copy(); _pointLimitText = _plan.PointLimit.ToString(); _editor?.SetTree(_plan); _message = "";
        ValidatePlan(); SyncAscendancy(); RefreshSearch(); Notify();
        if (classChanged && SelectedClass is not null) FocusRequested?.Invoke(SelectedClass.StartNodeId);
        PlanChanged?.Invoke();
    }
    private void ValidatePlan()
    {
        _validationCode = ""; Allocated = _plan.AllocatedNodes.ToHashSet();
        if (_engine is not null)
        {
            try { _engine.Validate(_plan); }
            catch (TreeRuleException e) { _validationCode = e.Code; }
        }
        RefreshPreview();
    }
    private void RefreshPreview()
    {
        Preview = [];
        if (_engine is null || _validationCode.Length != 0 || _selectedId is not int id) return;
        try { Preview = _engine.FindPath(_plan, id).ToHashSet(); } catch (TreeRuleException) { }
    }
    private void RefreshSearch()
    {
        SearchResults.Clear(); _selectedResult = null; Raise(nameof(SelectedResult));
        var query = Search.Trim(); SearchMatches = []; _matchCount = 0;
        if (Catalog is not null && query.Length > 0)
        {
            foreach (var node in Catalog.Nodes.Values.OrderBy(n => n.Id))
            {
                var info = Catalog.Describe(node.Id, _plan);
                if (node.Id.ToString() != query && !info.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && !info.Stats.Any(s => s.Contains(query, StringComparison.OrdinalIgnoreCase))) continue;
                SearchMatches.Add(node.Id); _matchCount++;
                if (SearchResults.Count < 80) SearchResults.Add(new(node.Id, info.Name, $"#{node.Id} · " + (info.Stats.FirstOrDefault() ?? "") + (node.IsSupported ? "" : " · " + L["TreeLocked"])));
            }
        }
        Raise(nameof(SearchCount)); StateChanged?.Invoke();
    }
    private void Notify()
    {
        foreach (var name in new[] { nameof(IsReady), nameof(CanModify), nameof(CanReset), nameof(Classes), nameof(SelectedClass), nameof(AttributeChoices), nameof(DefaultAttribute), nameof(SelectedAttribute), nameof(HasAttribute), nameof(PointLimitText), nameof(PointSummary), nameof(Warning), nameof(WarningText), nameof(LoadError), nameof(SelectedName), nameof(SelectedStats), nameof(SelectedInfo), nameof(Message), nameof(AscendancyChoices), nameof(SelectedAscendancy), nameof(HasAscendancy), nameof(DisplayedTree), nameof(ViewTitle), nameof(PortraitKey), nameof(WeaponSetNodes), nameof(ActiveWeaponSet), nameof(HasSelectedAllocated), nameof(SelectedWeaponSetText), nameof(ShowNodePanel) }) Raise(name);
        StateChanged?.Invoke(); CommandManager.InvalidateRequerySuggested();
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (TreeRuleException e) { _message = L[e.Code]; Notify(); }
    }
    private bool Confirm(string text) => MessageBox.Show(text, L["Confirm"], MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
