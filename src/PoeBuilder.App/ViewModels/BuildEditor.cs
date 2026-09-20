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
    private EquipmentPlan? _equipment;
    private SkillPlan? _skills;
    public EquipmentPlan? EquipmentSnapshot => _equipment?.Copy();
    public SkillPlan? SkillsSnapshot => _skills?.Copy();
    public void SetEquipment(EquipmentPlan plan) { _equipment = plan.Copy(); Changed(); Raise(nameof(EquipmentSnapshot)); }
    public void SetSkills(SkillPlan plan) { _skills = plan.Copy(); Changed(); Raise(nameof(SkillsSnapshot)); }
    private PassiveTreePlan? _tree;
    public PassiveTreePlan? TreeSnapshot => _tree?.Copy();
    public void SetTree(PassiveTreePlan plan) { _tree = plan.Copy(); Changed(); Raise(nameof(TreeSnapshot)); }
    public BuildEditor(BuildDocument document, bool isNew = false)
    {
        _baseline = document; _name = document.Name; _characterClass = document.CharacterClass;
        _levelText = document.Level.ToString(); _gameVersion = document.GameVersion; _notes = document.Notes; _progressStage = document.ProgressStage;
        _isDirty = isNew; _tree = document.Tree?.Copy(); _equipment = document.Equipment?.Copy(); _skills = document.Skills?.Copy();
    }
    public Guid Id => _baseline.Id;
    public string Name { get => _name; set { if (Set(ref _name, value)) Changed(); } }
    public string CharacterClass { get => _characterClass; set { if (Set(ref _characterClass, value)) Changed(); } }
    public string LevelText { get => _levelText; set { if (Set(ref _levelText, value)) Changed(); } }
    public string GameVersion { get => _gameVersion; set { if (Set(ref _gameVersion, value)) Changed(); } }
    public string ProgressStage { get => _progressStage; set { if (Set(ref _progressStage, value)) Changed(); } }
    public bool StageEndgame { get => _progressStage == "endgame"; set { if (Set(ref _progressStage, value ? "endgame" : "starter")) Changed(); } }
    public string Notes { get => _notes; set { if (Set(ref _notes, value)) Changed(); } }
    public bool IsDirty { get => _isDirty; private set => Set(ref _isDirty, value); }
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && Name.Length <= 80 &&
        int.TryParse(LevelText, out var level) && level is >= 1 and <= 100 &&
        CharacterClass.Length <= 80 && GameVersion.Length <= 32 && Notes.Length <= 100_000;
    private void Changed() { IsDirty = true; Raise(nameof(IsValid)); }
    public BuildDocument ToDocument()
    {
        if (!IsValid) throw new BuildFormatException("Invalid build metadata.");
        return _baseline with { SchemaVersion = 4, Equipment = _equipment?.Copy(), Skills = _skills?.Copy(), Tree = _tree?.Copy(), Name = Name.Trim(), CharacterClass = CharacterClass, Level = int.Parse(LevelText), GameVersion = GameVersion, Notes = Notes, ProgressStage = _progressStage };
    }
    public void AcceptSaved(BuildDocument document)
    {
        _baseline = document; _name = document.Name; Raise(nameof(Name)); IsDirty = false;
    }
}
