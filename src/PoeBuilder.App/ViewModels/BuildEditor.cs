using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Skills;
using PoeBuilder.Core.Models;
using PoeBuilder.Core.Tree;

namespace PoeBuilder.App.ViewModels;

public sealed class BuildEditor : Observable
{
    private BuildDocument _baseline;
    private string _name, _characterClass, _levelText, _gameVersion, _notes, _progressStage;
    private bool _isDirty;
    private bool _loadingStage;
    private readonly List<BuildProgressionStage> _stages = [];
    private Guid _activeStageId;
    private EquipmentPlan? _equipment;
    private SkillPlan? _skills;
    private ResourceReservationPlan? _reservation;
    private bool? _lowLife;
    private DefenceScenarioPlan? _defence;
    public EquipmentPlan? EquipmentSnapshot => _equipment?.Copy();
    public SkillPlan? SkillsSnapshot => _skills?.Copy();
    public void SetEquipment(EquipmentPlan plan) { _equipment = plan.Copy(); Changed(stageCustomized: true); Raise(nameof(EquipmentSnapshot)); }
    public void SetSkills(SkillPlan plan) { _skills = plan.Copy(); Changed(stageCustomized: true); Raise(nameof(SkillsSnapshot)); }
    private PassiveTreePlan? _tree;
    public PassiveTreePlan? TreeSnapshot => _tree?.Copy();
    public void SetTree(PassiveTreePlan plan) { _tree = plan.Copy(); Changed(stageCustomized: true); Raise(nameof(TreeSnapshot)); }
    private string[]? _questRewards;
    private string? _notesRtf;
    private BuildConditions _conditions;
    public event Action? StageActivated;
    /// <summary>Quest-reward lines of this build (see <see cref="QuestRewardIndex"/> for the table the
    /// tab offers). Null means "no rewards resolved", which is what a build created by hand has.</summary>
    public string[]? QuestRewardsSnapshot => _questRewards is null ? null : [.. _questRewards];
    /// <summary>PoB2 config conditions (player state, enemy state and the enemy values of the effective
    /// DPS mode). Records compare by value, so an unchanged set never marks the build dirty.</summary>
    public BuildConditions ConditionsSnapshot => _conditions;
    public void SetQuestRewards(string[]? lines)
    {
        string[]? next = lines is { Length: > 0 } ? [.. lines] : null;
        if (SameLines(_questRewards, next)) return;
        _questRewards = next; Changed(stageCustomized: true); Raise(nameof(QuestRewardsSnapshot));
    }
    public void SetConditions(BuildConditions conditions)
    {
        if (_conditions == conditions) return;
        _conditions = conditions; Changed(stageCustomized: true); Raise(nameof(ConditionsSnapshot));
    }
    private static bool SameLines(string[]? left, string[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    public BuildEditor(BuildDocument document, bool isNew = false)
    {
        _baseline = document; _name = document.Name; _characterClass = document.CharacterClass;
        _levelText = document.Level.ToString(); _gameVersion = document.GameVersion; _notes = document.Notes; _notesRtf = document.NotesRtf; _progressStage = document.ProgressStage;
        _isDirty = isNew; _tree = document.Tree?.Copy(); _equipment = document.Equipment?.Copy(); _skills = document.Skills?.Copy();
        _questRewards = document.QuestRewards is { Length: > 0 } rewards ? [.. rewards] : null;
        _conditions = document.Conditions;
        _reservation = document.Reservation; _lowLife = document.LowLife; _defence = document.Defence;
        _stages.AddRange(document.Stages.Select(CopyStage));
        if (_stages.Count == 0)
        {
            string[] defaults = ["1 Акт", "2 Акт", "3 Акт", "4 Акт", "5 Акт", "6 Акт", "Эндгейм"];
            _stages.AddRange(defaults.Select((name, index) => BuildProgressionStage.FromBuild(document, name) with { IsInitialized = index == 0 }));
        }
        _activeStageId = _stages.FirstOrDefault(s => s.Id == document.ActiveStageId)?.Id ?? _stages[0].Id;
        InitializeThrough(_stages.FindIndex(s => s.Id == _activeStageId));
        LoadStage(_stages.First(s => s.Id == _activeStageId));
    }
    public Guid Id => _baseline.Id;
    public string Name { get => _name; set { if (Set(ref _name, value)) Changed(); } }
    public string CharacterClass { get => _characterClass; set { if (Set(ref _characterClass, value)) Changed(stageCustomized: true); } }
    public string LevelText { get => _levelText; set { if (Set(ref _levelText, value)) Changed(stageCustomized: true); } }
    public string GameVersion { get => _gameVersion; set { if (Set(ref _gameVersion, value)) Changed(); } }
    public string ProgressStage { get => _progressStage; set { if (Set(ref _progressStage, value)) Changed(stageCustomized: true); } }
    public bool StageEndgame { get => _progressStage == "endgame"; set { if (Set(ref _progressStage, value ? "endgame" : "starter")) Changed(stageCustomized: true); } }
    public string Notes
    {
        get => _notes;
        set
        {
            if (!Set(ref _notes, value)) return;
            _notesRtf = null; Changed(); Raise(nameof(NotesLength)); Raise(nameof(NotesRtf));
        }
    }
    public string? NotesRtf => _notesRtf;
    public int NotesLength => _notes.Length;
    public void SetNotesContent(string text, string? rtf)
    {
        bool textChanged = _notes != text, rtfChanged = _notesRtf != rtf;
        if (!textChanged && !rtfChanged) return;
        _notes = text; _notesRtf = rtf;
        if (textChanged) { Raise(nameof(Notes)); Raise(nameof(NotesLength)); }
        if (rtfChanged) Raise(nameof(NotesRtf));
        Changed();
    }
    public IReadOnlyList<BuildProgressionStage> Stages => _stages;
    public Guid CurrentStageId
    {
        get => _activeStageId;
        set { if (value != _activeStageId) { ActivateStage(value); if (value != _activeStageId) Raise(nameof(CurrentStageId)); } }
    }
    public string CurrentStageName => _stages.FirstOrDefault(s => s.Id == _activeStageId)?.Name ?? "";
    public bool CanChangeStage => IsValid;
    public bool IsDirty { get => _isDirty; private set => Set(ref _isDirty, value); }
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && Name.Length <= 80 &&
        int.TryParse(LevelText, out var level) && level is >= 1 and <= 100 &&
        CharacterClass.Length <= 80 && GameVersion.Length <= 32 && Notes.Length <= 100_000 &&
        (_notesRtf is null || _notesRtf.Length <= 1_000_000);
    private void Changed(bool stageCustomized = false)
    {
        if (_loadingStage) return;
        CaptureActiveStage();
        if (stageCustomized)
        {
            int index = _stages.FindIndex(s => s.Id == _activeStageId);
            if (index >= 0)
            {
                _stages[index] = _stages[index] with { IsInitialized = true };
                // Flow the edit forward to every later stage that still holds an inherited snapshot.
                // Without this a stage only received the previous stage's content at the moment it was opened,
                // so a change made to Act 1 after Act 2 had already been visited stayed in Act 1 — the edit
                // had nowhere to travel to. Earlier stages are never touched: a build is levelled up, never
                // down, so an edit flows strictly left to right.
                CarryForward(index);
            }
        }
        IsDirty = true; Raise(nameof(IsValid)); Raise(nameof(CanChangeStage));
    }
    /// <summary>Copies the state of the stage at <paramref name="index"/> into every later stage that has
    /// not been customized itself. A stage the owner has already edited keeps its own content — that is the
    /// whole point of having stages — and its own flag, so this never overwrites real work. It stops at the
    /// first customized stage rather than skipping it: a stage's later stages branch off that stage, not off
    /// the first one.</summary>
    private void CarryForward(int index)
    {
        for (int next = index + 1; next < _stages.Count; next++)
        {
            if (_stages[next].IsInitialized) break;
            _stages[next] = InheritState(_stages[next - 1], _stages[next]);
        }
        Raise(nameof(Stages));
    }
    /// <summary>The state half of a stage: everything a build carries between acts, and nothing of the
    /// stage's own identity. Split out from <see cref="CopyStage"/> so inheriting a stage cannot rename or
    /// re-identify it by accident.</summary>
    private static BuildProgressionStage InheritState(BuildProgressionStage from, BuildProgressionStage into) => into with
    {
        CharacterClass = from.CharacterClass, Level = from.Level, ProgressStage = from.ProgressStage,
        Equipment = from.Equipment?.Copy(), Skills = from.Skills?.Copy(), Tree = from.Tree?.Copy(),
        Reservation = from.Reservation, LowLife = from.LowLife, Conditions = from.Conditions, Defence = from.Defence,
        QuestRewards = from.QuestRewards is null ? null : [.. from.QuestRewards]
    };
    public void ActivateStage(Guid id)
    {
        if (!IsValid || !_stages.Any(s => s.Id == id) || id == _activeStageId) return;
        CaptureActiveStage(); _activeStageId = id; InitializeThrough(_stages.FindIndex(s => s.Id == id));
        LoadStage(_stages.First(s => s.Id == id));
        IsDirty = true; Raise(nameof(CurrentStageId)); Raise(nameof(CurrentStageName)); Raise(nameof(Stages)); Raise(nameof(CanChangeStage));
        Raise(nameof(TreeSnapshot)); Raise(nameof(EquipmentSnapshot)); Raise(nameof(SkillsSnapshot));
        StageActivated?.Invoke();
    }
    public bool AddStage(string name)
    {
        if (!IsValid || _stages.Count >= 32 || !ValidStageName(name)) return false;
        CaptureActiveStage();
        if (_stages.Any(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))) return false;
        int index = _stages.FindIndex(s => s.Id == _activeStageId) + 1;
        var next = CopyStage(_stages[index - 1]) with { Id = Guid.NewGuid(), Name = name.Trim(), IsInitialized = false };
        _stages.Insert(index, next); ActivateStage(next.Id); return true;
    }
    public bool RenameStage(Guid id, string name)
    {
        if (!ValidStageName(name) || !_stages.Any(s => s.Id == id) ||
            _stages.Any(s => s.Id != id && s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))) return false;
        int index = _stages.FindIndex(s => s.Id == id);
        _stages[index] = _stages[index] with { Name = name.Trim() };
        Changed(); Raise(nameof(Stages)); Raise(nameof(CurrentStageName)); return true;
    }
    public bool RemoveStage(Guid id)
    {
        if (!IsValid || _stages.Count <= 1 || !_stages.Any(s => s.Id == id)) return false;
        bool removedActive = id == _activeStageId;
        CaptureActiveStage();
        int index = _stages.FindIndex(s => s.Id == id);
        _stages.RemoveAt(index);
        if (!_stages[0].IsInitialized) _stages[0] = _stages[0] with { IsInitialized = true };
        if (id == _activeStageId)
        {
            _activeStageId = _stages[Math.Min(index, _stages.Count - 1)].Id;
            InitializeThrough(_stages.FindIndex(s => s.Id == _activeStageId));
            LoadStage(_stages.First(s => s.Id == _activeStageId));
            Raise(nameof(CurrentStageId)); Raise(nameof(CurrentStageName));
            Raise(nameof(TreeSnapshot)); Raise(nameof(EquipmentSnapshot)); Raise(nameof(SkillsSnapshot));
        }
        IsDirty = true; Raise(nameof(Stages));
        if (removedActive) StageActivated?.Invoke();
        return true;
    }
    private static bool ValidStageName(string name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 40;
    private void InitializeThrough(int index)
    {
        for (int current = 1; current <= index; current++)
        {
            if (_stages[current].IsInitialized) continue;
            _stages[current] = InheritState(_stages[current - 1], _stages[current]);
        }
    }
    private static BuildProgressionStage CopyStage(BuildProgressionStage stage) => stage with
    {
        Equipment = stage.Equipment?.Copy(), Skills = stage.Skills?.Copy(), Tree = stage.Tree?.Copy(),
        QuestRewards = stage.QuestRewards is null ? null : [.. stage.QuestRewards]
    };
    private void CaptureActiveStage()
    {
        int index = _stages.FindIndex(s => s.Id == _activeStageId);
        if (index < 0) return;
        int.TryParse(_levelText, out int level);
        _stages[index] = _stages[index] with
        {
            CharacterClass = _characterClass, Level = level is >= 1 and <= 100 ? level : _stages[index].Level,
            ProgressStage = _progressStage, Equipment = _equipment?.Copy(), Skills = _skills?.Copy(), Tree = _tree?.Copy(),
            Reservation = _reservation, LowLife = _lowLife, Defence = _defence,
            QuestRewards = _questRewards is null ? null : [.. _questRewards], Conditions = _conditions
        };
    }
    private void LoadStage(BuildProgressionStage stage)
    {
        _loadingStage = true;
        _characterClass = stage.CharacterClass; _levelText = stage.Level.ToString(); _progressStage = stage.ProgressStage;
        _equipment = stage.Equipment?.Copy(); _skills = stage.Skills?.Copy(); _tree = stage.Tree?.Copy();
        _reservation = stage.Reservation; _lowLife = stage.LowLife; _defence = stage.Defence;
        _questRewards = stage.QuestRewards is { Length: > 0 } ? [.. stage.QuestRewards] : null;
        _conditions = stage.Conditions; _loadingStage = false;
        Raise(nameof(CharacterClass)); Raise(nameof(LevelText)); Raise(nameof(ProgressStage)); Raise(nameof(StageEndgame));
        Raise(nameof(EquipmentSnapshot)); Raise(nameof(SkillsSnapshot)); Raise(nameof(TreeSnapshot));
        Raise(nameof(QuestRewardsSnapshot)); Raise(nameof(ConditionsSnapshot)); Raise(nameof(IsValid));
    }
    public BuildDocument ToDocument()
    {
        if (!IsValid) throw new BuildFormatException("Invalid build metadata.");
        CaptureActiveStage();
        return _baseline with { SchemaVersion = 6, Equipment = _equipment?.Copy(), Skills = _skills?.Copy(), Tree = _tree?.Copy(), Reservation = _reservation, LowLife = _lowLife, Defence = _defence, Name = Name.Trim(), CharacterClass = CharacterClass, Level = int.Parse(LevelText), GameVersion = GameVersion, Notes = Notes, NotesRtf = _notesRtf, ProgressStage = _progressStage, QuestRewards = _questRewards, Conditions = _conditions, Stages = _stages.Select(CopyStage).ToList(), ActiveStageId = _activeStageId };
    }
    public void AcceptSaved(BuildDocument document)
    {
        _baseline = document; _name = document.Name; Raise(nameof(Name)); IsDirty = false;
    }
}
