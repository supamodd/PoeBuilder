using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Models;
using PoeBuilder.Core.Skills;
using PoeBuilder.Core.Tree;

namespace PoeBuilder.Core.Calculation;

public sealed record DamageSplit(decimal Physical, decimal Fire, decimal Cold, decimal Lightning, decimal Chaos)
{
    public decimal Total => Physical + Fire + Cold + Lightning + Chaos;
}

public sealed record SkillDpsInfo(Guid GroupId, string GroupName, string GemId, string GemName, bool IsAttack, bool EnabledForSet,
    bool HasData, decimal Dps, decimal AvgHit, DamageSplit Split, decimal HitsPerSecond, decimal CritChancePercent,
    decimal CritBonusPercent, decimal? ManaCost, string[] NoteCodes, int LevelFromItems);

public sealed record CharacterSummary(
    int Level, string ClassName, bool HasTreeData, bool HasGameData, bool HasStatMap,
    decimal Life, decimal Mana, decimal EnergyShield, decimal Spirit,
    decimal Strength, decimal Dexterity, decimal Intelligence,
    decimal Armour, decimal Evasion, decimal Accuracy, decimal? HitChancePercent, decimal? MonsterHitChancePercent,
    decimal? BlockChance, decimal DeflectionRating,
    decimal FireRes, decimal ColdRes, decimal LightRes, decimal ChaosRes,
    decimal FireResSources, decimal ColdResSources, decimal LightResSources, decimal ChaosResSources,
    decimal MoveSpeedPercent, decimal LifeRegenPerSecond, decimal EsRechargePerSecond,
    decimal? PhysicalReductionEstimate, int EstimateMonsterLevel,
    IReadOnlyList<SkillDpsInfo> Skills,
    IReadOnlyDictionary<string, decimal> Extras, IReadOnlyDictionary<string, int> Unaccounted, int UnaccountedTotal);

/// <summary>
/// Independent v1 calculator. Sources: pinned RePoE 4.5.5.2 values (item bases, implicits, rolls, gem
/// per-level stats, tree lines via the pinned statmap). Per-level growth +12 life / +4 mana / +6 accuracy /
/// +3 evasion, attributes +2 life (Str) / +5 accuracy (Dex) / +2 mana (Int), armour DR = A/(A+12·hit)
/// capped at 90%, ES recharge 12.5%/s, player resistance = stage baseline + raw sources with a 75%
/// upper cap (raisable by maximum-resistance modifiers). Armour ratio, growth constants and the exact
/// target-patch resistance rules remain verification items until backed by a pinned PoB/data fixture.
/// Base Critical Damage Bonus 100% (crits deal 2x by default). Explicitly NOT included (reported, never
/// hidden): buffs/charges/ailments, enemy defences, in-skill damage conversion and conditional
/// stats. Ordinary stat lines from allocated ascendancy nodes are included; special ascendancy mechanics remain unsupported.
/// </summary>
public static class CharacterCalculator
{
    public const decimal LifePerLevel = 12, ManaPerLevel = 4, AccuracyPerLevel = 6, EvasionPerLevel = 3;
    public const decimal LifePerStrength = 2, AccuracyPerDexterity = 5, ManaPerIntelligence = 2;
    public const decimal BaseCritDamageBonus = 100;
    public const decimal ArmourConstant = 12, ArmourCapPercent = 90;
    public const decimal EsRechargePercentPerSecond = 12.5m;
    public const decimal ResistanceCap = 75;
    public const decimal EndgameElementalPenalty = 40;

    public static CharacterSummary Calculate(BuildDocument build, TreeCatalog? tree, GameStatMap? statMap, GameCatalog? catalog)
    {
        int level = Math.Clamp(build.Level, 1, 100);
        var bucket = new StatBucket();

        // --- Class base attributes from the pinned tree export ---
        string className = build.CharacterClass;
        decimal baseStr = 0, baseDex = 0, baseInt = 0;
        TreeClass? treeClass = null;
        if (tree is not null && build.Tree is not null)
            treeClass = tree.Classes.FirstOrDefault(c => c.Index == build.Tree.ClassIndex) ?? tree.Classes.FirstOrDefault();
        if (treeClass is not null)
        {
            className = treeClass.Name;
            baseStr = treeClass.BaseStrength; baseDex = treeClass.BaseDexterity; baseInt = treeClass.BaseIntelligence;
        }

        // --- Passive tree and ascendancy lines ---
        if (tree is not null && statMap is not null && build.Tree is not null)
        {
            int start = tree.Classes.FirstOrDefault(c => c.Index == build.Tree.ClassIndex)?.StartNodeId ?? -1;
            ApplyTreeStats(bucket, statMap, tree, build.Tree, build.Tree.AllocatedNodes.Append(start));

            if (build.Tree.Ascendancy is { } ascendancyPlan)
            {
                var definition = tree.Ascendancies.FirstOrDefault(a =>
                    a.Id == ascendancyPlan.Id && a.ClassIndex == build.Tree.ClassIndex);
                if (definition is not null)
                {
                    var graphPlan = definition.ToGraphPlan(ascendancyPlan);
                    int ascendancyStart = definition.Graph.Classes.FirstOrDefault(c => c.Index == build.Tree.ClassIndex)?.StartNodeId ?? -1;
                    ApplyTreeStats(bucket, statMap, definition.Graph, graphPlan,
                        ascendancyPlan.AllocatedNodes.Append(ascendancyStart));
                }
            }
        }

        // --- Equipment (active weapon set + always-on slots) ---
        string[] alwaysSlots = ["Helmet", "Body", "Gloves", "Boots", "Belt", "Amulet", "Ring1", "Ring2", "LifeFlask", "ManaFlask", "Charm1", "Charm2", "Charm3"];
        decimal shieldBlock = 0;
        GearItem? mainHand = null;
        if (catalog is not null && build.Equipment is not null)
        {
            var plan = build.Equipment;
            int set = plan.WeaponSet == 2 ? 2 : 1;
            foreach (var slot in alwaysSlots.Append("Main" + set).Append("Off" + set))
            {
                if (!plan.Slots.TryGetValue(slot, out var itemId)) continue;
                var gear = plan.Items.FirstOrDefault(i => i.Id == itemId);
                if (gear is null || !catalog.Bases.TryGetValue(gear.BaseId, out var b)) continue;
                if (slot == "Main" + set) mainHand = gear;
                var item = new ItemContext();
                StatInterpreter.ApplyAll(bucket, ImplicitValues(b), item);
                foreach (var roll in gear.Mods.Concat(gear.CorruptedMods))
                {
                    if (!catalog.Mods.TryGetValue(roll.Id, out var mod)) continue;
                    for (int i = 0; i < mod.Stats.Length && i < roll.Values.Length; i++)
                        StatInterpreter.Apply(bucket, mod.Stats[i].Id, roll.Values[i], item);
                }
                // Base defences scaled by the item's own local increases. Quality has no verified formula here yet.
                bucket.ArmourFlat += (b.Props.Armour ?? 0) * (1 + (item.ArmourInc) / 100);
                bucket.EvFlat += (b.Props.Evasion ?? 0) * (1 + (item.EvInc) / 100);
                bucket.EsFlat += (b.Props.EnergyShield ?? 0) * (1 + (item.EsInc) / 100);
                bucket.MoveInc += b.Props.MovementSpeed ?? 0;
                if ((b.Props.Block ?? 0) > 0 || item.BlockInc != 0)
                    shieldBlock += (b.Props.Block ?? 0) * (1 + item.BlockInc / 100);
                bucket.AccFlat += item.AccuracyFlat;
            }
        }

        // --- Socketed tree jewels: their jewel-pool affixes act globally. Radius-limited affixes
        // (per-node-in-radius) need a counted radius model and are honestly skipped for now. ---
        if (catalog is not null && build.Equipment is not null && build.Tree is not null && build.Tree.Jewels.Count > 0)
        {
            var jewelMods = catalog.JewelMods.ToDictionary(m => m.Id);
            foreach (var jewelId in build.Tree.Jewels.Values.Distinct())
            {
                var jewel = build.Equipment.Items.FirstOrDefault(i => i.Id == jewelId);
                if (jewel is null) continue;
                foreach (var roll in jewel.Mods)
                {
                    if (roll.Id.StartsWith("JewelRadius", StringComparison.Ordinal)) { bucket.Extras["JewelRadiusSkipped"] = bucket.Extras.TryGetValue("JewelRadiusSkipped", out var n) ? n + 1 : 1; continue; }
                    if (!jewelMods.TryGetValue(roll.Id, out var mod)) continue;
                    for (int i = 0; i < mod.Stats.Length && i < roll.Values.Length; i++)
                        StatInterpreter.Apply(bucket, mod.Stats[i].Id, roll.Values[i], new ItemContext());
                }
            }
        }

        // --- Attributes and pools ---
        decimal str = baseStr + bucket.Str, dex = baseDex + bucket.Dex, inte = baseInt + bucket.Int;
        decimal baseLife = (catalog?.Vitals.BaseLife ?? 16) + LifePerLevel * (level - 1) + LifePerStrength * str;
        if (bucket.LifePerDexRate > 0) baseLife += Math.Floor(dex / 4m) * bucket.LifePerDexRate;
        decimal life = (baseLife + bucket.Life) * (1 + bucket.LifeInc / 100);
        decimal baseMana = (catalog?.Vitals.BaseMana ?? 30) + ManaPerLevel * (level - 1) + ManaPerIntelligence * inte;
        decimal mana = (baseMana + bucket.Mana) * (1 + bucket.ManaInc / 100);
        decimal accuracy = (AccuracyPerLevel * (level - 1) + AccuracyPerDexterity * dex + bucket.AccFlat) * (1 + bucket.AccInc / 100);
        decimal evasion = (EvasionPerLevel * (level - 1) + bucket.EvFlat) * (1 + bucket.EvInc / 100);
        decimal armour = bucket.ArmourFlat * (1 + bucket.ArmourInc / 100);
        decimal es = bucket.EsFlat * (1 + bucket.EsInc / 100);
        decimal spirit = bucket.Spirit * (1 + bucket.SpiritInc / 100);
        decimal moveSpeed = 100 + bucket.MoveInc;

        // --- Skill DPS ---
        var skills = new List<SkillDpsInfo>();
        if (catalog is not null && build.Skills is not null)
        {
            int set = build.Equipment?.WeaponSet == 2 ? 2 : 1;
            ItemBase? mainBase = mainHand is not null ? catalog.Bases.GetValueOrDefault(mainHand.BaseId) : null;
            ItemContext? mainLocal = mainHand is not null ? WeaponContext(catalog, mainHand) : null;
            foreach (var group in build.Skills.Groups)
                if (SkillInfo(catalog, group, group.WeaponSet == 0 || group.WeaponSet == set, mainBase, mainLocal, bucket) is { } info)
                    skills.Add(info);
        }

        // --- Same-level default-monster estimates (pinned stats) ---
        MonsterLevel? monster = catalog?.Monsters.GetValueOrDefault(level.ToString());
        decimal? playerHitChance = monster?.Evasion is decimal targetEvasion
            ? DefenceCalculator.PlayerHitChance(targetEvasion, accuracy) : null;
        decimal? monsterHitChance = monster?.Accuracy is decimal monsterAccuracy
            ? DefenceCalculator.MonsterHitChance(evasion, monsterAccuracy) : null;
        decimal? reduction = null;
        if (monster?.PhysicalDamage is decimal monsterPhysicalDamage && monsterPhysicalDamage > 0)
        {
            decimal dr = armour / (armour + ArmourConstant * monsterPhysicalDamage) * 100;
            reduction = Math.Min(ArmourCapPercent, Math.Max(0, dr));
        }

        // Player resistance is the stage baseline plus all raw sources, capped only on the upper
        // side. Enemy resistance, penetration and exposure are deliberately not part of this result.
        var fireResistance = ResistanceCalculator.Calculate(ResBaseline(build.ProgressStage), bucket.FireRes, bucket.FireMax, ResistanceCap);
        var coldResistance = ResistanceCalculator.Calculate(ResBaseline(build.ProgressStage), bucket.ColdRes, bucket.ColdMax, ResistanceCap);
        var lightningResistance = ResistanceCalculator.Calculate(ResBaseline(build.ProgressStage), bucket.LightRes, bucket.LightMax, ResistanceCap);
        var chaosResistance = ResistanceCalculator.Calculate(0, bucket.ChaosRes, bucket.ChaosMax, ResistanceCap);

        return new CharacterSummary(level, className, tree is not null, catalog is not null, statMap is not null,
            R(life), R(mana), R(es), R(spirit),
            R(str), R(dex), R(inte),
            R(armour), R(evasion), R(accuracy), playerHitChance, monsterHitChance,
            shieldBlock > 0 ? R(shieldBlock) : null,
            R(evasion * bucket.DeflectPctOfEvasion / 100),
            R(fireResistance.Effective), R(coldResistance.Effective),
            R(lightningResistance.Effective), R(chaosResistance.Effective),
            R(fireResistance.Sources), R(coldResistance.Sources),
            R(lightningResistance.Sources), R(chaosResistance.Sources),
            R(moveSpeed), R(bucket.LifeRegenPerMin / 60, 2), R(es * EsRechargePercentPerSecond / 100, 2),
            reduction, level, skills, bucket.Extras, bucket.Unaccounted, bucket.UnaccountedTotal);

        static decimal R(decimal v, int digits = 0) => Math.Round(v, digits, MidpointRounding.AwayFromZero);
        // "starter" = campaign (resistances start at 0); "endgame" = each campaign act took -10%,
        // i.e. -40% to Fire/Cold/Lightning after the campaign. Chaos is not penalised by acts.
        static decimal ResBaseline(string stage) => stage == "endgame" ? -40m : 0;
    }

    private static void ApplyTreeStats(StatBucket bucket, GameStatMap statMap, TreeCatalog graph,
        PassiveTreePlan plan, IEnumerable<int> nodeIds)
    {
        foreach (int id in nodeIds.Distinct())
        {
            if (!graph.Nodes.ContainsKey(id)) continue;
            foreach (var line in graph.Describe(id, plan).Stats)
                if (statMap.Lines.TryGetValue(line, out var stats)) StatInterpreter.ApplyAll(bucket, stats);
        }
    }

    private static Dictionary<string, decimal> ImplicitValues(ItemBase b)
    {
        var values = new Dictionary<string, decimal>();
        foreach (var stat in b.ImplicitStats)
            values[stat.Id] = values.TryGetValue(stat.Id, out var old) ? old + stat.Max : stat.Max; // implicits default to max roll
        return values;
    }

    /// <summary>Collects only the weapon-LOCAL contributions of a weapon (they scale that weapon only).</summary>
    private static ItemContext WeaponContext(GameCatalog catalog, GearItem weapon)
    {
        var item = new ItemContext();
        if (!catalog.Bases.TryGetValue(weapon.BaseId, out var b)) return item;
        var sink = new StatBucket();
        StatInterpreter.ApplyAll(sink, ImplicitValues(b), item);
        foreach (var roll in weapon.Mods.Concat(weapon.CorruptedMods))
        {
            if (!catalog.Mods.TryGetValue(roll.Id, out var mod)) continue;
            for (int i = 0; i < mod.Stats.Length && i < roll.Values.Length; i++)
                StatInterpreter.Apply(sink, mod.Stats[i].Id, roll.Values[i], item);
        }
        return item;
    }

    private static SkillDpsInfo? SkillInfo(GameCatalog catalog, SkillGroup group, bool setMatches, ItemBase? mainBase, ItemContext? mainLocal, StatBucket bucket)
    {
        var gem = catalog.Gems.GetValueOrDefault(group.Active.GemId);
        if (gem is null) return null;
        var skill = gem.Skill;
        bool isAttack = gem.Tags.Contains("attack");
        int levelFromItems = 0;
        foreach (var (scope, value) in bucket.GemLevels) if (GemScopeMatches(scope, gem.Tags)) levelFromItems += (int)value;
        foreach (var (scope, value) in mainLocal?.GemLevels ?? new List<(string, decimal)>()) if (GemScopeMatches(scope, gem.Tags)) levelFromItems += (int)value;
        int effectiveLevel = Math.Clamp(group.Active.Level + levelFromItems, 1, 40);
        var notes = new List<string>();
        if (!group.Enabled) notes.Add("DisabledGroup");
        if (!setMatches) notes.Add("WrongWeaponSet");
        var weaponWords = WeaponWords(mainBase);
        // v4 supports: apply the support gems' pinned "_final" multipliers (data-driven, no invention).
        // Applied multipliers are collected here and folded into rate/crit/damage below.
        decimal rateMore = 1m, critChanceMore = 1m, critBonusMore = 1m;
        var damageMore = new decimal[] { 1m, 1m, 1m, 1m, 1m };
        decimal damageMoreGeneral = 1m;
        int supportsApplied = 0;
        foreach (var selection in group.Supports)
        {
            var support = catalog.Gems.GetValueOrDefault(selection.GemId);
            var statics = support?.Skill?.Statics;
            if (support is null || statics is null || statics.Count == 0) continue;
            bool any = false;
            foreach (var (id, value) in statics)
            {
                if (!id.EndsWith("_final", StringComparison.Ordinal) || value == 0) continue;
                if (id.Contains("minion", StringComparison.Ordinal) && !gem.Tags.Contains("minion")) continue;
                decimal factor = 1 + value / 100m;
                if (id.Contains("attack_speed", StringComparison.Ordinal))
                {
                    if (!isAttack) continue;
                    rateMore *= factor; any = true;
                }
                else if (id.Contains("cast_speed", StringComparison.Ordinal))
                {
                    if (isAttack) continue;
                    rateMore *= factor; any = true;
                }
                else if (id.Contains("critical_strike_chance", StringComparison.Ordinal))
                {
                    critChanceMore *= factor; any = true;
                }
                else if (id.Contains("critical_damage", StringComparison.Ordinal) || (id.Contains("critical", StringComparison.Ordinal) && id.Contains("multiplier", StringComparison.Ordinal)))
                {
                    critBonusMore *= factor; any = true;
                }
                else if (id.Contains("damage", StringComparison.Ordinal))
                {
                    // Damage-scoped final: type words in the id must match the skill (or its weapon family).
                    var words = SupportScopeWords(id);
                    int type = words.Select(w => Array.IndexOf(TypeWords, w)).Where(i => i >= 0).ToArray() is { Length: > 0 } hits ? hits[^1] : -1;
                    bool elemental = words.Contains("elemental");
                    if (words.Contains("spell") && isAttack) continue;
                    if (words.Contains("attack") && !isAttack) continue;
                    if (!words.All(w => gem.Tags.Contains(w) || weaponWords.Contains(w)))
                    {
                        if (type < 0 && !elemental) continue;
                    }
                    if (type >= 0) { damageMore[type] *= factor; any = true; }
                    else if (elemental) { damageMore[1] *= factor; damageMore[2] *= factor; damageMore[3] *= factor; any = true; }
                    else if (words.Length == 0 || words.All(w => w is "maximum" or "minimum" or "base" or "skill" or "support" or "gem")) { damageMoreGeneral *= factor; any = true; }
                    if (any && id.Contains("maximum", StringComparison.Ordinal) && !notes.Contains("SupportFinalApprox")) notes.Add("SupportFinalApprox");
                }
            }
            if (any) supportsApplied++;
        }
        if (group.Supports.Length > 0 && supportsApplied < group.Supports.Length) notes.Add("SupportsPartial");
        if (group.Supports.Length > 0 && supportsApplied > 0) notes.Add("SupportsApplied");
        if (isAttack && mainBase?.Props.IsWeapon != true)
        {
            notes.Add("NoWeapon");
            return Record(0, 0, new DamageSplit(0, 0, 0, 0, 0), 0, 0, 0, null, isAttack, notes, group, gem, setMatches, levelFromItems);
        }

        decimal dps = 0, avgHit = 0, rate = 0, critChance = 0, critBonus = BaseCritDamageBonus;
        DamageSplit split = new(0, 0, 0, 0, 0);
        decimal? manaCost = null;
        // Skill-scoped damage increases: every scope whose words are all in the gem's tags applies.
        decimal scopedGeneral = 0; var scopedType = new decimal[5];
        foreach (var (words, value) in bucket.ScopedDamage)
        {
            if (!words.All(w => gem.Tags.Contains(w) || weaponWords.Contains(w))) continue;
            int type = TypeIndex(words);
            if (type >= 0) scopedType[type] += value;
            else if (words.Contains("elemental")) { scopedType[1] += value; scopedType[2] += value; scopedType[3] += value; }
            else scopedGeneral += value;
        }

        if (skill is null)
        {
            notes.Add("NoGemData");
        }
        else
        {
            manaCost = skill.LevelCosts(effectiveLevel)?.TryGetValue("Mana", out var mc) == true ? mc : null;
            decimal speedInc = bucket.CastSpeedInc + bucket.SkillSpeedInc;
            if (isAttack)
            {
                if (mainBase?.Props.AttackTime is not int attackTime) { notes.Add("NoWeapon"); return Record(0, 0, split, 0, 0, 0, manaCost, isAttack, notes, group, gem, setMatches, levelFromItems); }
                rate = 1000m / attackTime * (1 + (bucket.AttackSpeedInc + speedInc + (mainLocal?.AttackSpeedInc ?? 0)) / 100) * rateMore;
                decimal weaponCrit = (mainBase.Props.CritChance ?? 0) / 100m + (mainLocal?.CritChanceAdd ?? 0);
                critChance = Math.Min(100, weaponCrit * (1 + (bucket.CritChanceInc + bucket.AttackCritInc) / 100) * critChanceMore);
                critBonus = (BaseCritDamageBonus + bucket.CritBonusAdd + bucket.AttackCritBonusAdd + (mainLocal?.CritBonusAdd ?? 0)) * critBonusMore;
                split = AttackSplit(mainBase, mainLocal, bucket, scopedGeneral, scopedType, gem, notes);
                split = ApplyDamageMore(split, damageMore, damageMoreGeneral);
            }
            else
            {
                decimal castTime = Math.Max(1, skill.CastTime ?? 1000);
                rate = 1000m / castTime * (1 + speedInc / 100) * rateMore;
                decimal skillCrit = (skill.Crit ?? 0) / 100m;
                if (skillCrit > 0)
                {
                    critChance = Math.Min(100, skillCrit * (1 + (bucket.CritChanceInc + bucket.SpellCritInc) / 100) * critChanceMore);
                    critBonus = (BaseCritDamageBonus + bucket.CritBonusAdd + bucket.SpellCritBonusAdd) * critBonusMore;
                }
                var values = skill.LevelValues(effectiveLevel);
                if (values is not null)
                {
                    split = SpellSplit(values, bucket, scopedGeneral, scopedType, notes, gem);
                    split = ApplyDamageMore(split, damageMore, damageMoreGeneral);
                }
                else notes.Add("NoGemData");
            }
        }
        avgHit = split.Total;
        dps = avgHit * rate * (1 + critChance / 100 * critBonus / 100);
        return Record(dps, avgHit, split, rate, critChance, critBonus, manaCost, isAttack, notes, group, gem, setMatches, levelFromItems);
    }

    private static decimal Round(decimal value, int digits) => decimal.Round(value, digits, MidpointRounding.AwayFromZero);

    /// <summary>Folds support-gem final multipliers into the split after increased damage.</summary>
    private static DamageSplit ApplyDamageMore(DamageSplit split, decimal[] more, decimal general)
    {
        if (general == 1m && more.All(m => m == 1m)) return split;
        return new DamageSplit(
            split.Physical * more[0] * general, split.Fire * more[1] * general, split.Cold * more[2] * general,
            split.Lightning * more[3] * general, split.Chaos * more[4] * general);
    }

    /// <summary>Gem-native conversion/gain from the pinned static stats, e.g. Lightning Arrow's
    /// "active_skill_base_physical_damage_%_to_convert_to_lightning: 80". Single pass, physical first.</summary>
    private static DamageSplit ConvertDamage(DamageSplit split, Gem gem, List<string> notes)
    {
        var statics = gem.Skill?.Statics;
        if (statics is null || statics.Count == 0 || split.Total == 0) return split;
        bool applied = false;
        foreach (var (id, value) in statics)
        {
            const string marker = "_damage_%_to_convert_to_";
            int at = id.IndexOf(marker, StringComparison.Ordinal);
            if (at <= 0 || value == 0) continue;
            var left = id[..at];
            string src = left[(left.LastIndexOf('_') + 1)..];
            string dstRaw = id[(at + marker.Length)..];
            string dst = dstRaw.Split('_')[0];
            int si = Array.IndexOf(TypeWords, src), di = Array.IndexOf(TypeWords, dst);
            if (si < 0 || di < 0 || si == di) continue;
            decimal amount = SplitAt(split, si) * Math.Clamp(value, 0, 100) / 100m;
            split = AddType(SetType(split, si, SplitAt(split, si) - amount), TypeWords[di], amount);
            applied = true;
        }
        foreach (var (id, value) in statics)
        {
            const string marker = "_damage_%_to_add_as_";
            int at = id.IndexOf(marker, StringComparison.Ordinal);
            if (at <= 0 || value == 0) continue;
            var left = id[..at];
            string src = left[(left.LastIndexOf('_') + 1)..];
            string dstRaw = id[(at + marker.Length)..];
            string dst = dstRaw.Split('_')[0];
            int si = Array.IndexOf(TypeWords, src), di = Array.IndexOf(TypeWords, dst);
            if (si < 0 || di < 0 || si == di) continue;
            split = AddType(split, dst, SplitAt(split, si) * value / 100m);
            applied = true;
        }
        if (applied) notes.RemoveAll(n => n == "NoDamageStats");
        return split;
    }

    private static decimal SplitAt(DamageSplit s, int i) => i switch
    {
        0 => s.Physical, 1 => s.Fire, 2 => s.Cold, 3 => s.Lightning, _ => s.Chaos
    };
    private static DamageSplit SetType(DamageSplit s, int i, decimal v) => i switch
    {
        0 => s with { Physical = v }, 1 => s with { Fire = v }, 2 => s with { Cold = v },
        3 => s with { Lightning = v }, _ => s with { Chaos = v }
    };

    /// <summary>Scope words carried by a support final id, e.g. "support_pinpoint_critical_critical_strike_chance_+%_final".</summary>
    private static string[] SupportScopeWords(string id)
    {
        var words = new List<string>();
        foreach (var w in new[] { "physical", "fire", "cold", "lightning", "chaos", "elemental", "melee", "projectile", "area", "spell", "attack", "minion", "maximum", "minimum", "base", "skill", "support", "gem" })
            if (id.Contains(w, StringComparison.Ordinal)) words.Add(w);
        return words.ToArray();
    }

    private static readonly string[] TypeWords = ["physical", "fire", "cold", "lightning", "chaos"];
    private static int TypeIndex(string[] words)
    {
        foreach (var w in words)
            for (int i = 0; i < TypeWords.Length; i++) if (w == TypeWords[i]) return i;
        return -1;
    }
    /// <summary>Lowercase family words of the equipped weapon ("bow", "sword", ...) so tree lines like
    /// "increased Damage with Bows" apply to skills used with that weapon, not to gem tags.</summary>
    private static readonly HashSet<string> WeaponScopeWords = new() { "bow", "crossbow", "sword", "mace", "axe", "dagger", "flail", "spear", "staff", "claw", "wand" };
    private static HashSet<string> WeaponWords(ItemBase? weapon)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (weapon is null) return words;
        foreach (var w in weapon.ItemClass.ToLowerInvariant().Split(' '))
            if (WeaponScopeWords.Contains(w)) words.Add(w);
        if (weapon.Tags.Contains("crossbow")) words.Add("crossbow");
        if (weapon.ItemClass == "Bow") words.Add("bow");
        return words;
    }
    private static bool GemScopeMatches(string scope, string[] tags)
    {
        if (scope == "all") return true;
        if (scope == "elemental") return tags.Contains("fire") || tags.Contains("cold") || tags.Contains("lightning");
        foreach (var part in scope.Split('+'))
            if (!tags.Contains(part)) return false;
        return true;
    }

    private static SkillDpsInfo Record(decimal dps, decimal avgHit, DamageSplit split, decimal rate, decimal critChance, decimal critBonus,
        decimal? manaCost, bool isAttack, List<string> notes, SkillGroup group, Gem gem, bool setMatches, int levelFromItems) =>
        new(group.Id, group.Name, gem.Id, gem.Name, isAttack, setMatches, notes.All(n => n is not ("NoGemData" or "NoWeapon")),
            Round(dps, 1), Round(avgHit, 1), new(Round(split.Physical, 1), Round(split.Fire, 1), Round(split.Cold, 1), Round(split.Lightning, 1), Round(split.Chaos, 1)),
            Round(rate, 2), Round(critChance, 2), Round(critBonus, 0), manaCost, notes.ToArray(), levelFromItems);

    private static DamageSplit AttackSplit(ItemBase weapon, ItemContext? local, StatBucket bucket, decimal scopedGeneral, decimal[] scopedType, Gem gem, List<string> notes)
    {
        var wp = weapon.Props;
        decimal phys = wp.PhysMin is decimal pmin && wp.PhysMax is decimal pmax ? (pmin + pmax) / 2 * (1 + (local?.PhysInc ?? 0) / 100) : 0;
        var split = new DamageSplit(phys, 0, 0, 0, 0);
        split = AddLocalAdded(split, local);
        foreach (var type in Types)
            split = AddType(split, type, (bucket.AddedAttackMin.GetValueOrDefault(type) + bucket.AddedAttackMax.GetValueOrDefault(type)) / 2);
        // Game order: conversion first (on the base hit), then increased by final type, then "gain as" extras.
        split = ConvertDamage(split, gem, notes);
        split = new DamageSplit(
            split.Physical * (1 + (IncFor("physical", true, bucket) + scopedGeneral + scopedType[0]) / 100),
            split.Fire * (1 + (IncFor("fire", true, bucket) + scopedGeneral + scopedType[1]) / 100),
            split.Cold * (1 + (IncFor("cold", true, bucket) + scopedGeneral + scopedType[2]) / 100),
            split.Lightning * (1 + (IncFor("lightning", true, bucket) + scopedGeneral + scopedType[3]) / 100),
            split.Chaos * (1 + (IncFor("chaos", true, bucket) + scopedGeneral + scopedType[4]) / 100));
        if (bucket.GainAs.Count > 0)
        {
            decimal baseTotal = split.Total;
            foreach (var (type, pct) in bucket.GainAs)
                split = AddType(split, type, baseTotal * pct / 100);
        }
        return split;
    }

    private static DamageSplit SpellSplit(Dictionary<string, decimal> values, StatBucket bucket, decimal scopedGeneral, decimal[] scopedType, List<string> notes, Gem gem)
    {
        var split = new DamageSplit(0, 0, 0, 0, 0);
        bool hasDamage = false;
        foreach (var t in Types)
        {
            decimal min = values.TryGetValue("spell_minimum_base_" + t + "_damage", out var v1) ? v1 : 0;
            decimal max = values.TryGetValue("spell_maximum_base_" + t + "_damage", out var v2) ? v2 : 0;
            if (min != 0 || max != 0) { split = AddType(split, t, (min + max) / 2); hasDamage = true; }
        }
        if (!hasDamage) notes.Add("NoDamageStats");
        foreach (var type in Types)
            split = AddType(split, type, (bucket.AddedSpellMin.GetValueOrDefault(type) + bucket.AddedSpellMax.GetValueOrDefault(type)) / 2);
        split = ConvertDamage(split, gem, notes);
        return new DamageSplit(
            split.Physical * (1 + (IncFor("physical", false, bucket) + scopedGeneral + scopedType[0]) / 100),
            split.Fire * (1 + (IncFor("fire", false, bucket) + scopedGeneral + scopedType[1]) / 100),
            split.Cold * (1 + (IncFor("cold", false, bucket) + scopedGeneral + scopedType[2]) / 100),
            split.Lightning * (1 + (IncFor("lightning", false, bucket) + scopedGeneral + scopedType[3]) / 100),
            split.Chaos * (1 + (IncFor("chaos", false, bucket) + scopedGeneral + scopedType[4]) / 100));
    }

    private static readonly string[] Types = ["physical", "fire", "cold", "lightning", "chaos"];

    private static decimal IncFor(string type, bool isAttack, StatBucket b)
    {
        decimal inc = b.DamageInc;
        inc += type switch
        {
            "physical" => b.PhysInc,
            "fire" => b.FireInc + b.ElemInc,
            "cold" => b.ColdInc + b.ElemInc,
            "lightning" => b.LightInc + b.ElemInc,
            "chaos" => b.ChaosInc,
            _ => 0
        };
        inc += isAttack ? b.AttackDamageInc : b.SpellDamageInc;
        if (isAttack && type is "fire" or "cold" or "lightning") inc += b.ElemAttackInc;
        return inc;
    }

    private static DamageSplit AddLocalAdded(DamageSplit split, ItemContext? local)
    {
        if (local is null) return split;
        foreach (var type in Types)
        {
            decimal min = local.AddedMin.GetValueOrDefault(type), max = local.AddedMax.GetValueOrDefault(type);
            if (min != 0 || max != 0) split = AddType(split, type, (min + max) / 2);
        }
        return split;
    }

    private static DamageSplit AddType(DamageSplit s, string type, decimal value) => type switch
    {
        "physical" => s with { Physical = s.Physical + value },
        "fire" => s with { Fire = s.Fire + value },
        "cold" => s with { Cold = s.Cold + value },
        "lightning" => s with { Lightning = s.Lightning + value },
        "chaos" => s with { Chaos = s.Chaos + value },
        _ => s
    };
}
