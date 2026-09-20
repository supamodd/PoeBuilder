using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PoeBuilder.App.Services;
using PoeBuilder.App.Views;
using PoeBuilder.Core.Calculation;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Skills;
using Localization = PoeBuilder.App.Services.Localization;

namespace PoeBuilder.App.ViewModels;

public sealed record SkillSocketVm(Guid GroupId, int Index, bool Filled, string GemId, string Name, string Level, ImageSource? Icon);
public sealed record SkillGroupCardVm(Guid Id, string Name, string GemName, bool Enabled, ImageSource? Icon, Brush Accent,
    string Level, string Quality, string SetLabel, IReadOnlyList<SkillSocketVm> Sockets, string Dps, string Summary, string LevelNote, string Description);

public sealed class SkillsViewModel : Observable
{
    public Localization L { get; }
    public GameCatalog? Catalog { get; private set; }
    private BuildEditor? _editor;
    private SkillPlan _plan = new();
    private readonly PlanHistory<SkillPlan> _history = new(p => p.Copy());
    private string _warning = "", _status = "", _search = "", _quickSearch = "", _quickLevel = "1", _quickQuality = "0";
    private Gem? _quickSelectedGem;
    private SkillGroupCardVm? _selected;
    public bool CanEdit => _editor is not null && Catalog is not null && _warning.Length == 0;
    public string Warning => _editor is null ? L["NoBuildText"] : Catalog is null ? L["CatalogMissing"] : _warning;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Search { get => _search; set { if (Set(ref _search, value)) Refresh(); } }
    // Quick add: pick the gem, level and quality — the group is named after the gem, like in game.
    public string QuickSearch { get => _quickSearch; set { if (Set(ref _quickSearch, value)) Raise(nameof(QuickActives)); } }
    public string QuickLevel { get => _quickLevel; set => Set(ref _quickLevel, value); }
    public string QuickQuality { get => _quickQuality; set => Set(ref _quickQuality, value); }
    public Gem? QuickSelectedGem { get => _quickSelectedGem; set => Set(ref _quickSelectedGem, value); }
    public IEnumerable<Gem> QuickActives => Catalog is null ? [] : Catalog.Gems.Values
        .Where(g => g.Kind != "support" && (QuickSearch.Length == 0 || (g.Name + " " + g.Description).Contains(QuickSearch, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(g => g.Name).Take(300);
    public ICommand QuickAddCommand { get; }
    public string SelectedDetails => Selected is null || Catalog is null ? "" : Describe(_plan.Groups.Single(g => g.Id == Selected.Id));
    public ObservableCollection<SkillGroupCardVm> Cards { get; } = [];
    public SkillGroupCardVm? Selected { get => _selected; set { Set(ref _selected, value); Raise(nameof(SelectedDetails)); CommandManager.InvalidateRequerySuggested(); } }
    public ICommand NewCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand SocketPickCommand { get; }
    public ICommand SocketClearCommand { get; }
    public ICommand DeleteCardCommand { get; }
    public SkillsViewModel(Localization l)
    {
        L = l;
        NewCommand = new ActionCommand(_ => Edit(null), () => CanEdit && _plan.Groups.Length < 40);
        EditCommand = new ActionCommand(_ => Edit(Current()), () => CanEdit && Selected is not null);
        DeleteCommand = new ActionCommand(_ =>
        {
            if (Confirm(L["DeleteGroupQuestion"])) Run(() => Apply(_plan.Copy() with { Groups = _plan.Groups.Where(g => g.Id != Selected!.Id).Select(g => g.Copy()).ToArray() }));
        }, () => CanEdit && Selected is not null);
        // Remark 0.8.0-7: a delete button on every card, so no selection dance is needed.
        DeleteCardCommand = new ActionCommand(arg =>
        {
            if (arg is not SkillGroupCardVm card || !CanEdit) return;
            if (Confirm(L["DeleteGroupQuestion"])) Run(() => Apply(_plan.Copy() with { Groups = _plan.Groups.Where(g => g.Id != card.Id).Select(g => g.Copy()).ToArray() }));
        }, () => CanEdit);
        DuplicateCommand = new ActionCommand(_ => Run(() => Apply(SkillRules.Put(Catalog!, _plan, Current().Copy() with { Id = Guid.NewGuid() }))), () => CanEdit && Selected is not null && _plan.Groups.Length < 40);
        UndoCommand = new ActionCommand(_ => Restore(_history.Undo(_plan)), () => _editor is not null && _history.CanUndo);
        RedoCommand = new ActionCommand(_ => Restore(_history.Redo(_plan)), () => _editor is not null && _history.CanRedo);
        ResetCommand = new ActionCommand(_ => { if (Confirm(L["ResetSkillsQuestion"])) Apply(new()); }, () => _editor is not null);
        SocketPickCommand = new ActionCommand(arg =>
        {
            if (arg is not SkillSocketVm socket) return;
            Guid id = socket.GroupId; int index = socket.Index;
            var group = _plan.Groups.FirstOrDefault(g => g.Id == id);
            if (group is null || !CanEdit) return;
            var picker = new SupportPickerWindow(L, Catalog!, group.Supports) { Owner = Application.Current.MainWindow };
            if (picker.ShowDialog() == true && picker.Selected is { } gem)
            {
                var supports = group.Supports.ToList();
                var selection = new GemSelection { GemId = gem.Id, Level = gem.Levels.Contains(1) ? 1 : gem.Levels[0], Quality = 0 };
                if (index < supports.Count) supports[index] = selection; else supports.Add(selection);
                Run(() => Apply(_plan with { Groups = [.. _plan.Groups.Select(g => g.Id == id ? group with { Supports = [.. supports] } : g)] }));
            }
        }, () => true);
        SocketClearCommand = new ActionCommand(arg =>
        {
            if (arg is not SkillSocketVm socket) return;
            Guid id = socket.GroupId; int index = socket.Index;
            var group = _plan.Groups.FirstOrDefault(g => g.Id == id);
            if (group is null || index >= group.Supports.Length) return;
            var supports = group.Supports.ToList(); supports.RemoveAt(index);
            Run(() => Apply(_plan with { Groups = [.. _plan.Groups.Select(g => g.Id == id ? group with { Supports = [.. supports] } : g)] }));
        }, () => true);
        QuickAddCommand = new ActionCommand(_ =>
        {
            if (QuickSelectedGem is null || Catalog is null || !CanEdit) return;
            if (!int.TryParse(QuickLevel, out int level) || !QuickSelectedGem.Levels.Contains(level)) { Status = L["PlanGemLevel"]; return; }
            if (!int.TryParse(QuickQuality, out int quality) || quality is < 0 or > 20) { Status = L["PlanGemLevel"]; return; }
            var gem = QuickSelectedGem;
            string name = gem.Name;
            for (int copy = 2; _plan.Groups.Any(g => g.Name == name); copy++) name = gem.Name + " " + copy;
            var group = new SkillGroup { Name = name, Active = new() { GemId = gem.Id, Level = level, Quality = quality } };
            try { Apply(SkillRules.Put(Catalog, _plan, group)); Status = L.Format("QuickAdded", name); }
            catch (PlanningException e) { Status = L[e.Code]; }
            catch (PoeBuilder.Core.Models.BuildFormatException e) { Status = L["PlanInvalid"] + "\n" + e.Message; }
        }, () => CanEdit);
        L.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Localization.Language)) Refresh(); };
        CalculationHub.Changed += RefreshDps;
    }
    public void SetCatalog(GameCatalog catalog) { Catalog = catalog; Validate(); Refresh(); }
    public void BindEditor(BuildEditor? editor) { _editor = editor; _plan = editor?.SkillsSnapshot ?? new(); _history.Clear(); Validate(); Refresh(); }
    private SkillGroup Current() => _plan.Groups.Single(g => g.Id == Selected!.Id);
    private void Edit(SkillGroup? group)
    {
        var draft = new GroupDraftViewModel(L, Catalog!, group, saved =>
        {
            var next = SkillRules.Put(Catalog!, _plan, saved); Apply(next);
            Selected = Cards.FirstOrDefault(g => g.Id == saved.Id);
        });
        new SkillGroupEditorWindow(draft) { Owner = Application.Current.MainWindow }.ShowDialog();
    }
    private void Apply(SkillPlan next) { _history.Record(_plan); Restore(next); }
    private void Restore(SkillPlan next)
    {
        try
        {
            if (Catalog is not null) SkillRules.Validate(Catalog, next);
            _plan = next.Copy(); _editor?.SetSkills(_plan); Validate(); Refresh();
        }
        catch (PlanningException e) { Status = L[e.Code]; }
    }
    private void Validate()
    {
        _warning = "";
        if (Catalog is null) return;
        try { SkillRules.Validate(Catalog, _plan); }
        catch (PlanningException e) { _warning = L[e.Code]; }
    }
    private void RefreshDps() { if (Cards.Count > 0) Refresh(); }
    private void Refresh()
    {
        Guid? selected = Selected?.Id; Cards.Clear();
        foreach (var group in _plan.Groups)
        {
            var gem = Catalog?.Gems.GetValueOrDefault(group.Active.GemId);
            if (gem is null) continue;
            if (Search.Length > 0 && !(group.Name + " " + gem.Name).Contains(Search, StringComparison.OrdinalIgnoreCase)) continue;
            string set = group.WeaponSet == 0 ? L["SetBoth"] : group.WeaponSet == 1 ? L["WeaponSet1"] : L["WeaponSet2"];
            var sockets = new List<SkillSocketVm>();
            for (int i = 0; i < 5; i++)
            {
                if (i < group.Supports.Length)
                {
                    var support = Catalog!.Gems.GetValueOrDefault(group.Supports[i].GemId);
                    sockets.Add(new(group.Id, i, true, group.Supports[i].GemId, support?.Name ?? group.Supports[i].GemId,
                        group.Supports[i].Level.ToString(), support is null ? null : IconService.Instance.ForGem(support.Id)));
                }
                else sockets.Add(new(group.Id, i, false, "", "", "", null));
            }
            var info = CalculationHub.Latest?.Skills.FirstOrDefault(s => s.GroupId == group.Id);
            string dps = info is { HasData: true, EnabledForSet: true, Dps: > 0 }
                ? L["DpsPerSecond"] + " ≈ " + info.Dps.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)
                : "";
            string levelNote = info is { LevelFromItems: > 0 } ? L.Format("LevelFromItems", info.LevelFromItems) : "";
            string accentHex = gem.Color switch { "r" or "s" => "#C5443C", "g" => "#4FAE54", "b" => "#4C7FD0", _ => "#7A8794" };
            var accent = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accentHex));
            accent.Freeze();
            Cards.Add(new(group.Id, (group.Enabled ? "" : "✕ ") + group.Name, gem.Name, group.Enabled,
                IconService.Instance.ForGem(gem.Id), accent, group.Active.Level.ToString(), group.Active.Quality.ToString(), set,
                sockets, dps, $"{gem.Name} · {L.Format("SupportsCount", group.Supports.Length)} · {set}", levelNote,
                PoeBuilder.App.ViewModels.CharacterViewModel.DescribeGem(L, gem, group.Active.Level, group.Active.Quality)));
        }
        Selected = Cards.FirstOrDefault(g => g.Id == selected);
        Raise(nameof(SelectedDetails)); Raise(nameof(CanEdit)); Raise(nameof(Warning));
        CommandManager.InvalidateRequerySuggested();
    }
    private string Describe(SkillGroup group)
    {
        var lines = new List<string>();
        void Add(GemSelection sel, bool support)
        {
            var gem = Catalog!.Gems.GetValueOrDefault(sel.GemId);
            lines.Add((support ? "◦ " : "◆ ") + (gem?.Name ?? sel.GemId) + $" · {L["GemLevel"]} {sel.Level} · {L["GemQuality"]} {sel.Quality}");
            if (gem is not null && gem.Description.Length > 0) lines.Add(gem.Description.Trim());
        }
        Add(group.Active, false);
        foreach (var support in group.Supports) Add(support, true);
        if (group.Notes.Length > 0) lines.Add("— " + group.Notes);
        var recommended = Catalog!.Gems.GetValueOrDefault(group.Active.GemId)?.RecommendedSupports
            .Select(id => Catalog.Gems.GetValueOrDefault(id)?.Name).Where(n => n is not null).Take(8).ToArray() ?? [];
        if (recommended.Length > 0) lines.Add(L["RecommendedSupports"] + ": " + string.Join(", ", recommended));
        return string.Join("\n\n", lines);
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
