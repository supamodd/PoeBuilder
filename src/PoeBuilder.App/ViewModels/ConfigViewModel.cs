using PoeBuilder.Core.Models;
using Localization = PoeBuilder.App.Services.Localization;

namespace PoeBuilder.App.ViewModels;

/// <summary>The Configuration tab: PoB2's build config, restricted to the options this calculator actually
/// reads. Every field here is consumed by the character sheet, so nothing in the tab is decorative —
/// a PoB2 option whose mechanics are not modelled yet is listed in the "not modelled" note instead of
/// being offered as a switch that would do nothing.</summary>
public sealed class ConfigViewModel : Observable
{
    public Localization L { get; }
    private BuildEditor? _editor;
    private bool _suppress;
    private string _status = "";
    private bool _moving, _critRecently, _beenHitRecently;
    private bool _enemyChilled, _enemyIgnited, _enemyBleeding, _enemyShocked;
    private bool _enemyFireExposure, _enemyColdExposure, _enemyLightningExposure;
    private bool _flameWall, _arcInfused, _endgame;
    private string _enemyLevel = "", _enemyFire = "", _enemyCold = "", _enemyLightning = "", _enemyChaos = "";
    private string _enemyArmour = "", _enemyPhysicalReduction = "";

    public ConfigViewModel(Localization l)
    {
        L = l;
        ResetCommand = new ActionCommand(_ => Reload(), () => _editor is not null);
    }

    public System.Windows.Input.ICommand ResetCommand { get; }
    public bool CanEdit => _editor is not null;
    public string Warning => _editor is null ? L["NoBuildText"] : "";
    public bool HasWarning => Warning.Length > 0;
    public string Status { get => _status; private set => Set(ref _status, value); }
    /// <summary>Honest limit: PoB2's config is much wider than what our model can price. Rather than
    /// offer switches that change nothing, the tab names the groups that stay unmodelled.</summary>
    public string NotModelled => L["ConfigNotModelled"];

    public bool Endgame { get => _endgame; set { if (Set(ref _endgame, value) && _editor is not null && !_suppress) { _editor.StageEndgame = value; Push(); } } }
    public bool Moving { get => _moving; set { if (Set(ref _moving, value)) Push(); } }
    public bool CritRecently { get => _critRecently; set { if (Set(ref _critRecently, value)) Push(); } }
    public bool BeenHitRecently { get => _beenHitRecently; set { if (Set(ref _beenHitRecently, value)) Push(); } }
    public bool EnemyChilled { get => _enemyChilled; set { if (Set(ref _enemyChilled, value)) Push(); } }
    public bool EnemyIgnited { get => _enemyIgnited; set { if (Set(ref _enemyIgnited, value)) Push(); } }
    public bool EnemyBleeding { get => _enemyBleeding; set { if (Set(ref _enemyBleeding, value)) Push(); } }
    public bool EnemyShocked { get => _enemyShocked; set { if (Set(ref _enemyShocked, value)) Push(); } }
    public bool EnemyFireExposure { get => _enemyFireExposure; set { if (Set(ref _enemyFireExposure, value)) Push(); } }
    public bool EnemyColdExposure { get => _enemyColdExposure; set { if (Set(ref _enemyColdExposure, value)) Push(); } }
    public bool EnemyLightningExposure { get => _enemyLightningExposure; set { if (Set(ref _enemyLightningExposure, value)) Push(); } }
    public bool FlameWallAddedDamage { get => _flameWall; set { if (Set(ref _flameWall, value)) Push(); } }
    public bool ArcLightningInfused { get => _arcInfused; set { if (Set(ref _arcInfused, value)) Push(); } }
    public string EnemyLevelText { get => _enemyLevel; set { if (Set(ref _enemyLevel, value)) Push(); } }
    public string EnemyFireResistText { get => _enemyFire; set { if (Set(ref _enemyFire, value)) Push(); } }
    public string EnemyColdResistText { get => _enemyCold; set { if (Set(ref _enemyCold, value)) Push(); } }
    public string EnemyLightningResistText { get => _enemyLightning; set { if (Set(ref _enemyLightning, value)) Push(); } }
    public string EnemyChaosResistText { get => _enemyChaos; set { if (Set(ref _enemyChaos, value)) Push(); } }
    public string EnemyArmourText { get => _enemyArmour; set { if (Set(ref _enemyArmour, value)) Push(); } }
    public string EnemyPhysicalReductionText { get => _enemyPhysicalReduction; set { if (Set(ref _enemyPhysicalReduction, value)) Push(); } }

    public void BindEditor(BuildEditor? editor)
    {
        _editor = editor;
        Reload();
    }

    /// <summary>Loads the editor's conditions into the controls without writing anything back.</summary>
    public void Reload()
    {
        _suppress = true;
        var c = _editor?.ConditionsSnapshot ?? new BuildConditions();
        _endgame = _editor?.StageEndgame ?? false;
        _moving = c.Moving; _critRecently = c.CritRecently; _beenHitRecently = c.BeenHitRecently;
        _enemyChilled = c.EnemyChilled; _enemyIgnited = c.EnemyIgnited;
        _enemyBleeding = c.EnemyBleeding; _enemyShocked = c.EnemyShocked;
        _enemyFireExposure = c.EnemyFireExposure; _enemyColdExposure = c.EnemyColdExposure;
        _enemyLightningExposure = c.EnemyLightningExposure;
        _flameWall = c.FlameWallAddedDamage; _arcInfused = c.ArcLightningInfused;
        _enemyLevel = Text(c.EnemyLevel); _enemyFire = Text(c.EnemyFireResist); _enemyCold = Text(c.EnemyColdResist);
        _enemyLightning = Text(c.EnemyLightningResist); _enemyChaos = Text(c.EnemyChaosResist);
        _enemyArmour = Text(c.EnemyArmour); _enemyPhysicalReduction = Text(c.EnemyPhysicalDamageReduction);
        _suppress = false;
        foreach (string name in new[] { nameof(Endgame), nameof(Moving), nameof(CritRecently), nameof(BeenHitRecently),
            nameof(EnemyChilled), nameof(EnemyIgnited), nameof(EnemyBleeding), nameof(EnemyShocked),
            nameof(EnemyFireExposure), nameof(EnemyColdExposure), nameof(EnemyLightningExposure),
            nameof(FlameWallAddedDamage), nameof(ArcLightningInfused), nameof(EnemyLevelText), nameof(EnemyFireResistText),
            nameof(EnemyColdResistText), nameof(EnemyLightningResistText), nameof(EnemyChaosResistText),
            nameof(EnemyArmourText), nameof(EnemyPhysicalReductionText) }) Raise(name);
        Raise(nameof(CanEdit)); Raise(nameof(Warning)); Raise(nameof(HasWarning));
    }

    /// <summary>Writes the tab's values onto the build. A blank enemy field means PoB2's own default for
    /// the effective mode (50% elemental resistance, 0% chaos, the level's monster armour).</summary>
    private void Push()
    {
        if (_suppress || _editor is null) return;
        _editor.SetConditions(new BuildConditions
        {
            Moving = _moving, CritRecently = _critRecently, BeenHitRecently = _beenHitRecently,
            EnemyChilled = _enemyChilled, EnemyIgnited = _enemyIgnited, EnemyBleeding = _enemyBleeding,
            EnemyShocked = _enemyShocked, EnemyFireExposure = _enemyFireExposure,
            EnemyColdExposure = _enemyColdExposure, EnemyLightningExposure = _enemyLightningExposure,
            FlameWallAddedDamage = _flameWall, ArcLightningInfused = _arcInfused,
            EnemyLevel = Number(_enemyLevel), EnemyFireResist = Number(_enemyFire), EnemyColdResist = Number(_enemyCold),
            EnemyLightningResist = Number(_enemyLightning), EnemyChaosResist = Number(_enemyChaos),
            EnemyArmour = Number(_enemyArmour), EnemyPhysicalDamageReduction = Number(_enemyPhysicalReduction)
        });
        Status = L["ConfigApplied"];
    }

    private static string Text(decimal? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private static decimal? Number(string text) =>
        decimal.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
}
