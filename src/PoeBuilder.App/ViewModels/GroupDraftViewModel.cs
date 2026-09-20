using System.Collections.ObjectModel;
using System.Windows.Input;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Models;
using PoeBuilder.Core.Skills;
using Localization = PoeBuilder.App.Services.Localization;

namespace PoeBuilder.App.ViewModels;

public sealed class SupportDraft : Observable
{
    public Gem Gem { get; }
    private string _level, _quality;
    private readonly Action _changed;
    public string LevelText { get => _level; set { if (Set(ref _level, value)) _changed(); } }
    public string QualityText { get => _quality; set { if (Set(ref _quality, value)) _changed(); } }
    public SupportDraft(Gem gem, int level, int quality, Action changed)
    { Gem = gem; _level = level.ToString(); _quality = quality.ToString(); _changed = changed; }
    public GemSelection ToSelection()
    {
        if (!int.TryParse(LevelText, out int level) || !Gem.Levels.Contains(level)) throw new PlanningException("PlanGemLevel");
        if (!int.TryParse(QualityText, out int quality) || quality is < 0 or > 20) throw new PlanningException("PlanGemLevel");
        return new() { GemId = Gem.Id, Level = level, Quality = quality };
    }
    public System.Windows.Media.ImageSource? Icon => PoeBuilder.App.Services.IconService.Instance.ForGem(Gem.Id);
}
public sealed class GroupDraftViewModel : Observable
{
    public Localization L { get; }
    private readonly GameCatalog _catalog;
    private readonly Action<SkillGroup> _commit;
    private readonly Guid _id;
    private bool _loading = true, _enabled = true;
    private string _name, _notes, _activeSearch = "", _supportSearch = "", _activeLevel = "1", _activeQuality = "0", _error = "";
    private Gem? _active, _selectedSupport;
    private NamedOption? _weaponSet;
    public bool IsDirty { get; private set; }
    public bool Accepted { get; private set; }
    public event Action? Saved;
    public string Error { get => _error; private set => Set(ref _error, value); }
    public ObservableCollection<SupportDraft> Supports { get; } = [];
    public IReadOnlyList<NamedOption> WeaponSets { get; }
    public string Name { get => _name; set { if (Set(ref _name, value)) Touch(); } }
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) Touch(); } }
    public string Notes { get => _notes; set { if (Set(ref _notes, value)) Touch(); } }
    public NamedOption? WeaponSet { get => _weaponSet; set { if (value is not null && Set(ref _weaponSet, value)) Touch(); } }
    public string ActiveSearch { get => _activeSearch; set { if (Set(ref _activeSearch, value)) Raise(nameof(Actives)); } }
    public string SupportSearch { get => _supportSearch; set { if (Set(ref _supportSearch, value)) Raise(nameof(AvailableSupports)); } }
    public string ActiveLevel { get => _activeLevel; set { if (Set(ref _activeLevel, value)) Touch(); } }
    public string ActiveQuality { get => _activeQuality; set { if (Set(ref _activeQuality, value)) Touch(); } }
    public IEnumerable<Gem> Actives => _catalog.Gems.Values
        .Where(g => g.Kind != "support" && (ActiveSearch.Length == 0 || (g.Name + " " + g.Description).Contains(ActiveSearch, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(g => g.Name).Take(200);
    public Gem? Active
    {
        get => _active;
        set { if (value is not null && Set(ref _active, value)) { if (Name.Length == 0) Name = value.Name; Touch(); Raise(nameof(ActiveDescription)); Raise(nameof(ActiveIcon)); Raise(nameof(Recommended)); } }
    }
    public System.Windows.Media.ImageSource? ActiveIcon => _active is null ? null : PoeBuilder.App.Services.IconService.Instance.ForGem(_active.Id);
    public string ActiveDescription => Active?.Description.Trim() ?? "";
    public string Recommended
    {
        get
        {
            if (Active is null) return "";
            var names = Active.RecommendedSupports.Select(id => _catalog.Gems.GetValueOrDefault(id)?.Name).Where(n => n is not null).Take(10);
            return L["RecommendedSupports"] + ": " + string.Join(", ", names);
        }
    }
    public IEnumerable<Gem> AvailableSupports => _catalog.Gems.Values
        .Where(g => g.Kind == "support" && Supports.All(s => s.Gem.Id != g.Id) &&
            (SupportSearch.Length == 0 || (g.Name + " " + g.Description).Contains(SupportSearch, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(g => g.Name).Take(200);
    public Gem? SelectedSupport { get => _selectedSupport; set => Set(ref _selectedSupport, value); }
    public string SupportsCount => L.Format("SupportsCount", Supports.Count);
    public ICommand AddSupportCommand { get; }
    public ICommand RemoveSupportCommand { get; }
    public ICommand SaveCommand { get; }
    public GroupDraftViewModel(Localization l, GameCatalog catalog, SkillGroup? group, Action<SkillGroup> commit)
    {
        L = l; _catalog = catalog; _commit = commit; _id = group?.Id ?? Guid.NewGuid();
        WeaponSets = [new("0", L["SetBoth"]), new("1", L["WeaponSet1"]), new("2", L["WeaponSet2"])];
        _weaponSet = WeaponSets[group?.WeaponSet ?? 0];
        _name = group?.Name ?? ""; _notes = group?.Notes ?? ""; _enabled = group?.Enabled ?? true;
        _active = group is null ? null : catalog.Gems[group.Active.GemId];
        if (group is not null) { _activeLevel = group.Active.Level.ToString(); _activeQuality = group.Active.Quality.ToString(); }
        if (group is not null) foreach (var sel in group.Supports)
                Supports.Add(new(catalog.Gems[sel.GemId], sel.Level, sel.Quality, Touch));
        Supports.CollectionChanged += (_, _) => Raise(nameof(SupportsCount));
        AddSupportCommand = new ActionCommand(_ =>
        {
            if (SelectedSupport is null || Supports.Count >= 5 || !AvailableSupports.Any(g => g.Id == SelectedSupport.Id)) return;
            Supports.Add(new(SelectedSupport, 1, 0, Touch)); SelectedSupport = null; Touch(); Raise(nameof(AvailableSupports)); Raise(nameof(SupportsCount));
        }, () => SelectedSupport is not null && Supports.Count < 5);
        RemoveSupportCommand = new ActionCommand(arg => { if (arg is SupportDraft s) { Supports.Remove(s); Touch(); Raise(nameof(AvailableSupports)); Raise(nameof(SupportsCount)); } });
        SaveCommand = new ActionCommand(_ => Save());
        _loading = false;
    }
    private void Touch() { if (!_loading) IsDirty = true; Error = ""; CommandManager.InvalidateRequerySuggested(); }
    private void Save()
    {
        try
        {
            if (Active is null || !int.TryParse(ActiveLevel, out int level) || !Active.Levels.Contains(level)) throw new PlanningException("PlanGemLevel");
            if (!int.TryParse(ActiveQuality, out int quality) || quality is < 0 or > 20) throw new PlanningException("PlanGemLevel");
            var group = new SkillGroup
            {
                Id = _id, Name = Name.Trim(), Enabled = Enabled, WeaponSet = int.Parse(WeaponSet!.Id),
                Active = new() { GemId = Active.Id, Level = level, Quality = quality },
                Supports = Supports.Select(s => s.ToSelection()).ToArray(), Notes = Notes
            };
            SkillRules.ValidateGroup(_catalog, group); _commit(group); Accepted = true; Saved?.Invoke();
        }
        catch (PlanningException e) { Error = L[e.Code]; }
        catch (BuildFormatException e) { Error = L["PlanInvalid"] + "\n" + e.Message; }
    }
}
