using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PoeBuilder.App.Services;
using PoeBuilder.Core.Calculation;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Skills;
using PoeBuilder.Core.Tree;
using Localization = PoeBuilder.App.Services.Localization;

namespace PoeBuilder.App.ViewModels;

public sealed record StatRow(string Label, string Value, string Detail, string Tone);
public sealed record SkillSupportMini(ImageSource? Icon, string Name);

/// <summary>Read-only per-skill result line for the Character sheet (final totals, no editing here).</summary>
public sealed record SkillDetailVm(ImageSource? Icon, Brush Accent, string Name, string Kind, string LevelText, string Dps,
    string Dot, string Avg, string Rate, string Crit, string Mana, string Phys, string Fire, string Cold, string Light, string Chaos,
    IReadOnlyList<SkillSupportMini> Supports, string Notes, string Breakdown, bool HasData, string Description);

/// <summary>Character sheet (the in-game "C" screen). Recomputed from pinned data on every plan change;
/// every number traces back to a documented source, unaccounted stats are listed, never hidden.
/// This page is a RESULT view: all editing happens on Tree / Items / Skills pages.</summary>
public sealed class CharacterViewModel : Observable
{
    public Localization L { get; }
    private readonly MainViewModel _main;
    private TreeCatalog? _tree;
    private GameStatMap? _statMap;
    private GameCatalog? _catalog;
    private BuildEditor? _editor;
    private CharacterSummary? _summary;
    private string _datasetNote = "";
    public CharacterSummary? Summary => _summary;
    public string DatasetNote { get => _datasetNote; private set => Set(ref _datasetNote, value); }
    public bool HasBuild => _editor is not null;
    public string ClassName => _summary?.ClassName ?? "";
    public string LevelText => _summary is null ? "—" : _summary.Level.ToString();
    public ObservableCollection<StatRow> Resources { get; } = [];
    public ObservableCollection<StatRow> Defences { get; } = [];
    public ObservableCollection<StatRow> Resistances { get; } = [];
    public ObservableCollection<StatRow> Attributes { get; } = [];
    public ObservableCollection<SkillDetailVm> SkillDetails { get; } = [];
    public ObservableCollection<string> Unaccounted { get; } = [];
    public ObservableCollection<string> Assumptions { get; } = [];
    public string UnaccountedHeader { get; private set; } = "";

    public CharacterViewModel(Localization l, MainViewModel main)
    {
        L = l; _main = main;
        L.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Localization.Language)) Recalculate(); };
    }

    public void SetData(TreeCatalog? tree, GameStatMap? statMap, GameCatalog? catalog)
    {
        _tree = tree; _statMap = statMap; _catalog = catalog;
        Recalculate();
    }
    public void BindEditor(BuildEditor? editor)
    {
        if (_editor is not null) _editor.PropertyChanged -= EditorPropertyChanged;
        _editor = editor;
        if (_editor is not null) _editor.PropertyChanged += EditorPropertyChanged;
        Raise(nameof(HasBuild));
        Recalculate();
    }
    private void EditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The sheet must reflect every plan change immediately (equipment, skills, tree, stage).
        // One recalculation pass is the whole cost of an edit (measured at ~1 ms for the heaviest
        // pinned build, see tests/PoeBuilder.Tests/PerfTests.cs), so it runs synchronously and the
        // sheet is never stale.
        if (e.PropertyName is nameof(BuildEditor.LevelText) or nameof(BuildEditor.Name)) Raise(nameof(LevelText));
        Recalculate();
    }

    public void Recalculate()
    {
        try { RecalculateCore(); }
        catch (Exception e)
        {
            // The sheet degrades to an honest "unavailable" state; it never crashes the app.
            ErrorLog.Append(e, "CharacterRecalc");
            Resources.Clear(); Defences.Clear(); Resistances.Clear(); Attributes.Clear(); SkillDetails.Clear(); Unaccounted.Clear(); Assumptions.Clear();
            _summary = null;
            DatasetNote = L["CharacterCalcFailed"] + " · " + e.Message;
            Raise(nameof(Summary)); Raise(nameof(ClassName)); Raise(nameof(LevelText)); Raise(nameof(UnaccountedHeader)); Raise(nameof(DatasetNote));
        }
    }
    private static string N(decimal v) => v.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);

    private void RecalculateCore()
    {
        Resources.Clear(); Defences.Clear(); Resistances.Clear(); Attributes.Clear(); SkillDetails.Clear(); Unaccounted.Clear(); Assumptions.Clear();
        var build = _editor?.ToDocument();
        if (build is null || _catalog is null || _tree is null || _statMap is null)
        {
            _summary = null; DatasetNote = L["NoBuildText"];
            Raise(nameof(Summary)); Raise(nameof(ClassName)); Raise(nameof(LevelText)); Raise(nameof(UnaccountedHeader)); Raise(nameof(DatasetNote));
            return;
        }
        var s = CharacterCalculator.Calculate(build, _tree, _statMap, _catalog);
        _summary = s;
        CalculationHub.Publish(s);

        Resources.Add(new(L["CharLife"], N(s.Life), "+12 " + L["PerLevelShort"] + " · +2 " + L["PerStrengthShort"], "life"));
        Resources.Add(new(L["CharMana"], N(s.Mana), "+4 " + L["PerLevelShort"] + " · +2 " + L["PerIntelligenceShort"], "mana"));
        Resources.Add(new(L["CharEnergyShield"], N(s.EnergyShield), s.EsRechargePerSecond > 0
            ? L.Format("EsRecharge", N(s.EsRechargePerSecond), s.EsRechargeDelaySeconds is decimal delay ? N(delay) : "—") : "", "es"));
        Resources.Add(new(L["CharWard"], N(s.Ward), "", "ward"));
        Resources.Add(new(L["CharSpirit"], N(s.Spirit), "", "spirit"));
        AddReservationRow("CharLifeUnreserved", s.LifeReservation);
        AddReservationRow("CharManaUnreserved", s.ManaReservation);
        AddReservationRow("CharSpiritUnreserved", s.SpiritReservation);
        void AddReservationRow(string key, ResourceReservation? reservation)
        {
            if (reservation is not { } resolved) return;
            Resources.Add(new(L[key], N(resolved.Unreserved),
                L.Format("ReservationNote", N(resolved.Reserved), N(resolved.Maximum), N(resolved.ReservedPercent)), "none"));
        }
        Defences.Add(new(L["CharArmour"], N(s.Armour), s.PhysicalReductionEstimate is decimal dr
            ? L.Format("ArmourEstimate", dr.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture), s.EstimateMonsterLevel) : "", "none"));
        Defences.Add(new(L["CharEvasion"], N(s.Evasion), L["EvasionNote"], "none"));
        Defences.Add(new(L["CharAccuracy"], N(s.Accuracy), "+6 " + L["PerLevelShort"] + " · +6 " + L["PerDexterityShort"], "none"));
        Defences.Add(new(L["CharHitChance"], s.HitChancePercent is decimal h ? N(h) + "%" : "—",
            s.HitChancePercent is decimal ? L.Format("HitChanceNote", s.EstimateMonsterLevel) : "", "none"));
        Defences.Add(new(L["CharMonsterHitChance"], s.MonsterHitChancePercent is decimal mh ? N(mh) + "%" : "—",
            s.MonsterHitChancePercent is decimal ? L.Format("MonsterHitChanceNote", s.EstimateMonsterLevel) : "", "none"));
        Defences.Add(new(L["CharBlock"], s.BlockChance is decimal b ? N(b) + "%" : "—",
            s.BlockChance is decimal ? L.Format("BlockNote", N(s.BlockChanceMax)) : "", "none"));
        if (s.SpellBlockChance is decimal spellBlock)
            Defences.Add(new(L["CharSpellBlock"], N(spellBlock) + "%",
                L.Format("SpellBlockNote", N(s.SpellBlockChanceMax)), "none"));
        if (s.AttackDodgeChancePercent is decimal attackDodge)
            Defences.Add(new(L["CharDodge"], N(attackDodge) + "%", L["DodgeNote"], "none"));
        if (s.SpellDodgeChancePercent is decimal spellDodge)
            Defences.Add(new(L["CharSpellDodge"], N(spellDodge) + "%", L["SpellDodgeNote"], "none"));
        if (s.SpellSuppressionChancePercent is decimal suppressionChance)
            Defences.Add(new(L["CharSuppression"], N(suppressionChance) + "%",
                L.Format("SuppressionNote", N(suppressionChance), N(s.SpellSuppressionEffectPercent ?? 0)), "none"));
        string deflectionValue = s.DeflectionChancePercent is decimal dc ? N(dc) + "%"
            : s.DeflectionRating > 0 ? N(s.DeflectionRating) : "—";
        Defences.Add(new(L["CharDeflection"], deflectionValue,
            s.DeflectionRating > 0 ? L.Format("DeflectionNote", N(s.DeflectionRating), N(s.DeflectionDamagePreventedPercent)) : "", "none"));
        Defences.Add(new(L["CharMoveSpeed"], N(s.MoveSpeedPercent) + "%", "", s.MoveSpeedPercent < 100 ? "danger" : "none"));
        Defences.Add(new(L["CharLifeRegen"], N(s.LifeRegenPerSecond) + " " + L["PerSecondShort"], "", "none"));
        Defences.Add(new(L["CharManaRegen"], N(s.ManaRegenPerSecond) + " " + L["PerSecondShort"],
            L["ManaRegenNote"], "none"));
        foreach (var ehp in s.EhpEstimates)
        {
            string value = ehp.EffectiveHitPool is decimal pool ? N(pool) : "∞";
            Defences.Add(new(L["Ehp" + ehp.DamageType], value,
                L.Format("EhpNote", s.EstimateMonsterLevel, N(ehp.RawHit), N(ehp.Pool)), "none"));
        }
        if (s.ExpectedAttackEhp is { } expectedAttack)
        {
            string value = expectedAttack.EffectiveHitPool is decimal pool ? N(pool) : "∞";
            Defences.Add(new(L["EhpExpectedAttack"], value,
                L.Format("ExpectedEhpNote", N(expectedAttack.HitChancePercent),
                    N(expectedAttack.BlockChancePercent), N(expectedAttack.DeflectionChancePercent),
                    N(expectedAttack.AttackDodgeChancePercent)), "none"));
        }
        if (s.ExpectedSpellEhp is { } expectedSpell)
        {
            string value = expectedSpell.EffectiveHitPool is decimal pool ? N(pool) : "∞";
            Defences.Add(new(L["EhpExpectedSpell"], value,
                L.Format("ExpectedSpellEhpNote", expectedSpell.DamageType,
                    N(expectedSpell.HitChancePercent), N(expectedSpell.SpellBlockChancePercent),
                    N(expectedSpell.SuppressionChancePercent), N(expectedSpell.SpellDodgeChancePercent)), "none"));
        }

        // The value is the effective player resistance (stage baseline + raw sources, upper-capped), and the
        // overcapped part is printed beside it exactly like PoB2's own panel ("75% (+14%)"). The row detail
        // keeps the uncapped total and the raw source sum, so a build whose gear overshoots the cap can no
        // longer look like a resistance of 149%.
        decimal elementalPenalty = build.ProgressStage == "endgame" ? -CharacterCalculator.EndgameElementalPenalty : 0;
        Resistances.Add(ResRow(L["ResFire"], s.FireRes, s.FireResSources, elementalPenalty, s.FireResMax));
        Resistances.Add(ResRow(L["ResCold"], s.ColdRes, s.ColdResSources, elementalPenalty, s.ColdResMax));
        Resistances.Add(ResRow(L["ResLightning"], s.LightRes, s.LightResSources, elementalPenalty, s.LightResMax));
        Resistances.Add(ResRow(L["ResChaos"], s.ChaosRes, s.ChaosResSources, 0, s.ChaosResMax));

        Attributes.Add(new(L["AttrStrength"], N(s.Strength), "+2 " + L["CharLife"].ToLowerInvariant() + " " + L["PerPoint"], "str"));
        Attributes.Add(new(L["AttrDexterity"], N(s.Dexterity), "+6 " + L["CharAccuracy"].ToLowerInvariant() + " " + L["PerPoint"], "dex"));
        Attributes.Add(new(L["AttrIntelligence"], N(s.Intelligence), "+2 " + L["CharMana"].ToLowerInvariant() + " " + L["PerPoint"], "int"));

        var groupsById = build.Skills?.Groups.ToDictionary(g => g.Id) ?? new Dictionary<Guid, SkillGroup>();
        foreach (var skill in s.Skills)
        {
            var gem = _catalog.Gems.GetValueOrDefault(skill.GemId);
            string kind = skill.IsAttack ? L["KindAttack"] : gem?.Tags.Contains("spell") == true ? L["KindSpell"] : gem?.Kind == "spirit" ? L["KindSpirit"] : L["KindOther"];
            if (skill.LevelFromItems > 0) kind += " · " + L.Format("LevelFromItems", skill.LevelFromItems);
            var notes = skill.NoteCodes.Select(code => L["Note" + code]).Where(t => t.Length > 0 && !t.StartsWith("Note"));
            var supports = new List<SkillSupportMini>();
            if (groupsById.TryGetValue(skill.GroupId, out var group))
                foreach (var selection in group.Supports)
                    if (_catalog.Gems.TryGetValue(selection.GemId, out var support))
                        supports.Add(new(IconService.Instance.ForGem(support.Id), support.Name));
            SkillDetails.Add(new(
                IconService.Instance.ForGem(skill.GemId), AccentFor(gem?.Color), skill.GemName, kind,
                // The effective (post-global-bonus) values of the gem that deals the damage: for a host
                // group such as "Spell Totem hosting Arc" that is Arc, not the totem.
                skill.EffectiveLevel > 0 ? skill.EffectiveLevel.ToString() : "—",
                // Effective DPS by default (PoB2's and poe.ninja's figure); the raw value is in the breakdown.
                skill.HasData ? N(skill.EffectiveDps > 0 ? skill.EffectiveDps : skill.Dps) : "—",
                skill.TotalDotDps is decimal dotTotal && dotTotal > 0 ? "DoT " + N(dotTotal) : "",
                skill.HasData ? N(skill.AvgHit) : "—",
                skill.HasData ? N(skill.HitsPerSecond) : "—",
                skill.HasData ? N(skill.CritChancePercent) + "% / +" + N(skill.CritBonusPercent) + "%" + (skill.EffectiveCritChancePercent is decimal effCrit ? " (eff " + N(effCrit) + "%)" : "") : "—",
                skill.ManaCost is decimal cost ? N(cost) : "—",
                skill.HasData ? N(skill.Split.Physical) : "—", skill.HasData ? N(skill.Split.Fire) : "—",
                skill.HasData ? N(skill.Split.Cold) : "—", skill.HasData ? N(skill.Split.Lightning) : "—",
                skill.HasData ? N(skill.Split.Chaos) : "—",
                supports, string.Join(" · ", notes), string.Join("\n", skill.Breakdown), skill.HasData,
                DescribeGem(L, gem, skill.EffectiveLevel > 0 ? skill.EffectiveLevel : 1, skill.EffectiveQuality)));
        }

        UnaccountedHeader = L.Format("UnaccountedHeader", s.UnaccountedTotal);
        foreach (var id in s.Unaccounted.OrderByDescending(p => p.Value).Take(10).Select(p => p.Key))
            Unaccounted.Add(id);
        Assumptions.Add(L["AssumptionLevel"]);
        Assumptions.Add(L["AssumptionAttributes"]);
        Assumptions.Add(L["AssumptionCrit"]);
        Assumptions.Add(L["AssumptionArmour"]);
        Assumptions.Add(L["AssumptionBlock"]);
        Assumptions.Add(L["AssumptionSuppression"]);
        Assumptions.Add(L["AssumptionDodge"]);
        Assumptions.Add(L["AssumptionDeflection"]);
        Assumptions.Add(L["AssumptionLifeRecovery"]);
        Assumptions.Add(L["AssumptionEsRecharge"]);
        Assumptions.Add(L["AssumptionHitChance"]);
        Assumptions.Add(L["AssumptionEhp"]);
        Assumptions.Add(build.ProgressStage == "endgame" ? L["AssumptionResEndgame"] : L["AssumptionRes"]);
        Assumptions.Add(L["AssumptionSupports"]);
        Assumptions.Add(L["AssumptionConversion"]);
        Assumptions.Add(L["AssumptionQuality"]);
        if (s.Extras.TryGetValue("UniqueTextMods", out var uniqueModLines) && uniqueModLines > 0)
            Assumptions.Add(L.Format("AssumptionUniqueText", uniqueModLines));
        DatasetNote = L["CharacterDataNotice"];
        Raise(nameof(Summary)); Raise(nameof(ClassName)); Raise(nameof(LevelText)); Raise(nameof(UnaccountedHeader)); Raise(nameof(DatasetNote));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>PoB2-style resistance row: the capped value with the overcapped part printed beside it
    /// ("75% (+14%)", exactly what its panel shows) and a detail line naming the cap, the uncapped total
    /// (stage baseline + raw sources) and the raw source sum.</summary>
    private StatRow ResRow(string label, decimal effective, decimal sources, decimal penalty, decimal maximum)
    {
        var row = ResistanceCalculator.Display(effective, sources, penalty, maximum);
        string detail = L.Format("ResMax", N(row.Maximum)) + " · " + L.Format("ResTotal", N(row.Total));
        if (sources != 0) detail += " · " + L.Format("ResFromSources", (sources > 0 ? "+" : "") + N(sources));
        return new(label, row.Value, detail, row.Tone);
    }

    /// <summary>PoB2-style node tooltip contribution: what allocating this tree node changes in the
    /// character sheet (DPS, speed, crit, pools, defences, resists) — and, for a node that is already
    /// allocated, what refunding it would take away. Only this node is recomputed against the current
    /// document; a branch that a refund would prune reports nothing rather than a misleading delta for
    /// one node. Returns null when the node cannot be reached or has no computable contribution.</summary>
    public string? NodeImpact(int nodeId)
    {
        if (_editor is null || !_editor.IsValid || _tree is null || _statMap is null || _catalog is null || _summary is null) return null;
        var doc = _editor.ToDocument();
        if (doc.Tree is null || !_tree.Nodes.TryGetValue(nodeId, out var node) || node.IsStart) return null;
        var engine = new PassiveTreeEngine(_tree);
        bool allocated = doc.Tree.AllocatedNodes.Contains(nodeId);
        PassiveTreePlan plan;
        try
        {
            // Refunding a node that other nodes hang from removes the whole branch, so the delta would not
            // describe this node at all; the tooltip then keeps the node's own stats only. A saved tree that
            // fails its own rules walks the same way and reports nothing either — a hover is a mouse move,
            // not a place to throw at the window.
            if (allocated && engine.RefundSet(doc.Tree, nodeId).Length != 1) return null;
            plan = allocated ? engine.Refund(doc.Tree, nodeId) : engine.Allocate(doc.Tree, nodeId, 26297);
        }
        catch (TreeRuleException) { return null; }
        var next = CharacterCalculator.Calculate(doc with { Tree = plan }, _tree, _statMap, _catalog);

        var parts = new List<string>();
        decimal dps = Total(next) - Total(_summary);
        if (dps != 0) parts.Add(L["ImpactDps"] + " " + Sign(dps));
        if (Top(_summary) is { } before && Top(next) is { } after)
        {
            // A rate/crit delta is only meaningful against the same skill, which is what the top group is.
            if (before.GroupId == after.GroupId)
            {
                decimal rate = after.HitsPerSecond - before.HitsPerSecond;
                if (Math.Abs(rate) >= 0.005m) parts.Add(L["ImpactRate"] + " " + Sign(rate) + "/s");
                decimal crit = after.CritChancePercent - before.CritChancePercent;
                if (Math.Abs(crit) >= 0.01m) parts.Add(L["ImpactCrit"] + " " + Sign(crit) + "%");
            }
        }
        Add("ImpactLife", next.Life, _summary.Life, "");
        Add("ImpactMana", next.Mana, _summary.Mana, "");
        Add("ImpactEs", next.EnergyShield, _summary.EnergyShield, "");
        Add("ImpactWard", next.Ward, _summary.Ward, "");
        Add("ImpactSpirit", next.Spirit, _summary.Spirit, "");
        Add("ImpactArmour", next.Armour, _summary.Armour, "");
        Add("ImpactEvasion", next.Evasion, _summary.Evasion, "");
        Add("ImpactAccuracy", next.Accuracy, _summary.Accuracy, "");
        Add("ImpactFire", next.FireRes, _summary.FireRes, "%");
        Add("ImpactCold", next.ColdRes, _summary.ColdRes, "%");
        Add("ImpactLightning", next.LightRes, _summary.LightRes, "%");
        Add("ImpactChaos", next.ChaosRes, _summary.ChaosRes, "%");
        if (parts.Count == 0) return L["NodeImpactNone"];
        return (allocated ? L["ImpactAllocated"] : L["NodeImpactDefault"]) + "\n" + string.Join(" · ", parts);

        void Add(string key, decimal value, decimal current, string suffix)
        {
            decimal delta = value - current;
            if (delta != 0) parts.Add(L[key] + " " + Sign(delta) + suffix);
        }
        static decimal Total(CharacterSummary summary) =>
            summary.Skills.Sum(s => s.EffectiveDps > 0 ? s.EffectiveDps : s.Dps);
        static SkillDpsInfo? Top(CharacterSummary summary) =>
            summary.Skills.Where(s => s.Dps > 0).OrderByDescending(s => s.Dps).FirstOrDefault();
        static string Sign(decimal v) => (v > 0 ? "+" : "") + N(v);
    }
    /// <summary>In-game-like tooltip: description plus the pinned per-level stat lines of the current level.
    /// Quality is shown as metadata; the pinned math does not consume gem quality yet (disclosed).
    /// The pinned source is English; verified Russian wording does not exist yet.</summary>
    internal static string DescribeGem(Localization L, Gem? gem, int level, int quality = 0)
    {
        if (gem is null) return "";
        var parts = new List<string>();
        var desc = gem.Description.Trim();
        if (desc.Length > 0) parts.Add(desc);
        // Quality may exceed the gem's own 20% once global "+N% to Quality of all Skills" mods are
        // folded in, so the clamp is wider than the item-quality range.
        parts.Add(L.Format(gem.Kind == "support" ? "GemMetaSupport" : "GemMetaActive", Math.Clamp(level, 1, 40), Math.Clamp(quality, 0, 100)));
        var texts = gem.Skill?.StatText;
        if (texts is not null)
        {
            for (int candidate = Math.Min(level, 40); candidate >= 1; candidate--)
            {
                if (texts.TryGetValue(candidate.ToString(), out var lines) && lines.Count > 0)
                {
                    parts.Add(string.Join("\n", lines.Values.Where(v => !string.IsNullOrWhiteSpace(v))));
                    break;
                }
            }
        }
        return string.Join("\n\n", parts);
    }

    private static Brush AccentFor(string? color)
    {
        var hex = color switch { "r" or "s" => "#C5443C", "g" => "#4FAE54", "b" => "#4C7FD0", _ => "#7A8794" };
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze(); return brush;
    }
}
