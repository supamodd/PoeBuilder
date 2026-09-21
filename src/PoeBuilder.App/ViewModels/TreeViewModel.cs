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
    private int Spent => Catalog is null ? 0 : Allocated.Where(Catalog.Nodes.ContainsKey).Sum(id => Catalog.Nodes[id].PointCost);
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
    public bool IsMainView => _owner is null;
    public bool IsReady => Catalog is not null;
    public bool CanModify => (_owner?.CanModify ?? (_editor is not null)) && IsReady && _validationCode.Length == 0;
    public bool CanReset => (_owner?.CanReset ?? (_editor is not null)) && IsReady;
    public bool HasAttribute => _selectedId is int id && Allocated.Contains(id) && Catalog?.Nodes[id].IsAttribute == true;
    public string LoadError => _loadError;
    public string Warning => _loadError.Length > 0 ? _loadError : _validationCode.Length > 0 ? L[_validationCode] : !(_owner?.CanReset ?? (_editor is not null)) ? L["TreeBrowseOnly"] : "";
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
            if (Allocated.Contains(id)) return node.IsJewel ? L["TreeJewelInfo"] : L["TreeAllocated"];
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
        _plan = editor?.TreeSnapshot ?? new(); _pointLimitText = _plan.PointLimit.ToString();
        ValidatePlan(); SyncAscendancy(); RefreshSearch(); Notify();
        if (SelectedClass is not null) FocusRequested?.Invoke(SelectedClass.StartNodeId);
    }
    public void Select(int id)
    {
        if (Catalog?.Nodes.ContainsKey(id) != true) return;
        _selectedId = id; _message = ""; RefreshPreview(); Notify();
    }
    private void Apply(PassiveTreePlan next)
    {
        if (_owner is not null) { _owner.Apply(AscendancyRules.Update(_owner.Catalog!, _owner._plan, next)); return; }
        _history.Record(_plan); Restore(next);
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
    public IReadOnlyList<(int NodeId, string Label, Guid? JewelId)> JewelSockets =>
        Catalog is null ? [] : _plan.AllocatedNodes
            .Where(id => Catalog.Nodes[id].IsJewel)
            .OrderBy(id => id)
            .Select(id => (id, L.Format("TreeSocketLabel", id), _plan.Jewels.TryGetValue(id, out var g) ? (Guid?)g : null))
            .ToList();

    public bool SocketJewel(Guid jewelItemId, int nodeId)
    {
        if (Catalog is null || !Catalog.Nodes.TryGetValue(nodeId, out var node) || !node.IsJewel || !_plan.AllocatedNodes.Contains(nodeId)) return false;
        var jewels = new Dictionary<int, Guid>(_plan.Jewels) { [nodeId] = jewelItemId };
        Apply(_plan with { Jewels = jewels });
        return true;
    }

    public bool UnsocketJewel(int nodeId)
    {
        if (!_plan.Jewels.ContainsKey(nodeId)) return false;
        var jewels = new Dictionary<int, Guid>(_plan.Jewels);
        jewels.Remove(nodeId);
        Apply(_plan with { Jewels = jewels });
        return true;
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
        foreach (var name in new[] { nameof(IsReady), nameof(CanModify), nameof(CanReset), nameof(Classes), nameof(SelectedClass), nameof(AttributeChoices), nameof(DefaultAttribute), nameof(SelectedAttribute), nameof(HasAttribute), nameof(PointLimitText), nameof(PointSummary), nameof(Warning), nameof(LoadError), nameof(SelectedName), nameof(SelectedStats), nameof(SelectedInfo), nameof(Message), nameof(AscendancyChoices), nameof(SelectedAscendancy), nameof(HasAscendancy), nameof(DisplayedTree), nameof(ViewTitle), nameof(PortraitKey) }) Raise(name);
        StateChanged?.Invoke(); CommandManager.InvalidateRequerySuggested();
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (TreeRuleException e) { _message = L[e.Code]; Notify(); }
    }
    private bool Confirm(string text) => MessageBox.Show(text, L["Confirm"], MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
