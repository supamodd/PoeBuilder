using System.Globalization;
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
    bool HasData, decimal Dps, decimal EffectiveDps, decimal AvgHit, DamageSplit Split, decimal HitsPerSecond, decimal CritChancePercent,
    decimal CritBonusPercent, decimal? EffectiveCritChancePercent, decimal? ManaCost, string[] NoteCodes, IReadOnlyList<string> Breakdown,
    IReadOnlyList<AilmentDotResult> Ailments, decimal? TotalDotDps, int LevelFromItems, int EffectiveLevel, int EffectiveQuality);

public sealed record CharacterSummary(
    int Level, string ClassName, bool HasTreeData, bool HasGameData, bool HasStatMap,
    decimal Life, decimal Mana, decimal EnergyShield, decimal Ward, decimal Spirit,
    ResourceReservation? LifeReservation, ResourceReservation? ManaReservation, ResourceReservation? SpiritReservation,
    decimal Strength, decimal Dexterity, decimal Intelligence,
    decimal Armour, decimal Evasion, decimal Accuracy, decimal? HitChancePercent, decimal? MonsterHitChancePercent,
    decimal? BlockChance, decimal BlockChanceMax, decimal? SpellBlockChance, decimal SpellBlockChanceMax,
    decimal? AttackDodgeChancePercent, decimal? SpellDodgeChancePercent,
    decimal? SpellSuppressionChancePercent, decimal? SpellSuppressionEffectPercent,
    decimal DeflectionRating, decimal? DeflectionChancePercent,
    decimal DeflectionDamagePreventedPercent,
    decimal FireRes, decimal ColdRes, decimal LightRes, decimal ChaosRes,
    decimal FireResSources, decimal ColdResSources, decimal LightResSources, decimal ChaosResSources,
    /// <summary>Upper cap of each resistance ("75%" + the maximum-resistance modifiers). PoB2's panel prints
    /// the overcapped part next to the capped value, so it is published per row.</summary>
    decimal FireResMax, decimal ColdResMax, decimal LightResMax, decimal ChaosResMax,
    decimal MoveSpeedPercent, decimal LifeRegenPerSecond, decimal ManaRegenPerSecond, decimal EsRechargePerSecond, decimal? EsRechargeDelaySeconds,
    decimal? PhysicalReductionEstimate, IReadOnlyList<DefenceEhpEstimate> EhpEstimates,
    ExpectedAttackEhpEstimate? ExpectedAttackEhp, ExpectedSpellEhpEstimate? ExpectedSpellEhp, int EstimateMonsterLevel,
    IReadOnlyList<SkillDpsInfo> Skills,
    IReadOnlyDictionary<string, decimal> Extras, IReadOnlyDictionary<string, int> Unaccounted, int UnaccountedTotal,
    /// <summary>Lines this model recognises but deliberately does not turn into a statistic, each with its
    /// reason and — where checked — the evidence from PoB2's own sources (see StatBucket.KnownNonModelled).
    /// They are deliberately kept apart from <paramref name="Unaccounted"/>, which stays the real to-do list.</summary>
    IReadOnlyDictionary<string, int> Known);

/// <summary>
/// Independent v1 calculator. Sources: pinned RePoE 4.5.5.2 values (item bases, implicits, rolls, gem
/// per-level stats, tree lines via the pinned statmap) aligned to the PoB2 (PoE2) reference constants
/// from Data/Misc.lua + Modules/Data.lua: per-level growth +12 life / +4 mana / +6 accuracy with flat
/// per-level base offsets (base life 16, base mana 30), base evasion 7, attributes +2 life (Str) /
/// +6 accuracy (Dex) / +2 mana (Int), armour DR = A/(A+10·hit) capped at 90%, inherent mana regen 4%
/// of maximum Mana per second, ES recharge base 12.5%/s with a 4s start delay, player resistance =
/// stage baseline (-60 at endgame) + raw sources with a 75% upper cap (raisable by maximum-resistance
/// modifiers). Attack block maximum/cap, dodge caps and deflection chance use the PoB2 reference
/// constants. Base Critical Damage Bonus 100% (crits deal 2x by default). Explicitly NOT included
/// (reported, never hidden): buffs/charges/ailments, enemy defences, in-skill damage conversion and
/// conditional stats. Ordinary stat lines from allocated ascendancy nodes are included; special
/// ascendancy mechanics remain unsupported.
/// </summary>
public static class CharacterCalculator
{
    // PoB2 (Data/Misc.lua) constants: life_per_level=12 line 155, mana_per_level=4 line 156,
    // accuracy_rating_per_level=6 line 157, base_evasion_rating=7 line 154, AccuracyPerDexBase=6,
    // ArmourRatio=10 (Data.lua misc, line 255). The per-level mods use a fixed base offset
    // (Multiplier Level base=16 for Life, base=30 for Mana) which the v1 pools reproduce as
    // BaseLife+LifePerLevel*level / BaseMana+ManaPerLevel*level.
    public const decimal LifePerLevel = 12, ManaPerLevel = 4, AccuracyPerLevel = 6;
    public const decimal BaseEvasionRating = 7, EvasionPerLevel = 0;
    public const decimal LifePerStrength = 2, AccuracyPerDexterity = 6, ManaPerIntelligence = 2;
    public const decimal BaseCritDamageBonus = 100;
    public const decimal ArmourConstant = 10, ArmourCapPercent = 90;
    public const decimal EsRechargePercentPerSecond = 12.5m;
    public const decimal ResistanceCap = 75;
    public const decimal EndgameElementalPenalty = 60;
    /// <summary>PoB2 inherent mana regen: character_inherent_mana_regeneration_rate_per_minute_%=240
    /// (Data/Misc.lua line 147) → 240/60/100 = 4% of maximum Mana per second.</summary>
    public const decimal InherentManaRegenPercentPerSecond = 4m;

    public static CharacterSummary Calculate(BuildDocument build, TreeCatalog? tree, GameStatMap? statMap, GameCatalog? catalog,
        ResourceReservationContext? reservationContext = null)
    {
        int level = Math.Clamp(build.Level, 1, 100);
        ResourceReservationContext? effectiveReservationContext = reservationContext ??
            (build.Reservation is { } plan
                ? new ResourceReservationContext(plan.LifeReservedFlat, plan.LifeReservedPercent,
                    plan.ManaReservedFlat, plan.ManaReservedPercent,
                    plan.SpiritReservedFlat, plan.SpiritReservedPercent)
                : null);
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
        // PoB2 gives every allocated node an allocation mode (0 = always, 1/2 = only while that weapon
        // set is active) and attaches a WeaponSetN condition to that node's mods
        // (Classes/PassiveSpec.lua:42-43, Modules/CalcSetup.lua:264-277). The character is computed with
        // the active item set's weapon set, so nodes of the other set must not contribute at all — real
        // builds allocate a whole second cluster there (the Twister fixture: 24 nodes per set).
        int weaponSet = build.Equipment?.WeaponSet == 2 ? 2 : 1;
        if (tree is not null && statMap is not null && build.Tree is not null)
        {
            int start = tree.Classes.FirstOrDefault(c => c.Index == build.Tree.ClassIndex)?.StartNodeId ?? -1;
            var activeNodes = ActiveTreeNodes(build.Tree, weaponSet);
            if (activeNodes.Count != build.Tree.AllocatedNodes.Length)
                bucket.Extras["WeaponSet" + weaponSet + "OnlyNodes"] = activeNodes.Count;
            ApplyTreeStats(bucket, statMap, tree, build.Tree, activeNodes.Append(start));

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

        // --- Item text conditions ---
        // PoB2 tags every rune/idol "Bonded: …" line with the CanUseBondedModifiers condition, so the
        // flag has to be known before the first item's text is read.
        bucket.CanUseBondedModifiers = UsesBondedModifiers(build, tree);
        if (bucket.CanUseBondedModifiers) bucket.Extras["BondedModifiersEnabled"] = 1;
        // PoB2's effective mode reads the enemy from the build's config; absent keys keep PoB2's defaults.
        bucket.EnemyFireResist = build.Conditions.EnemyFireResist;
        bucket.EnemyColdResist = build.Conditions.EnemyColdResist;
        bucket.EnemyLightningResist = build.Conditions.EnemyLightningResist;
        bucket.EnemyChaosResist = build.Conditions.EnemyChaosResist;
        bucket.EnemyArmour = build.Conditions.EnemyArmour;
        bucket.EnemyLevel = build.Conditions.EnemyLevel;
        bucket.EnemyPhysicalDamageReduction = build.Conditions.EnemyPhysicalDamageReduction;

        // --- Equipment (active weapon set + always-on slots) ---
        string[] alwaysSlots = ["Helmet", "Body", "Gloves", "Boots", "Belt", "Amulet", "Ring1", "Ring2", "LifeFlask", "ManaFlask", "Charm1", "Charm2", "Charm3"];
        decimal shieldBlock = 0;
        GearItem? mainHand = null;
        // Item-granted skills ("Grants Skill: Level 19 Purity of Fire"): PoB2 turns them into skill groups
        // of the item's slot (Modules/CalcSetup.lua:1501-1543), so an aura granted by a weapon of the ACTIVE
        // set applies to the character, while the same line on the other set's weapon does not.
        var itemSkillGrants = new List<ItemSkillGrant>();
        if (catalog is not null && build.Equipment is not null)
        {
            var equipmentPlan = build.Equipment;
            int set = equipmentPlan.WeaponSet == 2 ? 2 : 1;
            foreach (var slot in alwaysSlots.Append("Main" + set).Append("Off" + set))
            {
                var gear = Equipped(equipmentPlan, slot);
                if (gear is null) continue;
                if (EffectiveItemText(catalog, gear) is { } grantedText)
                    itemSkillGrants.AddRange(AuraSkillCalculator.ParseGrants(grantedText));
                ApplyGearItem(bucket, catalog, gear, slot == "Main" + set, ref mainHand, ref shieldBlock);
            }
            // Kalandra's Touch ("Reflects opposite Ring") contributes exactly what the opposite ring
            // contributes, exactly like the game. PoB2 treats the line as display-only in ModParser
            // because the effect lives in the item handling, so the duplication happens here.
            foreach (var (ringSlot, otherSlot) in new[] { ("Ring1", "Ring2"), ("Ring2", "Ring1") })
            {
                var ring = Equipped(equipmentPlan, ringSlot);
                if (ring is null || !ReflectsOppositeRing(ring)) continue;
                var other = Equipped(equipmentPlan, otherSlot);
                if (other is null || ReflectsOppositeRing(other)) continue;
                ApplyGearItem(bucket, catalog, other, false, ref mainHand, ref shieldBlock);
                bucket.Extras["RingReflected"] = bucket.Extras.TryGetValue("RingReflected", out var mirrored) ? mirrored + 1 : 1;
            }
            // PoB2's Condition:UsingOneHandedWeapon + Condition:OffHandIsEmpty pair (ModParser.lua:2333), which
            // gates "N% more Skill Speed while Off Hand is empty and you have a One-Handed Martial Weapon
            // equipped in your Main Hand". The active weapon set decides both halves.
            bucket.OffHandEmptyUsingOneHandedWeapon =
                Equipped(equipmentPlan, "Off" + set) is null && mainHand is not null &&
                (catalog.Bases.TryGetValue(mainHand.BaseId, out var mainBase) && mainBase.Tags.Contains("one_hand_weapon"));
        }

        // --- Socketed tree jewels: their jewel-pool affixes act globally. Radius-limited affixes
        // (per-node-in-radius) need a counted radius model and are honestly skipped for now. ---
        if (catalog is not null && build.Equipment is not null && build.Tree is not null && build.Tree.Jewels.Count > 0)
        {
            foreach (var jewelId in build.Tree.Jewels.Values.Distinct())
            {
                var jewel = build.Equipment.Items.FirstOrDefault(i => i.Id == jewelId);
                if (jewel is null) continue;
                // Unique jewels carry no jewel-pool affixes; their own text is interpreted instead.
                ApplyUniqueModText(bucket, catalog, jewel);
                foreach (var roll in jewel.Mods)
                {
                    if (roll.Id.StartsWith("JewelRadius", StringComparison.Ordinal)) { bucket.Extras["JewelRadiusSkipped"] = bucket.Extras.TryGetValue("JewelRadiusSkipped", out var n) ? n + 1 : 1; continue; }
                    // AllMods: a socketed jewel's affix can come from the item pool as well as the jewel
                    // pool (both share the same English wording), and looking in only one of them
                    // silently dropped the other — e.g. a jewel's "increased Critical Spell Damage Bonus".
                    if (!catalog.AllMods.TryGetValue(roll.Id, out var mod)) continue;
                    for (int i = 0; i < mod.Stats.Length && i < roll.Values.Length; i++)
                        StatInterpreter.Apply(bucket, mod.Stats[i].Id, roll.Values[i], new ItemContext());
                }
                // Rare jewels also carry text lines the pinned catalog cannot place.
                ApplyUnmatchedAffixText(bucket, catalog, jewel);
            }
        }

        // --- Radius jewels: "<Type> Passive Skills in Radius also grant …" ---
        // PoB2 attaches such a line to every ALLOCATED node of the named type inside the jewel's radius
        // band, so the effect counts once per qualifying node (Classes/PassiveSpec.lua:1452-1490) — the
        // panel's source list shows exactly one row per notable. The band comes from the jewel's own text
        // ("Radius: …", or "Upgrades Radius to …" for a Time-Lost jewel). A node allocated for the other
        // weapon set is not in radius for this calculation, exactly as PoB2 adds the weapon-set condition
        // to jewel-sourced mods as well (Modules/CalcSetup.lua:279-283).
        if (catalog is not null && build.Equipment is not null && build.Tree is not null && tree is not null)
        {
            var radiusActiveNodes = ActiveTreeNodes(build.Tree, weaponSet);
            foreach (var (socketId, jewelId) in build.Tree.Jewels)
            {
                var jewel = build.Equipment.Items.FirstOrDefault(i => i.Id == jewelId);
                if (jewel is null) continue;
                string? text = EffectiveItemText(catalog, jewel);
                var grants = JewelRadius.ParseGrants(text);
                if (grants.Count == 0) continue;
                int band = JewelRadius.IndexForItemText(text);
                if (band == 0)
                {
                    bucket.Note("radius: " + jewel.Name + " has no fixed Radius line, so its radius grants are not counted");
                    continue;
                }
                int applied = 0;
                foreach (int nodeId in JewelRadius.NodesInRadius(tree, socketId, band, radiusActiveNodes))
                {
                    if (JewelRadius.TypeOf(tree.Nodes[nodeId]) is not { } type) continue;
                    foreach (var grant in grants)
                    {
                        if (grant.Type != type) continue;
                        var stats = RadiusEffects.Resolve(grant.Effect, statMap);
                        if (stats.Count == 0) { bucket.Note("radius: " + grant.Effect); continue; }
                        foreach (var (id, value) in stats)
                        {
                            StatInterpreter.Apply(bucket, id, value, null);
                            // Per-line radius accounting for the crit-bonus family: the fastest way to see how
                            // many notables a radius line matched (PoB2 lands exactly +12% per notable in range).
                            if (id.Contains("critical_strike_multiplier", StringComparison.Ordinal))
                                bucket.Extras["RadiusCrit:" + id] = bucket.Extras.GetValueOrDefault("RadiusCrit:" + id) + value;
                        }
                        applied++;
                    }
                }
                if (applied > 0)
                    bucket.Extras["RadiusGrantsApplied"] = bucket.Extras.TryGetValue("RadiusGrantsApplied", out var total)
                        ? total + applied : applied;
            }
        }

        // --- Quest rewards resolved from an imported build's config ---
        ApplyQuestRewards(bucket, build);

        // --- Auras and persistent skills ("Purity of Fire", "Herald of Thunder", item-granted auras) ---
        // PoB2 applies a persistent skill's GlobalEffect stats to the whole character while the skill is
        // enabled (Modules/CalcSetup.lua:1855, Modules/CalcActiveSkill.lua:1029-1103); the value is the
        // effect's per-level value plus the integral part of qualityStat x quality, and every instance of
        // the same buff merges by name keeping the HIGHEST value (Modules/CalcPerform.lua:40-57).
        if (catalog is not null)
        {
            var (auras, liveGrants) = AuraInstances(catalog, build, bucket, weaponSet, itemSkillGrants);
            AuraSkillCalculator.Apply(bucket, catalog, auras, build.Conditions);
        ResolveRage(bucket, build.Conditions);
            // A "Grants Skill" line that just became a live aura is no longer an unaccounted item line: the
            // modifiers it produced are part of the numbers now, so the report must not claim they are not.
            foreach (var grant in liveGrants) ForgetNote(bucket, "unique: " + grant.Line);
        }

        // --- Attributes and pools ---
        decimal str = baseStr + bucket.Str, dex = baseDex + bucket.Dex, inte = baseInt + bucket.Int;
        decimal baseLife = (catalog?.Vitals.BaseLife ?? 16) + LifePerLevel * level + LifePerStrength * str;
        if (bucket.LifePerDexRate > 0) baseLife += Math.Floor(dex / 4m) * bucket.LifePerDexRate;
        decimal life = (baseLife + bucket.Life) * (1 + bucket.LifeInc / 100);
        if (bucket.ChaosInoculation) life = 1;
        decimal baseMana = (catalog?.Vitals.BaseMana ?? 30) + ManaPerLevel * level + ManaPerIntelligence * inte;
        // PoB2 (CalcDefence.buildDefenceEstimations → doActorLifeManaSpirit): a resource converted INTO
        // Mana becomes an "ExtraMana" BASE mod, so the converted amount is taken from the raw Energy
        // Shield pool and then scaled by Mana's own increased modifiers. Scaling ES first and adding it
        // afterwards would apply the wrong increase to the converted part.
        decimal convertedEs = bucket.EsFlat * Math.Clamp(bucket.EnergyShieldToManaPercent, 0, 100) / 100m;
        decimal mana = (baseMana + bucket.Mana + convertedEs) * (1 + bucket.ManaInc / 100);
        // The final pool is published so per-100-Mana scalers (Rathpith Globe, Archmage) can read it.
        bucket.ManaFinal = mana;
        decimal accuracy = (AccuracyPerLevel * (level - 1) + AccuracyPerDexterity * dex + bucket.AccFlat) * (1 + bucket.AccInc / 100);
        decimal evasion = (BaseEvasionRating + EvasionPerLevel * (level - 1) + bucket.EvFlat) * (1 + bucket.EvInc / 100);
        decimal armour = bucket.ArmourFlat * (1 + bucket.ArmourInc / 100);
        decimal es = bucket.EsFlat * (1 + bucket.EsInc / 100);
        // "More" defence multipliers a persistent buff can carry (Charge Infusion's "15% more Armour, Evasion
        // and Energy Shield while you have an Endurance Charge") apply after the increases, like PoB2's MORE.
        if (bucket.ArmourMorePct != 0) armour *= 1 + bucket.ArmourMorePct / 100;
        if (bucket.EvMorePct != 0) evasion *= 1 + bucket.EvMorePct / 100;
        if (bucket.EsMorePct != 0) es *= 1 + bucket.EsMorePct / 100;
        // The converted share of Energy Shield never reaches the Energy Shield pool (PoB2 keeps the
        // conversion in the source's own base, so the pool shrinks before its increases apply).
        es -= convertedEs * (1 + bucket.EsInc / 100);
        if (es < 0) es = 0;
        // "spell damage per 100 maximum Mana" must read the FINAL maximum Mana, including conversion.
        // PoB2's PerStat tag floors the quotient (Classes/ModStore.lua: m_floor(base / div + 0.0001)),
        // so a pool of 4576 Mana yields 45 stacks, not 45.76 — the difference is visible in Rathpith
        // Globe's crit chance and in Archmage's gain-as-extra.
        decimal per100Mana = Math.Floor(mana / 100m + 0.0001m);
        if (bucket.SpellDamagePer100Mana != 0) bucket.SpellDamageInc += bucket.SpellDamagePer100Mana * per100Mana;
        // Rathpith Globe-style "3% increased Spell Critical Hit Chance per 100 maximum Mana" reads the
        // same final maximum Mana (PoB2 PerStat tag: stat = Mana, div = 100). PoB2 folds PerStat tags
        // into the ordinary INC bucket, so the increase is summed with every other crit chance source.
        if (bucket.SpellCritChancePer100Mana != 0) bucket.SpellCritInc += bucket.SpellCritChancePer100Mana * per100Mana;
        if (bucket.LifeCostPercentOfMaxLife != 0)
            bucket.Extras["LifeCostPerCast"] = Math.Floor(life * bucket.LifeCostPercentOfMaxLife / 100m);
        decimal ward = bucket.WardFlat * (1 + bucket.WardInc / 100);
        decimal spirit = bucket.Spirit * (1 + bucket.SpiritInc / 100);
        ResourceReservation? lifeReservation = effectiveReservationContext is { } context
            ? ResourceReservation.Calculate(life, context.LifeReservedFlat, context.LifeReservedPercent) : null;
        ResourceReservation? manaReservation = effectiveReservationContext is { } contextForMana
            ? ResourceReservation.Calculate(mana, contextForMana.ManaReservedFlat, contextForMana.ManaReservedPercent) : null;
        ResourceReservation? spiritReservation = null;
        if (effectiveReservationContext is not null || bucket.SpiritReservedFlat != 0)
            spiritReservation = ResourceReservation.Calculate(spirit,
                (effectiveReservationContext?.SpiritReservedFlat ?? 0) + bucket.SpiritReservedFlat,
                effectiveReservationContext?.SpiritReservedPercent ?? 0);
        decimal availableMana = manaReservation?.Unreserved ?? mana;
        // --- Life-state conditions (PoB2 data.misc.LowPoolThreshold = 35% of maximum Life) ---
        // The importer stores the state when the share code already resolved it; otherwise it is
        // derived from this build's own reservation plan.
        const decimal LowLifeThresholdPercent = 35m;
        bool lowLife = build.LowLife ?? (lifeReservation is { } lifeState && life > 0 &&
            lifeState.Unreserved * 100m / life < LowLifeThresholdPercent);
        bool fullLife = !lowLife && (lifeReservation is null || (life > 0 && lifeReservation.Unreserved >= life));
        // The life state and the imported config flags are what PoB2's conditional mod specs are evaluated
        // against — including the support gems' own statMap entries (Execute III's "+30% more Damage while
        // you are on Low Life" is a real multiplier, and it has to see the state this build actually has).
        bucket.Conditions = new ModConditions
        {
            LowLife = lowLife,
            FullLife = fullLife,
            Moving = build.Conditions.Moving,
            BeenHitRecently = build.Conditions.BeenHitRecently,
            CritRecently = build.Conditions.CritRecently,
            EnemyIgnited = build.Conditions.EnemyIgnited,
            EnemyChilled = build.Conditions.EnemyChilled,
            EnemyShocked = build.Conditions.EnemyShocked
        };
        foreach (var (percent, requiresLowLife, requiresFullLife) in bucket.ConditionalCritBonus)
            if ((requiresLowLife && lowLife) || (requiresFullLife && fullLife)) bucket.CritBonusMorePct += percent;
        // Condition-scoped stat lines (cast speed / mana regeneration). Flags come from the imported
        // PoB2 config; the two life states are resolved above.
        var conditions = build.Conditions;
        int conditionalsApplied = 0;
        foreach (var (id, value, when) in bucket.Conditionals)
        {
            bool active = when switch
            {
                StatCondition.Moving => conditions.Moving,
                StatCondition.Stationary => !conditions.Moving,
                StatCondition.FullLife => fullLife,
                StatCondition.LowLife => lowLife,
                StatCondition.CritRecently => conditions.CritRecently,
                StatCondition.BeenHitRecently => conditions.BeenHitRecently,
                StatCondition.EnemyIgnited => conditions.EnemyIgnited,
                StatCondition.EnemyChilled => conditions.EnemyChilled,
                StatCondition.EnemyShocked => conditions.EnemyShocked,
                StatCondition.Surrounded => conditions.Surrounded,
                StatCondition.StunnedRecently => conditions.StunnedRecently,
                StatCondition.AtCloseRange => conditions.AtCloseRange,
                // PoB2's distance thresholds are in units: "within 2m" is enemyDistance <= 20, "further than 6m"
                // is enemyDistance >= 60 (ModParser.lua:2153-2154). The option's own placeholder is 20.
                StatCondition.EnemyWithin2m => conditions.EnemyDistance <= 20m,
                StatCondition.EnemyFurtherThan6m => conditions.EnemyDistance >= 60m,
                StatCondition.OffHandEmptyUsingOneHandedWeapon => bucket.OffHandEmptyUsingOneHandedWeapon,
                _ => false
            };
            bucket.Extras["Condition:" + id + "=" + (active ? "on" : "off")] = value;
            if (!active) continue;
            conditionalsApplied++;
            if (id.StartsWith("cast_speed", StringComparison.Ordinal))
            {
                // Through the provenance helper, not straight into the pool: the conditional cast-speed
                // lines are 28 of the reference build's points, and they were invisible in the source list.
                bucket.SpeedScope = "condition:" + id + "=" + (active ? "on" : "off");
                bucket.AddCastSpeed(id, value);
                bucket.SpeedScope = "";
            }
            else if (id.StartsWith("mana_regeneration_rate", StringComparison.Ordinal)) bucket.ManaRegenInc += value;
            else if (id.StartsWith("attack_damage", StringComparison.Ordinal)) bucket.AttackDamageInc += value;
            else if (id.StartsWith("conditional_damage", StringComparison.Ordinal)) bucket.DamageInc += value;
            // "Projectiles deal X% increased Damage with Hits against Enemies within 2m" is projectile-scoped
            // damage in PoB2 (mod("ProjectileDamage","INC")); "X% increased Critical Damage Bonus against
            // Enemies within 2m" is a flat crit-bonus increase.
            else if (id.StartsWith("projectile_damage", StringComparison.Ordinal)) bucket.AddScopedDamage(["projectile"], value);
            else if (id.StartsWith("critical_hit_damage_bonus", StringComparison.Ordinal)) bucket.CritBonusAdd += value;
            else if (id.StartsWith("skill_speed_+%_final", StringComparison.Ordinal)) bucket.SkillSpeedMorePct += value;
            else bucket.Extras["Condition:known:" + id] = value;   // recognised, no damage bucket (e.g. stun buildup)
        }
        if (conditionalsApplied > 0) bucket.Extras["Conditions:applied"] = conditionalsApplied;
        bucket.ArcLightningInfused = conditions.ArcLightningInfused;
        // The conditions a skill's statMap entries may be gated on — every one of them comes from the
        // build's own config or the resolved life state, so nothing is assumed.
        bucket.Conditions = new ModConditions
        {
            LowLife = lowLife,
            FullLife = fullLife,
            Moving = conditions.Moving,
            BeenHitRecently = conditions.BeenHitRecently,
            CritRecently = conditions.CritRecently,
            EnemyIgnited = conditions.EnemyIgnited,
            EnemyChilled = conditions.EnemyChilled,
            EnemyShocked = conditions.EnemyShocked
        };
        bucket.Extras["Pool:LowLife"] = lowLife ? 1 : 0;
        bucket.Extras["Pool:FullLife"] = fullLife ? 1 : 0;
        // --- Second stage of the persistent-buff pass (PoB2's CalcSetup order) ---
        // The life states are known now, so the buff modifiers the first pass had to defer are re-evaluated
        // against them: that is where "on Low Life" support/buff mods land, and it is why the Arc build gains
        // Direstrike II's damage instead of only a note.
        if (catalog is not null && bucket.DeferredBuffs.Count > 0)
        {
            AuraSkillCalculator.ApplyDeferred(bucket, catalog, build.Conditions);
            // Rage's stack count is clamped by Maximum Rage, which a deferred buff mod can raise.
            ResolveRage(bucket, build.Conditions);
        }
        // The pool snapshot is taken HERE, after every stat pass (tree, items, buffs, conditionals), so the
        // report prints the numbers the skill loop actually reads. PoB2 computes a rate as
        // 1/(baseTime/round((1+inc/100)*more,2)) over the skill's own Speed pool (CalcOffence.lua:2835-2840),
        // so comparing pools with its panel is only meaningful once nothing can add to them any more.
        bucket.Extras["Pool:LifeBase"] = baseLife;
        bucket.Extras["Pool:LifeFlat"] = bucket.Life;
        bucket.Extras["Pool:LifeInc"] = bucket.LifeInc;
        bucket.Extras["Pool:ManaBase"] = baseMana;
        bucket.Extras["Pool:ManaFlat"] = bucket.Mana;
        bucket.Extras["Pool:ManaInc"] = bucket.ManaInc;
        bucket.Extras["Pool:ManaFromEs"] = convertedEs;
        bucket.Extras["Pool:EsRaw"] = bucket.EsFlat;
        bucket.Extras["Pool:EsInc"] = bucket.EsInc;
        bucket.Extras["Pool:EsToManaPct"] = Math.Clamp(bucket.EnergyShieldToManaPercent, 0, 100);
        // Rate components: a spell's cast time is scaled by the caster's cast speed and, when the skill is
        // deployed by a totem, by the totem's own cast-speed mods. Both are published so a rate mismatch can
        // be traced instead of guessed.
        bucket.Extras["Pool:CastSpeedInc"] = bucket.CastSpeedInc + bucket.SkillSpeedInc;
        bucket.Extras["Pool:TotemCastSpeedInc"] = bucket.TotemCastSpeedInc;
        bucket.Extras["Pool:TotemAttackSpeedInc"] = bucket.TotemAttackSpeedInc;
        bucket.Extras["Pool:TotemsSpellsCastSpeedPerActiveTotem"] = bucket.TotemsSpellsCastSpeedPerActiveTotem;
        bucket.Extras["Pool:TotemsAttackSpeedPerActiveTotem"] = bucket.TotemsAttackSpeedPerActiveTotem;
        bucket.Extras["Pool:ArmourFlat"] = bucket.ArmourFlat;
        bucket.Extras["Pool:ArmourInc"] = bucket.ArmourInc;
        bucket.Extras["Pool:EvasionFlat"] = bucket.EvFlat;
        bucket.Extras["Pool:EvasionInc"] = bucket.EvInc;
        bucket.Extras["Pool:SpiritFlat"] = bucket.Spirit;
        bucket.Extras["Pool:SpiritInc"] = bucket.SpiritInc;
        bucket.Extras["Pool:Str"] = bucket.Str;
        bucket.Extras["Pool:Dex"] = bucket.Dex;
        bucket.Extras["Pool:Int"] = bucket.Int;
        bucket.Extras["Pool:CritChanceInc"] = bucket.CritChanceInc;
        bucket.Extras["Pool:SpellCritInc"] = bucket.SpellCritInc;
        bucket.Extras["Pool:ProjectileSpeedInc"] = bucket.ProjectileSpeedInc + bucket.SpellProjectileSpeedInc;
        bucket.Extras["Pool:Charges"] = build.Conditions.FrenzyCharges * 100 + build.Conditions.PowerCharges * 10 + build.Conditions.EnduranceCharges;
        bucket.Extras["Pool:CritChanceAdd"] = bucket.CritChanceAdd;
        bucket.Extras["Pool:CritBonusAdd"] = bucket.CritBonusAdd;
        bucket.Extras["Pool:SpellCritBonusAdd"] = bucket.SpellCritBonusAdd;
        bucket.Extras["Pool:CritBonusInc"] = bucket.CritBonusInc;
        bucket.Extras["Pool:SpellCritBonusInc"] = bucket.SpellCritBonusInc;
        bucket.Extras["Pool:SpellCritChancePer100Mana"] = bucket.SpellCritChancePer100Mana;
        // Speed provenance: every stat id that fed a speed pool, so a rate that disagrees with PoB2 can be
        // taken apart term by term - the reference's own export carries a single panel number and no
        // breakdown, which is exactly why this list exists (docs/VALIDATION.md, 0.9.19).
        foreach (var (id, value) in bucket.CastSpeedSources) bucket.Extras["Pool:CastSpeedSrc:" + id] = value;
        foreach (var (id, value) in bucket.SkillSpeedSources) bucket.Extras["Pool:SkillSpeedSrc:" + id] = value;
        foreach (var (id, value) in bucket.TotemCastSpeedSources) bucket.Extras["Pool:TotemCastSpeedSrc:" + id] = value;
        foreach (var (id, value) in bucket.TotemAttackSpeedSources) bucket.Extras["Pool:TotemAttackSpeedSrc:" + id] = value;
        foreach (var (id, value) in bucket.AttackSpeedSources) bucket.Extras["Pool:AttackSpeedSrc:" + id] = value;
        // Line-level provenance: WHICH wording of WHICH item/node produced each speed point.
        foreach (var (key, value) in bucket.SpeedScopes) bucket.Extras["Pool:SpeedScope:" + key] = value;
        bucket.Extras["Pool:CritBonusMorePct"] = bucket.CritBonusMorePct;
        decimal moveSpeed = 100 + bucket.MoveInc;
        decimal esRechargePerSecond = DefenceCalculator.EnergyShieldRechargePerSecond(es, bucket.EsRechargeInc,
            EsRechargePercentPerSecond);
        decimal? esRechargeDelay = es > 0
            ? DefenceCalculator.EnergyShieldRechargeDelaySeconds(bucket.EsRechargeFasterInc)
            : null;
        decimal deflection = (evasion * bucket.DeflectPctOfEvasion + armour * bucket.DeflectPctOfArmour) / 100
            * (1 + bucket.DeflectInc / 100);
        decimal blockMaximum = DefenceCalculator.BlockChanceMaximum(bucket.BlockMaxAdd, bucket.BlockMaxOverride);
        decimal? blockChance = shieldBlock > 0 || bucket.BlockAdditional > 0
            ? DefenceCalculator.BlockChance(shieldBlock, bucket.BlockInc, bucket.BlockAdditional,
                bucket.BlockMaxAdd, bucket.BlockMaxOverride)
            : null;
        decimal spellBlockMaximum = DefenceCalculator.BlockChanceMaximum(bucket.SpellBlockMaxAdd, bucket.SpellBlockMaxOverride);
        decimal? spellBlockChance = bucket.SpellBlockBase != 0 || bucket.SpellBlockAdditional != 0
            ? DefenceCalculator.SpellBlockChance(bucket.SpellBlockBase, bucket.BlockInc, bucket.SpellBlockAdditional,
                bucket.SpellBlockMaxAdd, bucket.SpellBlockMaxOverride)
            : null;
        decimal? attackDodgeChance = bucket.AttackDodgeChance != 0
            ? DefenceCalculator.DodgeChance(bucket.AttackDodgeChance) : null;
        decimal? spellDodgeChance = bucket.SpellDodgeChance != 0
            ? DefenceCalculator.DodgeChance(bucket.SpellDodgeChance) : null;
        decimal deflectionDamagePrevented = Math.Max(0,
            DefenceCalculator.DeflectionDamagePreventedPercent + bucket.DeflectEffectAdd);
        decimal? spellSuppressionChance = bucket.SpellSuppressionChance != 0
            ? DefenceCalculator.SpellSuppressionChance(bucket.SpellSuppressionChance) : null;
        decimal? spellSuppressionEffect = spellSuppressionChance is not null
            ? Math.Max(0, DefenceCalculator.BaseSpellSuppressionEffectPercent + bucket.SpellSuppressionEffectAdd)
            : null;

        // --- Same-level default-monster estimates (pinned stats) ---
        MonsterLevel? monster = catalog?.Monsters.GetValueOrDefault(level.ToString());
        decimal? playerHitChance = monster?.Evasion is decimal targetEvasion
            ? DefenceCalculator.PlayerHitChance(targetEvasion, accuracy) : null;

        // --- Skill DPS ---
        var skills = new List<SkillDpsInfo>();
        if (catalog is not null && build.Skills is not null)
        {
            int set = build.Equipment?.WeaponSet == 2 ? 2 : 1;
            ItemBase? mainBase = mainHand is not null ? catalog.Bases.GetValueOrDefault(mainHand.BaseId) : null;
            ItemContext? mainLocal = mainHand is not null ? WeaponContext(catalog, mainHand) : null;
            // Buffs that other groups provide ("Archmage" grants gain-as-extra-Lightning to every
            // non-channelling spell) are collected before the per-skill pass, because they are
            // properties of the character, not of the group that carries the gem.
            CollectGlobalGemStatics(bucket, catalog, build.Skills.Groups);
            foreach (var group in build.Skills.Groups)
                if (SkillInfo(catalog, group, group.WeaponSet == 0 || group.WeaponSet == set, mainBase, mainLocal, bucket, playerHitChance) is { } info)
                    skills.Add(info);
        }
        decimal? monsterHitChance = monster?.Accuracy is decimal monsterAccuracy
            ? DefenceCalculator.MonsterHitChance(evasion, monsterAccuracy) : null;
        decimal? deflectionChance = monster?.Accuracy is decimal deflectionAccuracy
            ? DefenceCalculator.DeflectionChance(deflection, deflectionAccuracy) : null;
        decimal? reduction = null;
        decimal? scenarioHit = monster?.PhysicalDamage is decimal monsterPhysicalDamage && monsterPhysicalDamage > 0
            ? monsterPhysicalDamage : null;
        if (scenarioHit is decimal hit)
        {
            // PoB2 armourReductionF semantics: reduction = A/(A + ArmourRatio*rawHit), upper-capped;
            // negative armour (armour break) amplifies. The summary displays the integer-rounded value.
            decimal dr = EhpCalculator.ArmourReductionPercent(armour, hit, ArmourConstant, ArmourCapPercent);
            reduction = Math.Round(dr, 0, MidpointRounding.AwayFromZero);
        }

        // Player resistance is the stage baseline plus all raw sources, capped only on the upper
        // side. Enemy resistance, penetration and exposure are deliberately not part of this result.
        var fireResistance = ResistanceCalculator.Calculate(ResBaseline(build.ProgressStage), bucket.FireRes, bucket.FireMax, ResistanceCap);
        var coldResistance = ResistanceCalculator.Calculate(ResBaseline(build.ProgressStage), bucket.ColdRes, bucket.ColdMax, ResistanceCap);
        var lightningResistance = ResistanceCalculator.Calculate(ResBaseline(build.ProgressStage), bucket.LightRes, bucket.LightMax, ResistanceCap);
        var chaosResistance = ResistanceCalculator.Calculate(0, bucket.ChaosRes, bucket.ChaosMax, ResistanceCap);

        var ehpEstimates = new List<DefenceEhpEstimate>();
        ExpectedAttackEhpEstimate? expectedAttackEhp = null;
        ExpectedSpellEhpEstimate? expectedSpellEhp = null;
        if (scenarioHit is decimal ehpHit)
        {
            decimal physicalMultiplier = EhpCalculator.ArmourDamageMultiplier(armour, ehpHit, ArmourConstant, ArmourCapPercent);
            AddEhp("Physical", new DamagePacket(ehpHit, 0, 0, 0, 0),
                EhpCalculator.ResourcePoolForDamageType("Physical", life, es,
                    mana: availableMana, damageTakenFromManaPercent: bucket.DamageTakenFromManaPercent), physicalMultiplier);
            AddEhp("Fire", new DamagePacket(0, ehpHit, 0, 0, 0),
                EhpCalculator.ResourcePoolForDamageType("Fire", life, es,
                    mana: availableMana, damageTakenFromManaPercent: bucket.DamageTakenFromManaPercent));
            AddEhp("Cold", new DamagePacket(0, 0, ehpHit, 0, 0),
                EhpCalculator.ResourcePoolForDamageType("Cold", life, es,
                    mana: availableMana, damageTakenFromManaPercent: bucket.DamageTakenFromManaPercent));
            AddEhp("Lightning", new DamagePacket(0, 0, 0, ehpHit, 0),
                EhpCalculator.ResourcePoolForDamageType("Lightning", life, es,
                    mana: availableMana, damageTakenFromManaPercent: bucket.DamageTakenFromManaPercent));
            AddEhp("Chaos", new DamagePacket(0, 0, 0, 0, ehpHit),
                EhpCalculator.ResourcePoolForDamageType("Chaos", life, es,
                    chaosBypassesEnergyShield: !bucket.ChaosInoculation,
                    mana: availableMana, damageTakenFromManaPercent: bucket.DamageTakenFromManaPercent));

            if (monsterHitChance is decimal defaultMonsterHitChance)
            {
                decimal pool = EhpCalculator.ResourcePoolForDamageType("Physical", life, es);
                decimal expectedMultiplier = EhpCalculator.ExpectedAttackDamageMultiplier(
                    physicalMultiplier, defaultMonsterHitChance, blockChance ?? 0, deflectionChance ?? 0,
                    deflectionDamagePrevented, 0, attackDodgeChance ?? 0);
                expectedAttackEhp = new("Physical", R(ehpHit, 2), R(pool, 2),
                    R(defaultMonsterHitChance, 2), R(blockChance ?? 0, 2), R(deflectionChance ?? 0, 2),
                    R(physicalMultiplier, 4), R(expectedMultiplier, 6),
                    EhpCalculator.EffectiveHitPool(pool, expectedMultiplier) is decimal value ? R(value, 2) : null,
                    R(attackDodgeChance ?? 0, 2));
            }

            void AddEhp(string damageType, DamagePacket packet, decimal pool, decimal? knownMultiplier = null)
            {
                var resistances = new Dictionary<string, ResistanceHitResult>
                {
                    ["fire"] = ResistanceCalculator.ForHit(fireResistance),
                    ["cold"] = ResistanceCalculator.ForHit(coldResistance),
                    ["lightning"] = ResistanceCalculator.ForHit(lightningResistance),
                    ["chaos"] = ResistanceCalculator.ForHit(chaosResistance)
                };
                var routed = DamageRoutingCalculator.ApplyTakenAs(packet, bucket.DamageTakenAs);
                var mitigated = MitigationCalculator.Evaluate(routed, armour, resistances, pool,
                    ArmourConstant, ArmourCapPercent, bucket.ArmourAppliesToElemental);
                decimal multiplier = knownMultiplier is decimal fixedMultiplier && bucket.DamageTakenAs.Count == 0
                    ? fixedMultiplier : mitigated.DamageMultiplier;
                ehpEstimates.Add(new(damageType, R(ehpHit, 2), R(pool, 2), R(multiplier, 4),
                    EhpCalculator.EffectiveHitPool(pool, multiplier) is decimal value ? R(value, 2) : null));
            }
        }

        if (build.Defence?.SpellRawHit is decimal explicitSpellHit && explicitSpellHit > 0)
        {
            string spellType = build.Defence.SpellDamageType;
            decimal spellReduction = build.Defence.SpellResistanceReductionPercent;
            decimal spellPenetration = build.Defence.SpellResistancePenetrationPercent;
            var fireResistanceHit = ResistanceCalculator.ForHit(fireResistance, spellReduction, spellPenetration);
            var coldResistanceHit = ResistanceCalculator.ForHit(coldResistance, spellReduction, spellPenetration);
            var lightningResistanceHit = ResistanceCalculator.ForHit(lightningResistance, spellReduction, spellPenetration);
            var chaosResistanceHit = ResistanceCalculator.ForHit(chaosResistance, spellReduction, spellPenetration);
            var hitResistances = new Dictionary<string, ResistanceHitResult>
            {
                ["fire"] = fireResistanceHit,
                ["cold"] = coldResistanceHit,
                ["lightning"] = lightningResistanceHit,
                ["chaos"] = chaosResistanceHit
            };
            DamagePacket? spellPacket = spellType switch
            {
                "Physical" => new DamagePacket(explicitSpellHit, 0, 0, 0, 0),
                "Fire" => new DamagePacket(0, explicitSpellHit, 0, 0, 0),
                "Cold" => new DamagePacket(0, 0, explicitSpellHit, 0, 0),
                "Lightning" => new DamagePacket(0, 0, 0, explicitSpellHit, 0),
                "Chaos" => new DamagePacket(0, 0, 0, 0, explicitSpellHit),
                _ => null
            };
            if (spellPacket is DamagePacket rawPacket)
            {
                var routedPacket = DamageRoutingCalculator.ApplyTakenAs(rawPacket, bucket.DamageTakenAs);
                var mitigated = MitigationCalculator.Evaluate(
                    routedPacket, armour, hitResistances,
                    EhpCalculator.ResourcePoolForDamageType(spellType, life, es,
                        chaosBypassesEnergyShield: !bucket.ChaosInoculation),
                    ArmourConstant, ArmourCapPercent, bucket.ArmourAppliesToElemental);
                decimal spellPool = EhpCalculator.ResourcePoolForDamageType(spellType, life, es,
                    chaosBypassesEnergyShield: !bucket.ChaosInoculation);
                expectedSpellEhp = EhpCalculator.SpellEhpEstimate(new SpellEhpScenario(
                    spellType, R(explicitSpellHit, 2), R(spellPool, 2), R(mitigated.DamageMultiplier, 4),
                    spellSuppressionChance ?? 0, spellSuppressionEffect ?? DefenceCalculator.BaseSpellSuppressionEffectPercent,
                    spellDodgeChance ?? 0, build.Defence.SpellHitChancePercent,
                    spellBlockChance ?? 0, build.Defence.SpellBlockedHitDamagePercent));
            }
        }

        return new CharacterSummary(level, className, tree is not null, catalog is not null, statMap is not null,
            R(life), R(mana), R(es), R(ward), R(spirit),
            lifeReservation, manaReservation, spiritReservation,
            R(str), R(dex), R(inte),
            R(armour), R(evasion), R(accuracy), playerHitChance, monsterHitChance,
            blockChance is decimal finalBlock ? R(finalBlock) : null, R(blockMaximum),
            spellBlockChance is decimal finalSpellBlock ? R(finalSpellBlock) : null, R(spellBlockMaximum),
            attackDodgeChance is decimal finalAttackDodge ? R(finalAttackDodge) : null,
            spellDodgeChance is decimal finalSpellDodge ? R(finalSpellDodge) : null,
            spellSuppressionChance is decimal finalSuppression ? R(finalSuppression) : null,
            spellSuppressionEffect is decimal suppressionEffect ? R(suppressionEffect) : null,
            R(deflection), deflectionChance is decimal finalDeflectChance ? R(finalDeflectChance) : null,
            R(deflectionDamagePrevented),
            R(fireResistance.Effective), R(coldResistance.Effective),
            R(lightningResistance.Effective), R(chaosResistance.Effective),
            R(fireResistance.Sources), R(coldResistance.Sources),
            R(lightningResistance.Sources), R(chaosResistance.Sources),
            R(fireResistance.Maximum), R(coldResistance.Maximum), R(lightningResistance.Maximum), R(chaosResistance.Maximum),
            R(moveSpeed), R(ResourceRecovery.LifeRegenerationPerSecond(bucket.LifeRegenPerMin, bucket.LifeRegenInc)
                + life * Math.Max(0, bucket.LifeRegenPercentPerSecond) / 100m, 2),
            R(Math.Max(0m, mana * InherentManaRegenPercentPerSecond / 100m * (1 + bucket.ManaRegenInc / 100m)), 2),
            R(esRechargePerSecond, 2),
            esRechargeDelay is decimal delay ? R(delay, 2) : null,
            reduction, ehpEstimates, expectedAttackEhp, expectedSpellEhp, level, skills, bucket.Extras, bucket.Unaccounted, bucket.UnaccountedTotal, bucket.Known);

        static decimal R(decimal v, int digits = 0) => Math.Round(v, digits, MidpointRounding.AwayFromZero);
        // PoB2 (ConfigOptions.lua): resistance penalties progress per act; the default endgame value is
        // -60% to Fire/Cold/Lightning after the campaign. Chaos is not penalised by acts. The "starter"
        // stage keeps a zero baseline to mirror the campaign start.
        static decimal ResBaseline(string stage) => stage == "endgame" ? -EndgameElementalPenalty : 0;
    }

    /// <summary>Resident resolutions for pinned tree lines that the generated stat map does not
    /// cover (the pinned RePoE export's stat map is incomplete for a handful of keystones). Each key
    /// is the exact PlainText form stored in the pinned tree export; each value mirrors the stat-map
    /// entry format, and every stat id is consumed by StatInterpreter. No invented lines.</summary>
    private static readonly Dictionary<string, Dictionary<string, decimal>> TreeStatFallbacks = new()
    {
        ["Convert 100% of maximum Energy Shield to maximum Mana\nMana Costs are Doubled"] =
            new() { ["energy_shield_to_mana"] = 100m, ["skill_mana_cost_+100%_final"] = 100m },
        ["All Damage is taken from Mana before Life\n50% less Mana Recovery Rate"] =
            new() { ["damage_removed_from_mana_before_life_%"] = 100m, ["mana_recovery_rate_+%_final"] = -50m },
        ["Your Totem Limit is doubled\nNo Charge requirement for placing Totems\nTotems reserve 75 Spirit each"] =
            new() { ["spirit_reserved_flat"] = 75m },
        ["Gain 6% of Lightning damage as Extra Cold damage"] =
            new() { ["non_skill_base_lightning_damage_%_to_gain_as_cold"] = 6m },
        // PoB2: mod("Speed","MORE",25) under UsingOneHandedWeapon + OffHandIsEmpty (ModParser.lua:2333-2337).
        // The node's own value is fixed, so a literal entry is exact; whether it applies is decided by the
        // active weapon set's equipment. (The key is the PlainText form of Dance with Death's stat line.)
        ["25% more Skill Speed while Off Hand is empty and you have\na One-Handed Martial Weapon equipped in your Main Hand"] =
            new() { ["skill_speed_+%_final_while_off_hand_is_empty_and_using_one_handed_weapon"] = 25m },
    };

    /// <summary>The three bullets of Gemling's "For each colour of Socketed Support Gem that is most numerous,
    /// gain:" notable. PoB2 parses them as three separate stats (ModParser.lua:3365-3372); the colour that is
    /// actually most numerous is decided per skill group (CalcSetup.lua:2155-2162), so the cost reduction is
    /// applied there. Values are read from the text, never assumed.</summary>
    private static Dictionary<string, decimal>? MostNumerousColourStats(string line)
    {
        var stats = new Dictionary<string, decimal>();
        foreach (var bullet in line.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var number = System.Text.RegularExpressions.Regex.Match(bullet, @"(\d+)%");
            if (!number.Success || !decimal.TryParse(number.Groups[1].Value, out decimal value)) continue;
            if (bullet.Contains("Red:", StringComparison.OrdinalIgnoreCase))
                stats["most_numerous_colour_crit_damage_taken_%"] = value;
            else if (bullet.Contains("Blue:", StringComparison.OrdinalIgnoreCase))
                stats["most_numerous_colour_cost_more_%"] = -value;   // "30% less cost" is a MORE/less on the cost
            else if (bullet.Contains("Green:", StringComparison.OrdinalIgnoreCase))
                stats["most_numerous_colour_move_penalty_%"] = -value;
        }
        return stats.Count > 0 ? stats : null;
    }

    /// <summary>Parametric fallback for recurring tree-line shapes that repeat with different
    /// numbers across the tree. Only the exact shapes stored in the pinned export are matched;
    /// anything else stays unreported rather than guessed.</summary>
    private static Dictionary<string, decimal>? PatternFallbackStats(string line)
    {
        // "For each colour of Socketed Support Gem that is most numerous, gain:" (the Gemling notable): PoB2
        // parses the three bullet stats separately (ModParser.lua:3365-3372) — red removes the enemy's extra
        // critical damage against you, blue is "30% less cost", green is the movement-speed penalty.
        // The blue one is applied per skill group, because "most numerous" is decided by that group's colours.
        if (line.StartsWith("For each colour of Socketed Support Gem that is most numerous", StringComparison.Ordinal))
            return MostNumerousColourStats(line);
        // "Regenerate X% of maximum Life per second" is handled further down (the pinned stat map has no
        // entry for this wording, so the shape is read there).
        // "Attacks have +1% to Critical Hit Chance" (a tree/idol line whose wording is not in the pinned
        // stat map): PoB2 reads it as a flat addition to the critical hit chance, i.e. the BASE term of
        // (baseCrit + CritChance BASE) * (1 + inc) * more (CalcOffence.lua:3718).
        const string attackCritPrefix = "Attacks have +";
        const string attackCritSuffix = "% to Critical Hit Chance";
        if (line.StartsWith(attackCritPrefix, StringComparison.Ordinal) && line.EndsWith(attackCritSuffix, StringComparison.Ordinal))
        {
            var number = line[attackCritPrefix.Length .. (line.Length - attackCritSuffix.Length)];
            if (IsDecimalNumber(number)) return new() { ["critical_strike_chance_+"] = decimal.Parse(number, CultureInfo.InvariantCulture) };
        }
        // Attribute grants. The pinned stat map carries most of these, but the "+5 to Intelligence"
        // wording of the PoE2 generic attribute nodes is missing from it, so the shape is read here:
        // a single attribute, a pair, or all three.
        const string toMarker = " to ";
        int toIndex = line.IndexOf(toMarker, StringComparison.Ordinal);
        if (line.StartsWith('+') && toIndex > 1)
        {
            string amountText = line[1..toIndex];
            string target = line[(toIndex + toMarker.Length)..].Trim();
            if (IsDecimalNumber(amountText) && decimal.TryParse(amountText, out decimal amount))
            {
                string? attributeId = target switch
                {
                    "Strength" => "base_strength",
                    "Dexterity" => "base_dexterity",
                    "Intelligence" => "base_intelligence",
                    "all Attributes" => "additional_all_attributes",
                    // PoB2 defaults a generic attribute node to Strength when no choice is recorded.
                    "any Attribute" => "base_strength",
                    "Strength and Intelligence" => "base_strength_and_intelligence",
                    "Strength and Dexterity" => "base_strength_and_dexterity",
                    "Dexterity and Intelligence" => "base_dexterity_and_intelligence",
                    _ => null
                };
                if (attributeId is not null) return new() { [attributeId] = amount };
            }
        }
        // "Regenerate 0.5% of maximum Life per second"
        const string regenSuffix = "% of maximum Life per second";
        if (line.StartsWith("Regenerate ", StringComparison.Ordinal) && line.EndsWith(regenSuffix, StringComparison.Ordinal))
        {
            var number = line["Regenerate ".Length .. (line.Length - regenSuffix.Length)];
            if (IsDecimalNumber(number))
                return new() { ["life_regeneration_percent_per_second"] = decimal.Parse(number, CultureInfo.InvariantCulture) };
            return null;
        }
        // "Gain 6% of Lightning damage as Extra Cold damage"
        const string gainMarker = " damage as Extra ";
        int marker = line.IndexOf(gainMarker, StringComparison.Ordinal);
        if (line.StartsWith("Gain ", StringComparison.Ordinal) && marker > 4 && line.EndsWith(" damage", StringComparison.Ordinal))
        {
            var parts = line["Gain ".Length .. marker].Split('%', 2);
            string destination = line[(marker + gainMarker.Length)..(line.Length - " damage".Length)].ToLowerInvariant();
            if (parts.Length == 2 && IsDecimalNumber(parts[0]) && TypeWords.Contains(parts[1].ToLowerInvariant()) && TypeWords.Contains(destination))
            {
                string source = parts[1].ToLowerInvariant();
                return new() { ["non_skill_base_" + source + "_damage_%_to_gain_as_" + destination] = decimal.Parse(parts[0], CultureInfo.InvariantCulture) };
            }
            return null;
        }
        return null;
    }

    private static bool IsDecimalNumber(string text)
    {
        // InvariantCulture: the strings come from the game data (always a '.' decimal separator), while the
        // process culture may use a comma — plain decimal.Parse would then reject "0.5" and silently drop
        // every fractional stat line (this is how "Regenerate 0.5% of maximum Life per second" was lost).
        try { decimal.Parse(text, CultureInfo.InvariantCulture); return true; }
        catch (FormatException) { return false; }
    }

    /// <summary>Persistent buffs granted by an allocated node ("Grants Skill: X"). The buff's effect
    /// lives in the game's skill data, which the pinned export does not carry, so the modelled ones are
    /// listed here and a granted skill we do not model is reported instead of guessed.</summary>
    private static readonly Dictionary<string, Dictionary<string, decimal>> GrantedSkillBuffs = new(StringComparer.OrdinalIgnoreCase)
    {
        // Gemling Legionnaire, "Essence of Virtue" (ascendancy node 11641): the granted persistent buff
        // gives 10% increased maximum Life, 15% increased Armour/Evasion/Energy Shield and 189%
        // increased Life Regeneration Rate. Every value is cross-checked on the reference build: PoB2's
        // Life pool is exactly (1168 base + 176 from Strength + 139 flat) x 1.15, its Armour and Evasion
        // are exactly the flat pools x 1.45 (30% quest + 15% here), and its own panel reports
        // LifeRegenRecovery 98.5 = 2046/60 x 2.89. The only other Life increase the build carries is a
        // 5% quest reward, so nothing else can supply these numbers.
        ["Virtuous Barrier"] = new()
        {
            ["maximum_life_+%"] = 10m,
            ["defences_+%"] = 15m,
            ["life_regeneration_rate_+%"] = 189m,
        },
    };

    /// <summary>Applies the modelled persistent buff of a "Grants Skill: X" line. Returns false when the
    /// line does not grant a skill at all, so ordinary stat lines keep their normal path.</summary>
    private static bool TryGrantedSkillBuff(StatBucket bucket, string line)
    {
        const string marker = "Grants Skill:";
        int at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;
        string name = line[(at + marker.Length)..];
        foreach (string markup in new[] { "<underline>", "</underline>", "<italic>", "</italic>", "{", "}" })
            name = name.Replace(markup, "");
        name = name.Trim();
        if (name.Length == 0) return true;
        if (!GrantedSkillBuffs.TryGetValue(name, out var stats))
        {
            // Honest: a granted skill we do not model is listed, never silently ignored.
            bucket.Note("skill: " + name);
            return true;
        }
        StatInterpreter.ApplyAll(bucket, stats);
        bucket.Extras["GrantedSkillBuffs"] = bucket.Extras.TryGetValue("GrantedSkillBuffs", out var n) ? n + 1 : 1;
        return true;
    }

    private static void ApplyTreeStats(StatBucket bucket, GameStatMap statMap, TreeCatalog graph,
        PassiveTreePlan plan, IEnumerable<int> nodeIds)
    {
        foreach (int id in nodeIds.Distinct())
        {
            if (!graph.Nodes.ContainsKey(id)) continue;
            foreach (var line in graph.Describe(id, plan).Stats)
            {
                bucket.SpeedScope = "tree#" + id + " " + line;
                // Conditional crit-bonus lines ("30% more Critical Damage Bonus when on Low Life")
                // depend on the Life state, which is only known after the pools are computed, so they
                // are collected here and resolved in Calculate.
                if (line.Contains("Critical Damage Bonus when on", StringComparison.OrdinalIgnoreCase) &&
                    TryConditionalCritBonus(bucket, line)) continue;
                if (TryGrantedSkillBuff(bucket, line)) continue;
                // The pinned stat map is keyed by the PLAIN wording of a line ("+5 to Strength"),
                // while the tree export keeps the game's display markup ("+5 to [Strength]"). Both
                // forms are tried, so markup never hides a line that the map actually knows — this is
                // what makes every allocated node contribute, including attribute nodes.
                if (TryApplyTreeLine(bucket, statMap, line)) continue;
                string plain = TreeCatalog.PlainText(line);
                if (plain != line && TryApplyTreeLine(bucket, statMap, plain)) continue;
                // Recognised-but-not-modelled lines (each with its reason, see StatBucket.KnownNonModelled)
                // are recorded separately; only the genuinely unknown ones stay unaccounted.
                if (bucket.NoteKnownOr(line) || (plain != line && bucket.NoteKnownOr(plain))) continue;
                bucket.Note("tree: " + line);
            }
            bucket.SpeedScope = "";
        }
        bucket.SpeedScope = "";
    }

    private static bool TryApplyTreeLine(StatBucket bucket, GameStatMap statMap, string line)
    {
        if (statMap.Lines.TryGetValue(line, out var stats)) { StatInterpreter.ApplyAll(bucket, stats); return true; }
        Dictionary<string, decimal>? fallback = TreeStatFallbacks.TryGetValue(line, out var known) ? known : PatternFallbackStats(line);
        if (fallback is null) return false;
        StatInterpreter.ApplyAll(bucket, fallback);
        return true;
    }

    private static readonly System.Text.RegularExpressions.Regex MoreCritBonusLowLife = new(
        @"^([+-]?\d+(?:\.\d+)?)%\s+more\s+Critical\s+Damage\s+Bonus\s+when\s+on\s+Low\s+Life$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex LessCritBonusFullLife = new(
        @"^([+-]?\d+(?:\.\d+)?)%\s+less\s+Critical\s+Damage\s+Bonus\s+when\s+on\s+Full\s+Life$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Collects a conditional "more/less Critical Damage Bonus" line. The pinned tree stores
    /// Pain Attunement's two branches as one multi-line stat, so each line is matched on its own.
    /// The condition itself is resolved after the pools are known.</summary>
    private static bool TryConditionalCritBonus(StatBucket bucket, string line)
    {
        bool any = false;
        foreach (var part in line.Replace("\r", "").Split('\n'))
        {
            string text = part.Trim();
            if (text.Length == 0) continue;
            if (MoreCritBonusLowLife.Match(text) is { Success: true } more)
            {
                bucket.ConditionalCritBonus.Add((decimal.Parse(more.Groups[1].Value,
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture), true, false));
                any = true;
            }
            else if (LessCritBonusFullLife.Match(text) is { Success: true } less)
            {
                bucket.ConditionalCritBonus.Add((-decimal.Parse(less.Groups[1].Value,
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture), false, true));
                any = true;
            }
            else if (text.Contains("Critical Damage Bonus when on", StringComparison.OrdinalIgnoreCase))
            {
                // A shape we do not model: report it instead of silently dropping the whole line.
                bucket.Note("tree: " + text);
                any = true;
            }
        }
        return any;
    }

    /// <summary>Gear item id equipped in a slot, or null when the slot is empty.</summary>
    private static GearItem? Equipped(EquipmentPlan plan, string slot)
        => plan.Slots.TryGetValue(slot, out var id) ? plan.Items.FirstOrDefault(i => i.Id == id) : null;

    /// <summary>Kalandra's Touch: "Reflects opposite Ring" (PoB2 keeps the line as display-only and
    /// performs the duplication in its item handling).</summary>
    private static bool ReflectsOppositeRing(GearItem item)
        => item.Notes.Contains("Reflects opposite Ring", StringComparison.OrdinalIgnoreCase) ||
           item.Notes.Contains("Reflects your other Ring", StringComparison.OrdinalIgnoreCase);

    /// <summary>PoB2's <c>CanUseBondedModifiers</c> condition, which gates every rune/idol
    /// "Bonded: …" line (Modules/ModParser.lua tags "^bonded: " with it). Only the modifier "Gain the
    /// benefits of Bonded modifiers on Runes and Idols" sets it, and it can arrive from an item's text,
    /// an allocated passive or a quest reward — so every text source of the build is scanned. Without
    /// it the Bonded lines do not count at all, which is exactly what PoB2's own panel of the reference
    /// build shows: Morior Invictus' "Bonded: +60 to maximum Mana" and the two "+20 to maximum Mana"
    /// bonded rune lines are absent from its Mana pool.</summary>
    private static bool UsesBondedModifiers(BuildDocument build, TreeCatalog? tree)
    {
        const string enabler = "Gain the benefits of Bonded modifiers";
        if (build.Equipment is not null &&
            build.Equipment.Items.Any(item => item.Notes.Contains(enabler, StringComparison.OrdinalIgnoreCase))) return true;
        if (build.QuestRewards is not null &&
            build.QuestRewards.Any(line => line.Contains(enabler, StringComparison.OrdinalIgnoreCase))) return true;
        if (tree is null || build.Tree is null) return false;
        if (build.Tree.AllocatedNodes.Any(id => tree.Nodes.TryGetValue(id, out var node) &&
            node.Stats.Any(stat => stat.Contains(enabler, StringComparison.OrdinalIgnoreCase)))) return true;
        var definition = tree.Ascendancies.FirstOrDefault(a =>
            a.Id == build.Tree.Ascendancy?.Id && a.ClassIndex == build.Tree.ClassIndex);
        return definition is not null && build.Tree.Ascendancy is not null &&
            build.Tree.Ascendancy.AllocatedNodes.Any(id => definition.Graph.Nodes.TryGetValue(id, out var node) &&
                node.Stats.Any(stat => stat.Contains(enabler, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Applies one equipped item: pinned base implicits and affixes, the item's own text
    /// (unique modifiers, rune/enchant bonuses), its printed base defence values, movement speed,
    /// block and accuracy.</summary>
    private static void ApplyGearItem(StatBucket bucket, GameCatalog catalog, GearItem gear, bool isMainHand,
        ref GearItem? mainHand, ref decimal shieldBlock)
    {
        // The item's own text is the authoritative source for its implicit block: it carries the rolls
        // the item actually has ("Implicits: N"), while the pinned base table stores one max-roll value
        // per base. Both are reconciled in ApplyImplicitMods, and the text passes skip the block.
        var textImplicits = UniqueTextParser.ParseModsDetailed(EffectiveItemText(catalog, gear)).Where(entry => entry.Implicit).ToArray();
        if (!catalog.Bases.TryGetValue(gear.BaseId, out var b))
        {
            // Imported uniques have no pinned base identity. Their own text carries BOTH the
            // modifier lines and the base defence values ("Armour: 1072", "Energy Shield: 172"),
            // so both are read from it — exactly the numbers the game prints on the item.
            var textItem = new ItemContext();
            bucket.SpeedScope = "implicit:" + gear.Name;
            ApplyImplicitMods(bucket, null, textImplicits, textItem);
            ApplyUniqueModText(bucket, catalog, gear, textItem);
            bucket.SpeedScope = "";
            ApplyTextBases(bucket, gear, textItem, new UniqueTextParser.ItemBaseValues(null, null, null, null, null, 0, null));
            return;
        }
        if (isMainHand) mainHand = gear;
        var item = new ItemContext();
        bucket.SpeedScope = "implicit:" + gear.Name;
        ApplyImplicitMods(bucket, ImplicitValues(b), textImplicits, item);
        foreach (var roll in gear.Mods.Concat(gear.CorruptedMods))
        {
            // AllMods, not Mods: the importer's matcher also searches the jewel affix pool, so a
            // stored roll can be a jewel mod (that is how "+208 to maximum Mana" on a ring is kept).
            if (!catalog.AllMods.TryGetValue(roll.Id, out var mod)) continue;
            for (int i = 0; i < mod.Stats.Length && i < roll.Values.Length; i++)
            {
                bucket.SpeedScope = "item:" + gear.Name + " | " + roll.Id;
                StatInterpreter.Apply(bucket, mod.Stats[i].Id, roll.Values[i], item);
            }
        }
        // Uniques that DO resolve to a pinned base still carry user text. It is folded into the
        // same item context so a local defence increase from the text scales that item's own
        // base values, exactly like a native local modifier. On a WEAPON the damage/speed/crit
        // lines are local to that weapon and are read by WeaponContext instead.
        ApplyUniqueModText(bucket, catalog, gear, item, weaponLocal: b.Props.IsWeapon);
        // Non-unique imported items carry rune/enchant bonuses and affixes the pinned catalog does
        // not export; those lines are read here, once each (the catalog-matched ones are skipped).
        // Rune/enchant lines and affixes the pinned catalog does not export are read here, once each
        // (the catalog-matched ones are skipped).
        ApplyUnmatchedAffixText(bucket, catalog, gear);
        // Base defences scale with the item's quality (PoE2/PoB: +quality% of the base) and the
        // item's own local increases. The item's own printed values are authoritative — armour
        // bases scale with item level and the pinned table only stores one value per base — so
        // they are preferred over the pinned base whenever the text prints them.
        ApplyTextBases(bucket, gear, item, new UniqueTextParser.ItemBaseValues(b.Props.Armour, b.Props.Evasion, b.Props.EnergyShield, b.Props.Ward, null, 0, null));
        bucket.MoveInc += b.Props.MovementSpeed ?? 0;
        if ((b.Props.Block ?? 0) > 0 || item.BlockInc != 0)
            shieldBlock += (b.Props.Block ?? 0) * (1 + item.BlockInc / 100);
        bucket.AccFlat += item.AccuracyFlat;
    }

    /// <summary>Applies the quest rewards resolved from an imported build's config. PoB2 exposes every
    /// "useConfig" quest as a checkbox whose default state is ON, so a levelled character has them all;
    /// the importer stores the resulting lines verbatim and they are parsed here.</summary>
    private static void ApplyQuestRewards(StatBucket bucket, BuildDocument build)
    {
        if (build.QuestRewards is not { Length: > 0 } lines) return;
        foreach (var line in lines)
        {
            var parsed = QuestRewardParser.Parse(line);
            if (parsed.Count == 0)
            {
                // Honest: a reward we do not model is reported, never silently dropped.
                if (!string.IsNullOrWhiteSpace(line) && !bucket.NoteKnownOr(line)) bucket.Note("quest: " + line.Trim());
                continue;
            }
            foreach (var (id, value) in parsed) StatInterpreter.Apply(bucket, id, value, null);
        }
        bucket.Extras["QuestRewards"] = lines.Length;
    }

    /// <summary>Reads the affix lines of an imported item that the pinned catalog could not place.
    /// The importer stores the catalog-matched affixes as rolls; every other line of the item's own
    /// text is interpreted here with the reverse stat table, so affixes missing from the pinned
    /// export still contribute (for example "+# to Intelligence" and "+# to Spirit", which the pinned
    /// catalog does not carry at all). A line whose shape already matches one of the item's rolls is
    /// skipped because that roll already counts it. Lines owned by another pass are skipped too: the
    /// "Implicits: N" block (see <see cref="ApplyImplicitMods"/>) and rune/idol "Bonded: …" lines
    /// while the build lacks the CanUseBondedModifiers condition (see <see cref="SkipTextEntry"/>).</summary>
    private static void ApplyUnmatchedAffixText(StatBucket bucket, GameCatalog catalog, GearItem gear)
    {
        if (string.IsNullOrWhiteSpace(gear.Notes)) return;
        // Uniques apply their whole text elsewhere; only rare/magic/normal items need this pass.
        if (string.Equals(gear.Rarity, "unique", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var entry in UniqueTextParser.ParseModsDetailed(gear.Notes))
        {
            if (SkipTextEntry(bucket, entry)) continue;
            if (!entry.Tagged && CoveredByStoredRoll(catalog, gear, entry.Line)) continue;
            bucket.SpeedScope = "affix:" + gear.Name + " | " + entry.Line;
            StatInterpreter.Apply(bucket, entry.Id, entry.Value, null);
        }
        bucket.SpeedScope = "";
    }

    /// <summary>True when one of the item's stored rolls came from this text line.</summary>
    private static bool CoveredByStoredRoll(GameCatalog catalog, GearItem gear, string line)
    {
        foreach (var roll in gear.Mods.Concat(gear.CorruptedMods))
            if (catalog.AllMods.TryGetValue(roll.Id, out var mod) && ModLineMatcher.Covers(mod, line)) return true;
        return false;
    }

    /// <summary>Two kinds of item text lines are not a global modifier at the point the text is read:
    /// the "Implicits: N" block (owned by the implicit pass, which knows the pinned base implicit) and
    /// a rune/idol "Bonded: …" line (gated by PoB2's CanUseBondedModifiers condition — ModParser.lua
    /// tags every "bonded: " line with it, and nothing sets it unless the build carries "Gain the
    /// benefits of Bonded modifiers on Runes and Idols"). Skipped lines are counted, never silently
    /// dropped, so a build that needs them stays visible in the report.</summary>
    private static bool SkipTextEntry(StatBucket bucket, UniqueTextParser.ModEntry entry)
        => entry.Implicit || BondedGated(bucket, entry);

    /// <summary>A rune/idol "Bonded: …" line while the build lacks PoB2's CanUseBondedModifiers
    /// condition. Counted, never silently dropped, so a build that needs them stays visible.</summary>
    private static bool BondedGated(StatBucket bucket, UniqueTextParser.ModEntry entry)
    {
        if (!entry.Bonded || bucket.CanUseBondedModifiers) return false;
        bucket.Extras["BondedModsSkipped"] = bucket.Extras.TryGetValue("BondedModsSkipped", out var n) ? n + 1 : 1;
        return true;
    }

    /// <summary>Applies the item's implicits. The item's own text carries its REAL rolls ("Implicits: N"),
    /// while the pinned base table stores a single max-roll value per base, so the text wins per stat:
    /// a Lapis Amulet rolled "+12 to Intelligence" must not become 15 (pinned) + 12 (text). A stat the
    /// text does not mention still comes from the pinned base, which is what keeps items whose text
    /// omits an implicit (or whose base is not pinned at all) complete.</summary>
    private static void ApplyImplicitMods(StatBucket bucket, IReadOnlyDictionary<string, decimal>? pinned,
        IEnumerable<UniqueTextParser.ModEntry> textImplicits, ItemContext? item)
    {
        var fromText = textImplicits.Where(entry => !BondedGated(bucket, entry)).ToArray();
        if (pinned is not null)
        {
            var supplied = fromText.Select(entry => ImplicitKey(entry.Id)).ToHashSet(StringComparer.Ordinal);
            foreach (var (id, value) in pinned)
                if (!supplied.Contains(ImplicitKey(id))) StatInterpreter.Apply(bucket, id, value, item);
        }
        foreach (var entry in fromText) StatInterpreter.Apply(bucket, entry.Id, entry.Value, item);
        if (fromText.Length > 0)
            bucket.Extras["ImplicitsFromText"] = bucket.Extras.TryGetValue("ImplicitsFromText", out var n) ? n + fromText.Length : fromText.Length;
    }

    /// <summary>Canonical name of the stat an implicit feeds, so the pinned base table and the item's own
    /// text can be compared even when the two spell the same modifier differently: the pinned Lapis
    /// Amulet implicit is <c>additional_intelligence</c> while the item's own line "+12 to Intelligence"
    /// parses to <c>base_intelligence</c>, and StatInterpreter routes both into the same bucket.</summary>
    private static string ImplicitKey(string id)
    {
        if (id.StartsWith("additional_", StringComparison.Ordinal)) return id["additional_".Length..];
        if (id.StartsWith("base_", StringComparison.Ordinal)) return id["base_".Length..];
        return id;
    }

    /// <summary>Applies the item's own printed defence values. The game prints the level-scaled base
    /// ("Energy Shield: 243"); the pinned base table stores one value per base, so the printed value
    /// wins whenever the text carries it. Quality and the item's own local increases then scale it,
    /// exactly like a native base.</summary>
    /// <summary>Nodes that actually contribute with the given weapon set active. A node allocated for the
    /// other set keeps its id in the plan — it is still allocated in the game — but is left out of the
    /// calculation, exactly like PoB2's allocation mode.</summary>
    private static IReadOnlyList<int> ActiveTreeNodes(PassiveTreePlan plan, int weaponSet) =>
        plan.WeaponSetNodes.Count == 0
            ? plan.AllocatedNodes
            : [.. plan.AllocatedNodes.Where(id => !plan.WeaponSetNodes.TryGetValue(id, out var set) || set == weaponSet)];

    /// <summary>Every live instance of a persistent/aura skill: the active gem of each enabled socket group
    /// that belongs to the active weapon set, plus the skills the equipped items grant. Their levels carry the
    /// same "+N to Level of all … Skills" bonuses a socketed gem gets, and their quality is the gem's own
    /// quality plus the global "+N% to Quality of all Skills" the tree and items grant
    /// (PoB2's GemProperty mods, Modules/CalcSetup.lua:466-491). The grants that produced an instance come
    /// back with it, so their item lines can leave the "not accounted" report.</summary>
    private static (IReadOnlyList<AuraInstance> Instances, IReadOnlyList<ItemSkillGrant> LiveGrants) AuraInstances(
        GameCatalog catalog, BuildDocument build, StatBucket bucket, int weaponSet, IReadOnlyList<ItemSkillGrant> itemGrants)
    {
        var instances = new List<AuraInstance>();
        foreach (var group in build.Skills?.Groups ?? [])
        {
            // A group parked in a weapon slot only runs while that set is in hand (PoB2's group.slotEnabled,
            // Modules/CalcSetup.lua:1729); 0 means the group belongs to both sets.
            if (!group.Enabled || (group.WeaponSet != 0 && group.WeaponSet != weaponSet)) continue;
            var gem = catalog.Gems.GetValueOrDefault(group.Active.GemId);
            var skill = catalog.SkillData.ForGem(group.Active.GemId);
            // A SUPPORT can grant a buff to the character as well: Blazing Critical's statMap produces
            // DamageGainAsFire with a GlobalEffect Buff ("Critical Hits with Supported Skills imbue all of
            // your Attacks with Fire damage"), which PoB2 applies to every attack of the build — that is why
            // its changelog calls it "applying as a global buff". Supports are therefore scanned for every
            // enabled group, whether or not that group's active skill is an aura itself.
            foreach (var support in group.Supports)
            {
                var supportSkill = catalog.SkillData.ForGem(support.GemId);
                if (supportSkill is null || !AuraSkillCalculator.IsAura(catalog, supportSkill)) continue;
                var supportGem = catalog.Gems.GetValueOrDefault(support.GemId);
                int supportLevel = support.Level;
                if (supportGem is not null)
                    foreach (var (scope, value) in bucket.GemLevels) if (GemScopeMatches(scope, supportGem.Tags)) supportLevel += (int)value;
                instances.Add(new(supportSkill.Name, supportSkill.Id, Math.Clamp(supportLevel, 1, 40),
                    support.Quality + bucket.AllGemQuality));
            }
            if (gem is null || skill is null || !AuraSkillCalculator.IsAura(catalog, skill)) continue;
            int level = group.Active.Level;
            foreach (var (scope, value) in bucket.GemLevels) if (GemScopeMatches(scope, gem.Tags)) level += (int)value;
            instances.Add(new(gem.Name, skill.Id, Math.Clamp(level, 1, 40),
                group.Active.Quality + bucket.AllGemQuality));
        }
        var liveGrants = new List<ItemSkillGrant>();
        foreach (var grant in itemGrants)
        {
            var gem = catalog.Gems.Values.FirstOrDefault(g => string.Equals(g.Name, grant.Name, StringComparison.OrdinalIgnoreCase));
            var skill = gem is not null ? catalog.SkillData.ForGem(gem.Id) : ByEffectName(catalog, grant.Name);
            if (skill is null || !AuraSkillCalculator.IsAura(catalog, skill)) continue;
            int level = grant.Level;
            if (gem is not null)
                foreach (var (scope, value) in bucket.GemLevels) if (GemScopeMatches(scope, gem.Tags)) level += (int)value;
            // A gem an item grants has no quality of its own; only the global quality mods reach it.
            instances.Add(new(skill.Name, skill.Id, Math.Clamp(level, 1, 40), bucket.AllGemQuality));
            liveGrants.Add(grant);
        }
        return (instances, liveGrants);
    }

    /// <summary>Rage, exactly as PoB2 resolves it (Modules/CalcPerform.lua:777-791). The chain is:
    /// the config's rage count (a count input that only exists while the build can gain rage, so a non-zero
    /// value is the same "is rage live?" test PoB2 makes with its <c>Condition:CanGainRage</c> flag or a
    /// positive rage regeneration) clamped to the maximum rage — <c>Data/Misc.lua:123 ["BaseMaximumRage"] = 30</c>
    /// plus any "+N to Maximum Rage" — and then translated into a <c>RageEffect</c> of
    /// <c>floor(stacks × (1 + RageEffect INC / 100))</c>, which is the "Damage MORE" the tooltip promises as
    /// "1% More Attack Damage per 1 Rage". Berserk's "N% increased Rage effect" is what the INC part reads.</summary>
    private static void ResolveRage(StatBucket bucket, BuildConditions? conditions)
    {
        bucket.RageStacks = conditions?.RageStacks ?? 0;
        decimal maxRage = BaseMaximumRage + bucket.MaximumRageFlat;
        if (bucket.RageStacks > maxRage) bucket.RageStacks = (int)maxRage;
        if (bucket.RageStacks < 0) bucket.RageStacks = 0;
        bucket.RageEffectPct = Math.Floor(bucket.RageStacks * (1 + bucket.RageEffectInc / 100m));
        if (conditions?.RageStacks is > 0) bucket.Extras["Pool:Rage"] = bucket.RageStacks;
        if (bucket.RageEffectPct != 0) bucket.Extras["Pool:RageEffect"] = bucket.RageEffectPct;
    }

    /// <summary>PoB2's own base maximum rage (Data/Misc.lua:123), before gear and buffs.</summary>
    private const decimal BaseMaximumRage = 30m;

    /// <summary>Drops one occurrence of an already-noted honesty entry. A line whose modifiers a later pass
    /// did consume must not stay in the "not accounted" list.</summary>
    private static void ForgetNote(StatBucket bucket, string key)
    {
        if (!bucket.Unaccounted.TryGetValue(key, out int count)) return;
        bucket.UnaccountedTotal--;
        if (count <= 1) bucket.Unaccounted.Remove(key); else bucket.Unaccounted[key] = count - 1;
    }

    /// <summary>The effect whose name matches a granted skill that the pinned gem table has no entry for.</summary>
    private static SkillData? ByEffectName(GameCatalog catalog, string name) =>
        catalog.SkillData.ByEffectId.Values.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private static void ApplyTextBases(StatBucket bucket, GearItem gear, ItemContext item, UniqueTextParser.ItemBaseValues pinned)
    {
        var text = UniqueTextParser.ParseBaseValues(gear.Notes);
        int quality = Math.Clamp(gear.Quality, 0, 20);
        // A printed value is the FINAL item value (base already scaled by local increases and
        // quality), so it is added as-is. A pinned base is a raw base: the item's own flat local
        // affixes ("+71 to maximum Energy Shield" on armour is `local_energy_shield`) add to it and
        // are then scaled by its local increases — the printed case needs no such step because the
        // printed number already contains them.
        bucket.ArmourFlat += text.Armour ?? ((pinned.Armour ?? 0) + item.ArmourFlat) * (1 + (item.ArmourInc + quality) / 100);
        bucket.EvFlat += text.Evasion ?? ((pinned.Evasion ?? 0) + item.EvFlat) * (1 + (item.EvInc + quality) / 100);
        bucket.EsFlat += text.EnergyShield ?? ((pinned.EnergyShield ?? 0) + item.EsFlat) * (1 + (item.EsInc + quality) / 100);
        bucket.WardFlat += text.Ward ?? ((pinned.Ward ?? 0) + item.WardFlat) * (1 + (item.WardInc + quality) / 100);
        // A printed "Spirit: 192" is the item's final Spirit, exactly like its printed defences; the
        // pinned base (when one exists) is still scaled by the item's local Spirit increase.
        bucket.Spirit += text.Spirit ?? (pinned.Spirit ?? 0) * (1 + item.SpiritInc / 100);
    }

    /// <summary>Applies the deterministic unique-text parser to an imported unique item or jewel.
    /// Returns the number of recognised stat lines; 0 keeps the item honestly uninterpreted.
    /// A non-null <paramref name="item"/> routes item-local ids ("77% increased Energy Shield")
    /// onto that item's own base values; null keeps them global, which is the right fallback for
    /// items that have no base of their own (unique jewels).</summary>
    /// <summary>The text the calculator works from: the item's own text, or — when an item created in the
    /// editor has none and the unique is known to PoB2's data — a synthesised text. PoB2's unique format
    /// lists the implicit lines first, right after "Implicits: N", so the marker count stays correct.</summary>
    private static string? EffectiveItemText(GameCatalog catalog, GearItem gear)
    {
        if (!string.IsNullOrWhiteSpace(gear.Notes)) return gear.Notes;
        if (!string.Equals(gear.Rarity, "unique", StringComparison.OrdinalIgnoreCase)) return null;
        var data = catalog.UniqueData.For(gear.Name);
        var mods = catalog.UniqueData.ModsFor(gear.Name);
        if (data is null || mods.Count == 0) return null;
        return "Rarity: UNIQUE\n" + gear.Name + "\n" + data.BaseType + "\nImplicits: " + data.Implicits + "\n"
            + string.Join("\n", mods.Select(m => UniqueTextParser.ResolveRanges(m.Line)));
    }

    private static int ApplyUniqueModText(StatBucket bucket, GameCatalog catalog, GearItem gear, ItemContext? item = null, bool weaponLocal = false)
    {
        if (!string.Equals(gear.Rarity, "unique", StringComparison.OrdinalIgnoreCase)) return 0;
        string? text = EffectiveItemText(catalog, gear);
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int applied = 0;
        // Mageblood's "Legacy of X" lines are markers in PoB2's data and effects in its engine
        // (CalcPerform.lua:65-141, applied at :1502): Diamond is +75% Critical Hit Chance, Amethyst +45%
        // Chaos Resistance, and so on. They are read from the same unique text as the other lines.
        applied += MagebloodLegacies.Apply(bucket, text);
        bucket.SpeedScope = "unique:" + gear.Name;
        foreach (var entry in UniqueTextParser.ParseModsDetailed(text))
        {
            // A weapon's own damage/speed/crit lines belong to that weapon and are read by
            // WeaponContext as LOCAL mods; applying them globally as well would double count them and
            // inflate every other damage source (PoB2 Classes/Item.lua:1909-1949).
            if (weaponLocal && WeaponLocalIds.ContainsKey(entry.Id)) continue;
            if (SkipTextEntry(bucket, entry)) continue;
            bucket.SpeedScope = "unique:" + gear.Name + " | " + entry.Line;
            StatInterpreter.Apply(bucket, entry.Id, entry.Value, item);
            applied++;
        }
        bucket.SpeedScope = "";
        // Honesty: a mod-looking unique line that produced no stat id is reported, never dropped.
        foreach (var line in UniqueTextParser.UnmappedLines(text))
        {
            if (bucket.NoteKnownOr(line)) continue;
            bucket.Note("unique: " + line);
            bucket.Extras["UniqueLinesUnmapped"] = bucket.Extras.TryGetValue("UniqueLinesUnmapped", out var u) ? u + 1 : 1;
            // Named as well as counted: an unmapped unique line is a candidate for a real modelling gap
            // (PoB2 reads the same wording with its own parser), so the report says WHICH line, not just how
            // many - a bare count sent the last speed investigation looking in the wrong place.
            bucket.Extras["UniqueLineUnmapped:" + line] = 1;
        }
        if (applied > 0)
            bucket.Extras["UniqueTextMods"] = bucket.Extras.TryGetValue("UniqueTextMods", out var n) ? n + applied : applied;
        return applied;
    }

    private static Dictionary<string, decimal> ImplicitValues(ItemBase b)
    {
        var values = new Dictionary<string, decimal>();
        foreach (var stat in b.ImplicitStats)
            values[stat.Id] = values.TryGetValue(stat.Id, out var old) ? old + stat.Max : stat.Max; // implicits default to max roll
        return values;
    }

    /// <summary>Every stat id a support gem carries with its value for the gem level: its constant statics
    /// (PoB2's <c>constantStats</c>) plus its per-level values. PoB2 applies exactly these stats to the
    /// supported skill, mapped through the support's own statMap.</summary>
    private static IEnumerable<(string Id, decimal Value)> SupportStatValues(Gem support, int level)
    {
        if (support.Skill is not { } skill) yield break;
        foreach (var (id, value) in skill.Statics) yield return (id, value);
        if (skill.LevelValues(level) is { } values)
            foreach (var (id, value) in values)
                if (!skill.Statics.ContainsKey(id)) yield return (id, value);
    }

    /// <summary>The ids a WEAPON's own text has to be read as. PoB2 keeps every damage, speed and crit line
    /// of a weapon local to it (Classes/Item.lua:1909-1949 reads them with <c>calcLocal</c>), so the wording
    /// "226% increased Physical Damage" on a weapon is <c>local_physical_damage_+%</c> and never a global
    /// increase. The same wording on a ring or a tree node stays global — which is why this map is only
    /// applied to items whose base is a weapon.</summary>
    public static readonly IReadOnlyDictionary<string, string> WeaponLocalIds = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["physical_damage_+%"] = "local_physical_damage_+%",
        ["fire_damage_+%"] = "local_fire_damage_+%",
        ["cold_damage_+%"] = "local_cold_damage_+%",
        ["lightning_damage_+%"] = "local_lightning_damage_+%",
        ["chaos_damage_+%"] = "local_chaos_damage_+%",
        ["elemental_damage_+%"] = "local_elemental_damage_+%",
        ["attack_speed_+%"] = "local_attack_speed_+%",
        ["critical_strike_multiplier_+"] = "local_critical_strike_multiplier_+",
        ["attack_minimum_added_physical_damage"] = "local_minimum_added_physical_damage",
        ["attack_maximum_added_physical_damage"] = "local_maximum_added_physical_damage",
        ["attack_minimum_added_fire_damage"] = "local_minimum_added_fire_damage",
        ["attack_maximum_added_fire_damage"] = "local_maximum_added_fire_damage",
        ["attack_minimum_added_cold_damage"] = "local_minimum_added_cold_damage",
        ["attack_maximum_added_cold_damage"] = "local_maximum_added_cold_damage",
        ["attack_minimum_added_lightning_damage"] = "local_minimum_added_lightning_damage",
        ["attack_maximum_added_lightning_damage"] = "local_maximum_added_lightning_damage",
        ["attack_minimum_added_chaos_damage"] = "local_minimum_added_chaos_damage",
        ["attack_maximum_added_chaos_damage"] = "local_maximum_added_chaos_damage"
    };

    /// <summary>Collects only the weapon-LOCAL contributions of a weapon (they scale that weapon only).</summary>
    private static ItemContext WeaponContext(GameCatalog catalog, GearItem weapon)
    {
        // Weapon quality scales the weapon's own PHYSICAL damage (PoB2 multiplies it in as its own factor,
        // Classes/Item.lua:1928-1933) and is the item's own quality — an imported weapon may carry more
        // than the 20% a plain currency item has (The Ordained: 26).
        var item = new ItemContext { WeaponQuality = Math.Clamp(weapon.Quality, 0, 100) };
        if (!catalog.Bases.TryGetValue(weapon.BaseId, out var b)) b = null;
        var sink = new StatBucket();
        if (b is not null) StatInterpreter.ApplyAll(sink, ImplicitValues(b), item);
        foreach (var roll in weapon.Mods.Concat(weapon.CorruptedMods))
        {
            if (!catalog.AllMods.TryGetValue(roll.Id, out var mod)) continue;
            for (int i = 0; i < mod.Stats.Length && i < roll.Values.Length; i++)
                StatInterpreter.Apply(sink, mod.Stats[i].Id, roll.Values[i], item);
        }
        // A unique weapon has no pinned base, so its own text carries every LOCAL modifier it has
        // ("226% increased Physical Damage", "Adds 1 to 296 Lightning Damage", "+7.54% to Critical Hit
        // Chance"). PoB2 applies an item's text to that item, so those belong in this weapon's context —
        // a local id applied to the global bucket would be silently dropped by the attack formula.
        // Lines already covered by a stored roll (matched affixes) are skipped, exactly like the
        // rare-item text pass, so nothing is counted twice.
        foreach (var entry in UniqueTextParser.ParseModsDetailed(EffectiveItemText(catalog, weapon)))
        {
            if (entry.Implicit || entry.Bonded) continue;
            if (!entry.Tagged && CoveredByStoredRoll(catalog, weapon, entry.Line)) continue;
            StatInterpreter.Apply(sink, WeaponLocalId(entry.Id), entry.Value, item);
        }
        return item;
    }

    /// <summary>The local id a weapon's own line has to be read as, or the id itself when the line is not
    /// part of PoB2's weapon-local family.</summary>
    public static string WeaponLocalId(string id) => WeaponLocalIds.TryGetValue(id, out var local) ? local : id;

    private static SkillDpsInfo? SkillInfo(GameCatalog catalog, SkillGroup group, bool setMatches, ItemBase? mainBase, ItemContext? mainLocal, StatBucket bucket, decimal? playerHitChance)
    {
        var activeGem = catalog.Gems.GetValueOrDefault(group.Active.GemId);
        if (activeGem is null) return null;
        // A deployed-skill host (e.g. Spell Totem) casts a linked gem that the official export lists
        // inside the support list. The host itself deals no damage, so the group's hit damage comes
        // from the hosted gem; the slot keeps the host's name.
        var gem = activeGem;
        var activeSelection = group.Active;
        if (!GemCanDealDamage(gem, activeSelection.Level, mainBase))
        {
            foreach (var support in group.Supports)
            {
                var candidate = catalog.Gems.GetValueOrDefault(support.GemId);
                if (candidate is not null && candidate.Kind != "support" && GemCanDealDamage(candidate, support.Level, mainBase))
                { gem = candidate; activeSelection = support; break; }
            }
        }
        var skill = gem.Skill;
        bool isAttack = gem.Tags.Contains("attack");
        // A skill deployed by a totem inherits the totem's cast speed (PoB2 is_totem host).
        bool deployedByTotem = activeGem.Skill?.Statics.ContainsKey("is_totem") == true ||
            activeGem.Skill?.Statics.ContainsKey("is_spell_totem") == true;
        int levelFromItems = 0;
        foreach (var (scope, value) in bucket.GemLevels) if (GemScopeMatches(scope, gem.Tags)) levelFromItems += (int)value;
        foreach (var (scope, value) in mainLocal?.GemLevels ?? new List<(string, decimal)>()) if (GemScopeMatches(scope, gem.Tags)) levelFromItems += (int)value;
        // Support gems can raise the supported skill's level ("supported_lightning_skill_gem_level_+",
        // Lightning Mastery), which is a large damage factor for spells.
        int levelFromSupports = SupportGemLevelBonus(catalog, group, gem);
        levelFromItems += levelFromSupports;
        int effectiveLevel = Math.Clamp(activeSelection.Level + levelFromItems, 1, 40);
        // A "+N% to Quality of all Skills" mod reaches only gems that grant an active skill: PoB2 maps
        // the wording to a GemProperty mod whose keyword is "grants_active_skill", so a support gem
        // never receives it. The gem's own quality is the one the share code stores (plus a corrupted
        // gem's own roll, which PoB2 keeps in corruptLevel and folds into the level instead).
        int effectiveQuality = activeSelection.Quality +
            (gem.Tags.Contains("grants_active_skill") ? (int)bucket.AllGemQuality : 0);
        var notes = new List<string>();
        var breakdown = new List<string>();
        if (gem != activeGem) breakdown.Add("Hosted: " + gem.Name + " (deployed by " + activeGem.Name + ")");
        if (levelFromItems != 0)
            breakdown.Add("Gem level: " + effectiveLevel + " (" + activeSelection.Level + " base + " + levelFromItems + " from items/supports)");
        if (effectiveQuality > 0)
            breakdown.Add("Quality: " + effectiveQuality + "% (" + activeSelection.Quality + " own + " + (effectiveQuality - activeSelection.Quality) + " global)");
        decimal? effectiveCrit = null;
        if (!group.Enabled) notes.Add("DisabledGroup");
        if (!setMatches) notes.Add("WrongWeaponSet");
        var weaponWords = WeaponWords(mainBase);
        // v4 supports: apply the support gems' pinned "_final" multipliers (data-driven, no invention).
        // Applied multipliers are collected here and folded into rate/crit/damage below.
        decimal rateMore = 1m, critChanceMore = 1m, critBonusMore = 1m;
        var damageMore = new decimal[] { 1m, 1m, 1m, 1m, 1m };
        decimal damageMoreGeneral = 1m;
        int supportsApplied = 0;
        // Support flags that remove whole damage types (Brutality): "deal_no_elemental_damage" and
        // "base_deal_no_chaos_damage" sit in the support's constant statics.
        bool dealNoElemental = false, dealNoChaos = false;
        bool projectileSpeedToDamage = false;
        // PoB2's own support mapping: every support ships a statMap (Data/Skills/sup_*.lua) that names the
        // mod each of its stat ids produces. Those ids are applied through PoB2ModTranslator, and the
        // hand-written "_final" heuristics below only handle ids the statMap does not cover, so nothing is
        // counted twice.
        var supportSink = new SupportModSink();
        var supportMapped = new HashSet<string>(StringComparer.Ordinal);
        decimal speedFromSupportsInc = 0, critChanceFromSupportsInc = 0;
        foreach (var selection in group.Supports)
        {
            var support = catalog.Gems.GetValueOrDefault(selection.GemId);
            var statics = support?.Skill?.Statics;
            if (support is null || statics is null || statics.Count == 0) continue;
            bucket.SpeedScope = "support:" + support.Name;
            if (statics.ContainsKey("deal_no_elemental_damage")) dealNoElemental = true;
            if (statics.ContainsKey("base_deal_no_chaos_damage") || statics.ContainsKey("deal_no_chaos_damage")) dealNoChaos = true;
            // "Increases and reductions to Projectile speed also apply to Damage" (Projectile Acceleration III):
            // the support's own statSet carries the flag, and PoB2 folds the skill's projectile-speed
            // increases into its damage when it is present.
            if (catalog.SkillData.ForGem(selection.GemId) is { } supportEffects &&
                supportEffects.Effects.Any(e => e.Stats.Contains("projectile_speed_additive_modifiers_also_apply_to_projectile_damage")))
                projectileSpeedToDamage = true;
            if (catalog.SkillData.ForGem(selection.GemId) is { } supportData)
            {
                foreach (var (id, value) in SupportStatValues(support, selection.Level))
                {
                    if (value == 0) continue;
                    // PoB2 looks a stat id up in the skill's own statMap first and then in the global
                    // skill-stat table (Data/SkillStatMap.lua). Only that table's flag entries are applied:
                    // numeric entries there overlap the "_final" fallback below (Rapid Casting's
                    // base_cast_speed_+% would be counted twice), so they stay catalogued as unhandled until
                    // both paths are unified. Flags have no such overlap — this is what turns Garukhan's
                    // Resolve's "attacks_roll_crits_twice" into the BifurcateCrit behaviour of its panel.
                    var effect = supportData.Effects.FirstOrDefault(e => e.StatMap.ContainsKey(id));
                    IReadOnlyList<SkillStatSpec> specs = effect is not null && effect.StatMap.TryGetValue(id, out var ownSpecs)
                        ? ownSpecs
                        : [.. catalog.SkillData.SpecsFor(id, null).Where(s => s.Spec.StartsWith("flag(", StringComparison.Ordinal) || GlobalNumericAllowed(id))];
                    if (specs.Count == 0) continue;
                    // The id is claimed by PoB2's own statMap as soon as it has a spec there, whether or not
                    // this model could apply it. Without this the "_final" fallback below would approximate a
                    // mod PoB2 explicitly scopes — Execute III's "more Damage against enemies on Low Life" was
                    // applied a second time that way, a ×1.3 the reference never counts.
                    if (effect is not null && effect.StatMap.ContainsKey(id)) supportMapped.Add(id);
                    foreach (var spec in specs)
                    {
                        if (PoB2ModTranslator.Parse(spec.Spec) is not { } parsed) continue;
                        // Only an id this model actually implemented counts as mapped, so the fallback
                        // below still covers the ones it does not (e.g. Heft's maximum-physical MORE).
                        if (supportSink.Apply(parsed, value * (spec.Mult ?? 1m), isAttack, gem.Tags, bucket.Conditions)) supportMapped.Add(id);
                    }
                }
            }
            bool any = false;
            foreach (var (id, value) in statics)
            {
                if (!id.EndsWith("_final", StringComparison.Ordinal) || value == 0) continue;
                if (supportMapped.Contains(id)) continue;   // PoB2's statMap already named this id's mod
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
                    // A support final is a MORE/less multiplier on the supported skill. Sub-parts of a skill
                    // ("branching_fissure_damage_+%_final" is the fissure explosion, not the main hit) never
                    // reach it — PoB2 leaves those stats unmapped (Data/Skills/*.lua statMap).
                    if (id.Contains("fissure", StringComparison.Ordinal) || id.Contains("detonation", StringComparison.Ordinal)
                        || id.Contains("area_of_effect", StringComparison.Ordinal) || id.Contains("cost", StringComparison.Ordinal)
                        || id.Contains("chain", StringComparison.Ordinal) || id.Contains("speed", StringComparison.Ordinal)) continue;
                    var words = SupportScopeWords(id);
                    // PoB2 maps every one of these to a plain mod("Damage", "MORE") — the words in front of
                    // "damage" (support/skill/gem/base/maximum/spell/...) describe the support's applicability,
                    // not a damage scope, so they must not route the multiplier into a scoped bucket.
                    var scopeWords = words.Where(w => w is not ("support" or "skill" or "gem" or "base" or "maximum" or "minimum")).ToArray();
                    int type = TypeIndex(words);
                    bool elemental = words.Contains("elemental");
                    if (words.Contains("spell") && isAttack) continue;
                    if (words.Contains("attack") && !isAttack) continue;
                    if (scopeWords.Length > 0 && !scopeWords.All(w => gem.Tags.Contains(w) || weaponWords.Contains(w)))
                    {
                        if (type < 0 && !elemental) continue;
                    }
                    if (type >= 0) { damageMore[type] *= factor; any = true; }
                    else if (elemental) { damageMore[1] *= factor; damageMore[2] *= factor; damageMore[3] *= factor; any = true; }
                    else { damageMoreGeneral *= factor; any = true; }
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
            return Record(0, 0, new DamageSplit(0, 0, 0, 0, 0), 0, 0, 0, null, null, isAttack, notes, breakdown, [], null, group, gem, setMatches, levelFromItems, effectiveLevel, effectiveQuality);
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
        // Projectile Acceleration III turns the character's projectile-speed increases into damage for this
        // skill (PoB2's projectile_speed_additive_modifiers_also_apply_to_projectile_damage flag).
        if (projectileSpeedToDamage)
        {
            decimal speedAsDamage = isAttack ? bucket.ProjectileSpeedInc : bucket.ProjectileSpeedInc + bucket.SpellProjectileSpeedInc;
            if (speedAsDamage != 0)
            {
                scopedGeneral += speedAsDamage;
                breakdown.Add("Projectile speed as damage: +" + Dmg(speedAsDamage) + "% (Projectile Acceleration III)");
            }
        }
        // Gemling's "Integrated Efficiency": each support gem of the group counts by its colour — red adds
        // Damage, green adds Skill Speed (the skill's rate), blue adds Critical Hit Chance — exactly what PoB2
        // does in CalcOffence.lua:684-722.
        if (bucket.DamageIncPerRedSupport != 0 || bucket.SkillSpeedIncPerGreenSupport != 0 || bucket.CritChanceIncPerBlueSupport != 0)
        {
            int red = 0, green = 0, blue = 0;
            foreach (var selection in group.Supports)
                switch (catalog.Gems.GetValueOrDefault(selection.GemId)?.Color)
                {
                    case "r": red++; break;
                    case "g": green++; break;
                    case "b": blue++; break;
                }
            if (bucket.DamageIncPerRedSupport != 0 && red > 0)
            {
                decimal bonus = red * bucket.DamageIncPerRedSupport;
                scopedGeneral += bonus;
                breakdown.Add("Support gems (poB2 per-red): +" + Dmg(bonus) + "% damage (" + red + " red x " + Dmg(bucket.DamageIncPerRedSupport) + "%)");
            }
            if (bucket.SkillSpeedIncPerGreenSupport != 0 && green > 0)
            {
                decimal bonus = green * bucket.SkillSpeedIncPerGreenSupport;
                speedFromSupportsInc += bonus;
                breakdown.Add("Support gems (PoB2 per-green): +" + Dmg(bonus) + "% skill speed (" + green + " green x " + Dmg(bucket.SkillSpeedIncPerGreenSupport) + "%)");
            }
            if (bucket.CritChanceIncPerBlueSupport != 0 && blue > 0)
            {
                decimal bonus = blue * bucket.CritChanceIncPerBlueSupport;
                critChanceFromSupportsInc += bonus;
                breakdown.Add("Support gems (PoB2 per-blue): +" + Dmg(bonus) + "% crit chance (" + blue + " blue x " + Dmg(bucket.CritChanceIncPerBlueSupport) + "%)");
            }
        }
        // PoB2's own support modifiers (statMap-driven, see the support loop): folded into exactly the same
        // places PoB2 folds them — damage MORE/INC, type-scoped damage, rate, crit chance and multiplier.
        if (supportSink.DamageMore != 1m || supportSink.DamageInc != 0 || supportSink.TypeMore.Count > 0 || supportSink.TypeInc.Count > 0)
        {
            damageMoreGeneral *= supportSink.DamageMore;
            scopedGeneral += supportSink.DamageInc;
            var folded = new List<string>();
            for (int i = 0; i < Types.Length; i++)
            {
                decimal more = supportSink.TypeMoreFor(Types[i]), inc = supportSink.TypeIncFor(Types[i]);
                if (more != 1m) damageMore[i] *= more;
                if (inc != 0) scopedType[i] += inc;
                if (more != 1m || inc != 0) folded.Add(Types[i] + (more != 1m ? " x" + Dmg(more) : "") + (inc != 0 ? " +" + Dmg(inc) + "%" : ""));
            }
            if (folded.Count > 0) breakdown.Add("Support damage (PoB2 statMap): " + string.Join(", ", folded));
            if (supportSink.DamageMore != 1m) breakdown.Add("Support damage (PoB2 statMap): more x" + Dmg(supportSink.DamageMore));
        }
        if (supportSink.RateMore != 1m || supportSink.RateInc != 0)
        {
            rateMore *= supportSink.RateMore;
            speedFromSupportsInc += supportSink.RateInc;
        }
        if (supportSink.CritChanceMore != 1m || supportSink.CritChanceInc != 0)
        {
            critChanceMore *= supportSink.CritChanceMore;
            critChanceFromSupportsInc += supportSink.CritChanceInc;
        }
        if (supportSink.CritMultiplierMore != 1m) critBonusMore *= supportSink.CritMultiplierMore;
        // Character-wide MOREs granted by persistent buffs (Charge Infusion's "with Frenzy/Power Charges"
        // mods): PoB2's Speed/CritChance/Damage MORE land on the skill itself, which is where they apply here.
        if (bucket.DamageMorePct != 0)
        {
            damageMoreGeneral *= 1 + bucket.DamageMorePct / 100m;
            breakdown.Add("Buff damage (more): x" + Dmg(1 + bucket.DamageMorePct / 100m));
        }
        if (bucket.CritChanceMorePct != 0)
        {
            critChanceMore *= 1 + bucket.CritChanceMorePct / 100m;
            breakdown.Add("Buff crit chance (more): x" + Dmg(1 + bucket.CritChanceMorePct / 100m));
        }
        if (isAttack ? bucket.AttackSpeedMorePct != 0 : bucket.CastSpeedMorePct != 0)
        {
            decimal speedMore = isAttack ? bucket.AttackSpeedMorePct : bucket.CastSpeedMorePct;
            rateMore *= 1 + speedMore / 100m;
            breakdown.Add("Buff " + (isAttack ? "attack" : "cast") + " speed (more): x" + Dmg(1 + speedMore / 100m));
        }
        // Rage (Modules/CalcPerform.lua:787-791): the rage count, scaled by its effect, is "Damage MORE" — for
        // attacks, or for spells when the build grants Rage spell damage instead. Its own multiplier vocabulary
        // ("per Rage") already reads the same stack count elsewhere.
        if (isAttack && bucket.RageEffectPct != 0)
        {
            damageMoreGeneral *= 1 + bucket.RageEffectPct / 100m;
            breakdown.Add("Rage damage (more): x" + Dmg(1 + bucket.RageEffectPct / 100m) + " (" + bucket.RageStacks + " rage)");
        }
        // Per-element buff multipliers (Elemental Conflux's "N% more damage" for each element, Trinity's
        // ElementalDamage MORE from resonance).
        foreach (var (type, more) in bucket.TypeMorePct)
        {
            int index = Array.IndexOf(Types, type);
            if (index >= 0 && more != 0) damageMore[index] *= 1 + more / 100m;
        }
        if (bucket.TypeMorePct.Count > 0)
        {
            var foldedTypes = Types.Where(t => bucket.TypeMorePct.TryGetValue(t, out var m) && m != 0)
                .Select(t => t + " x" + Dmg(1 + bucket.TypeMorePct[t] / 100m));
            if (foldedTypes.Any()) breakdown.Add("Buff damage by type (more): " + string.Join(", ", foldedTypes));
        }
        foreach (var (type, inc) in bucket.TypeIncPct)
        {
            int index = Array.IndexOf(Types, type);
            if (index >= 0) scopedType[index] += inc;
        }
        bool critBifurcates = supportSink.CritBifurcates;
        if (supportSink.LifeRegenPercent != 0) bucket.LifeRegenPercentPerSecond += supportSink.LifeRegenPercent;
        if (supportSink.ManaRegenInc != 0) bucket.ManaRegenInc += supportSink.ManaRegenInc;
        if (supportSink.AccuracyInc != 0) bucket.AccInc += supportSink.AccuracyInc;
        decimal critChanceCap = supportSink.CritChanceCap ?? 100m;
        foreach (var (type, value) in supportSink.GainAs)
            bucket.GainAs[type] = bucket.GainAs.TryGetValue(type, out var gain) ? gain + value : value;
        foreach (var (key, count) in supportSink.Unhandled) bucket.Extras["SupportSpecUnhandled:" + key] = count;
        // PoB2's per-skill data (skilldata.json): attacks scale the weapon base damage by the gem's
        // baseMultiplier and their attack rate by attackSpeedMultiplier; a skill's cooldown can come
        // from its per-level table when the pinned catalog has none (Frost Bomb: 6 s); and gem quality
        // is the gem's own qualityStats.
        var skillData = catalog.SkillData.ForGem(gem.Id);
        decimal baseMultiplier = skillData?.Number("baseMultiplier", effectiveLevel) ?? 1m;
        decimal attackSpeedMultiplier = (skillData?.Number("attackSpeedMultiplier", effectiveLevel) ?? 0m) / 100m;
        if (effectiveQuality > 0 && skillData is { QualityStats.Count: > 0 } qualitySkill)
        {
            // Each quality entry adds value x quality of that stat to the skill. Most entries are not
            // damage at all (Arc: +0.1 chains per quality, Frost Bomb: exposure cap, Flame Wall: ignite
            // chance), so the earlier "+1% increased damage per point of quality" approximation is
            // replaced by the real values: damage-shaped ids are applied, the rest are reported.
            foreach (var (stat, value) in qualitySkill.QualityStats)
            {
                decimal total = value * effectiveQuality;
                breakdown.Add("Quality stat: " + stat + " +" + Dmg(total) + " (" + Dmg(value) + " x " + effectiveQuality + "% quality)");
                if (stat.Contains("_damage_+%_final", StringComparison.Ordinal)) damageMoreGeneral *= 1 + total / 100m;
                else if (stat.Contains("_damage_+%", StringComparison.Ordinal)) scopedGeneral += total;
            }
        }
        else if (effectiveQuality > 0)
        {
            breakdown.Add("Quality " + effectiveQuality + "%: the pinned skill data has no qualityStats for this gem, so its quality effect is not modelled");
        }

        if (skill is null)
        {
            notes.Add("NoGemData");
        }
        else
        {
            manaCost = skill.LevelCosts(effectiveLevel)?.TryGetValue("Mana", out var mc) == true ? mc : null;
            // Archmage: "adds X per myriad of maximum Mana to the Mana cost of non-channelling spells"
            // (ManaCostNoMult BASE with SkillType.Spell + not Channel). One myriad is 10,000 Mana.
            if (manaCost is decimal archmageBase && bucket.ManaCostPerMyriadMaxMana != 0 && !gem.Tags.Contains("channelling"))
            {
                decimal added = bucket.ManaFinal * bucket.ManaCostPerMyriadMaxMana / 10_000m;
                if (added != 0)
                {
                    manaCost = archmageBase + added;
                    breakdown.Add("Archmage mana cost: +" + Dmg(added) + " (" + Dmg(bucket.ManaCostPerMyriadMaxMana) + " per 10,000 maximum Mana)");
                }
            }
            // Gemling's "most numerous colour" notable: with blue supports in the majority the skills cost
            // 30% less (PoB2's ManaCost MORE under MostNumerousBlueSocketedSupports, ModParser.lua:3369-3371).
            decimal mostNumerousCostPct = MostNumerousColourIsBlue(group, catalog) ? bucket.MostNumerousColourCostMorePct : 0m;
            if (manaCost is decimal baseManaCost && (bucket.ManaCostFinalPct != 0 || bucket.ManaCostInc != 0 || mostNumerousCostPct != 0))
            {
                // "N% reduced Mana Cost" is PoB2's ManaCost INC (mult = -1) applied as a divisor, then
                // its final (more/less) multiplier, e.g. Eldritch Battery's "Mana Costs are Doubled".
                decimal factor = (1 + (bucket.ManaCostFinalPct + mostNumerousCostPct) / 100m) / (1 + bucket.ManaCostInc / 100m);
                manaCost = baseManaCost * factor;
                if (bucket.ManaCostFinalPct != 0) breakdown.Add("More (mana cost): x" + Dmg(1 + bucket.ManaCostFinalPct / 100m));
                if (mostNumerousCostPct != 0) breakdown.Add("Most numerous support colour (blue): x" + Dmg(1 + mostNumerousCostPct / 100m) + " cost");
                if (bucket.ManaCostInc != 0) breakdown.Add("Mana cost: ÷" + Dmg(1 + bucket.ManaCostInc / 100m) + " (from " + Dmg(bucket.ManaCostInc) + "% reduced cost)");
            }
            // Weapon-class-scoped crit and attack-speed mods ("40% increased Critical Damage Bonus with
            // Spears"): a scope word matches when it is a tag of the gem or a word of the main-hand class,
            // exactly like the scoped damage lines resolved above.
            decimal scopedCritChance = 0, scopedCritBonus = 0, scopedAttackSpeed = 0;
            foreach (var (scopeWords2, value) in bucket.ScopedCritChanceInc)
                if (scopeWords2.All(w => gem.Tags.Contains(w) || weaponWords.Contains(w))) scopedCritChance += value;
            foreach (var (scopeWords2, value) in bucket.ScopedCritBonusAdd)
                if (scopeWords2.All(w => gem.Tags.Contains(w) || weaponWords.Contains(w))) scopedCritBonus += value;
            if (isAttack)
                foreach (var (scopeWords2, value) in bucket.ScopedAttackSpeedInc)
                    if (scopeWords2.All(w => gem.Tags.Contains(w) || weaponWords.Contains(w))) scopedAttackSpeed += value;
            if (scopedCritChance != 0 || scopedCritBonus != 0 || scopedAttackSpeed != 0)
                breakdown.Add("Weapon-class mods (" + string.Join("/", weaponWords) + "): crit chance +" + Dmg(scopedCritChance) +
                    "%, crit bonus +" + Dmg(scopedCritBonus) + "%, attack speed +" + Dmg(scopedAttackSpeed) + "%");
            decimal speedInc = bucket.CastSpeedInc + bucket.SkillSpeedInc;
            if (isAttack)
            {
                if (mainBase?.Props.AttackTime is not int attackTime) { notes.Add("NoWeapon"); return Record(0, 0, split, 0, 0, 0, effectiveCrit, manaCost, isAttack, notes, breakdown, [], null, group, gem, setMatches, levelFromItems, effectiveLevel, effectiveQuality); }
                // A totem-deployed ATTACK inherits the totem's attack-speed mods (PoB2: totem_skill_attack_speed_+%
                // is Speed INC with ModFlag.Attack and the Totem keyword). Its cast counterpart below is a
                // separate pool, because PoB2's flags are exclusive.
                decimal totemSpeedInc = deployedByTotem
                    ? bucket.TotemAttackSpeedInc + bucket.TotemsAttackSpeedPerActiveTotem * TotemsSummoned(activeGem, activeSelection.Level)
                      + SupportTotemSpeedInc(catalog, group, true)
                    : 0;
                rate = 1000m / attackTime * (1 + (bucket.AttackSpeedInc + totemSpeedInc + speedInc + scopedAttackSpeed + (mainLocal?.AttackSpeedInc ?? 0) + speedFromSupportsInc) / 100) * rateMore
                    * (1 + attackSpeedMultiplier);
                breakdown.Add("Attack speed: +" + Dmg(bucket.AttackSpeedInc) + "% gear/tree, +" + Dmg(scopedAttackSpeed) + "% weapon-class, +" +
                    Dmg(mainLocal?.AttackSpeedInc ?? 0) + "% weapon-local, +" + Dmg(speedFromSupportsInc) + "% supports/buffs" +
                    (totemSpeedInc != 0 ? ", +" + Dmg(totemSpeedInc) + "% totem ("
                        + TotemsSummoned(activeGem, activeSelection.Level) + " summoned)" : ""));
                // "N% more Skill Speed while Off Hand is empty and you have a One-Handed Martial Weapon equipped
                // in your Main Hand" is PoB2's Speed MORE (ModParser.lua:2333), so it multiplies the rate.
                if (bucket.SkillSpeedMorePct != 0)
                {
                    rateMore *= 1 + bucket.SkillSpeedMorePct / 100m;
                    rate *= 1 + bucket.SkillSpeedMorePct / 100m;
                    breakdown.Add("Skill speed (off hand empty, one-handed weapon): x" + Dmg(1 + bucket.SkillSpeedMorePct / 100m));
                }
                if (attackSpeedMultiplier != 0) breakdown.Add("Attack speed multiplier: x" + Dmg(1 + attackSpeedMultiplier));
                decimal weaponCrit = (mainBase.Props.CritChance ?? 0) / 100m + (mainLocal?.CritChanceAdd ?? 0);
                // PoB2: (baseCrit + sum of CritChance BASE) * (1 + inc/100) * more (CalcOffence.lua:3718).
                critChance = Math.Min(critChanceCap, (weaponCrit + bucket.CritChanceAdd) * (1 + (bucket.CritChanceInc + bucket.AttackCritInc + critChanceFromSupportsInc + scopedCritChance) / 100) * critChanceMore);
                // Garukhan's Resolve: "…will Bifurcate Critical Hits" — PoB2 rolls the chance twice and gives
                // the multiplier 2 x pre/post (CalcOffence.lua:3736-3756), which is what its panel reports as
                // CritChance 75 against PreEffectiveCritChance 50 and CritBifurcates x1.33.
                if (critBifurcates && critChance > 0)
                {
                    decimal preBifurcate = critChance;
                    decimal postBifurcate = (1 - (1 - preBifurcate / 100m) * (1 - preBifurcate / 100m)) * 100m;
                    if (postBifurcate > 0)
                    {
                        // PoB2 (CalcOffence.lua:3824-3843): the crit multiplier gets "MORE (pre^2/100) / post"
                        // per cent — the chance that BOTH rolls crit, divided by the chance that at least one
                        // did — so the extra damage becomes damageBonus + conditionalChance x damageBonus.
                        // It is a MORE on the crit bonus, NOT a replacement of it: assigning it to `critBonus`
                        // here and then recomputing critBonus below dropped the whole bifurcation bonus.
                        decimal conditionalChance = (preBifurcate * preBifurcate / 100m) / postBifurcate;
                        critBonusMore *= 1 + conditionalChance;
                        critChance = postBifurcate;
                        breakdown.Add("Crit bifurcation (Garukhan's Resolve): " + Dmg(preBifurcate) + "% -> " + Dmg(postBifurcate) +
                            "%, crit damage x" + Dmg(1 + conditionalChance));
                    }
                }
                critBonus = (BaseCritDamageBonus + bucket.CritBonusAdd + bucket.AttackCritBonusAdd + scopedCritBonus + (mainLocal?.CritBonusAdd ?? 0))
                    * (1 + bucket.CritBonusInc / 100) * (1 + bucket.CritBonusMorePct / 100) * critBonusMore;
                // Component breakdown of the crit multiplier: the fastest way to see WHERE an attack's bonus
                // comes from (PoB2: (base + BASE sums) x (1 + INC/100) x MORE, CalcOffence.lua:3813-3858).
                breakdown.Add("Crit bonus: base " + Dmg(BaseCritDamageBonus) + " +" + Dmg(bucket.CritBonusAdd) + " general +" +
                    Dmg(bucket.AttackCritBonusAdd) + " attack +" + Dmg(scopedCritBonus) + " weapon-class +" +
                    Dmg(mainLocal?.CritBonusAdd ?? 0) + " weapon-local, inc +" + Dmg(bucket.CritBonusInc) + "%, more x" +
                    Dmg((1 + bucket.CritBonusMorePct / 100m) * critBonusMore));
                split = AttackSplit(mainBase, mainLocal, bucket, scopedGeneral, scopedType, gem, notes, breakdown);
                if (baseMultiplier != 1m)
                {
                    split = new DamageSplit(split.Physical * baseMultiplier, split.Fire * baseMultiplier,
                        split.Cold * baseMultiplier, split.Lightning * baseMultiplier, split.Chaos * baseMultiplier);
                    breakdown.Add("Base damage multiplier (" + gem.Name + " " + effectiveLevel + "): x" + Dmg(baseMultiplier));
                }
                if (dealNoElemental || dealNoChaos)
                {
                    breakdown.Add("Support: deals no " + string.Join(" and no ",
                        new[] { dealNoElemental ? "Elemental" : null, dealNoChaos ? "Chaos" : null }.Where(x => x is not null)) +
                        " damage (Brutality-class flag)");
                    split = ApplyDealNo(split, dealNoElemental, dealNoChaos);
                }
                split = ApplyDamageMore(split, damageMore, damageMoreGeneral);
            }
            else
            {
                decimal castTime = Math.Max(1, skill.CastTime ?? 1000);
                // A totem-deployed skill casts at the totem's speed, not the player's: one pool for the
                // totem's own cast-speed mods, one for the supports' statics, and — PoB2's PerStat forms —
                // the "per Summoned Totem" lines times the totem count (CalcOffence.lua:1786).
                int summonedTotems = TotemsSummoned(activeGem, activeSelection.Level);
                decimal totemSpeedInc = deployedByTotem
                    ? bucket.TotemCastSpeedInc + bucket.TotemsSpellsCastSpeedPerActiveTotem * summonedTotems
                      + SupportTotemSpeedInc(catalog, group, false)
                    : 0;
                if (totemSpeedInc != 0)
                    breakdown.Add("Totem cast speed: +" + Round(totemSpeedInc, 0) + "% (totem pool " +
                        Dmg(bucket.TotemCastSpeedInc) + "%, per summoned totem " +
                        Dmg(bucket.TotemsSpellsCastSpeedPerActiveTotem) + "% x " + summonedTotems + " summoned, supports " +
                        Dmg(SupportTotemSpeedInc(catalog, group, false)) + "%)");
                // The spell counterpart of the attack line below: without it a rate that disagrees with
                // PoB2 cannot be taken apart (the totem wording above is only one of three buckets).
                breakdown.Add("Cast speed: +" + Dmg(speedInc) + "% gear/tree, +" + Dmg(totemSpeedInc) +
                    "% totem, +" + Dmg(speedFromSupportsInc) + "% supports/buffs, base " + Dmg(castTime / 1000m) + "s");
                rate = 1000m / castTime * (1 + (speedInc + totemSpeedInc + speedFromSupportsInc) / 100) * rateMore;
                // PoB2 reports a skill's cooldown (its own per-level table, e.g. Frost Bomb 6 s) but does
                // NOT cap the hit rate with it: CalcOffence's cooldown only feeds the Cooldown section and
                // a duration skill's uptime, while DPS uses the cast/attack speed. The value is therefore
                // reported, never applied — poe.ninja's own engine does cap it, which is why its Frost
                // Bomb entry reads 1/6 per second and PoB2's panel does not.
                if (skill.Cooldown is int cooldownMs and > 0)
                    rate = Math.Min(rate, 1000m / cooldownMs);
                else if (skillData?.Number("cooldown", effectiveLevel) is decimal tableCooldown and > 0)
                    breakdown.Add("Skill cooldown (PoB2 skill data): " + Dmg(tableCooldown) + " s (PoB2 does not cap the rate with it)");
                decimal skillCrit = (skill.Crit ?? 0) / 100m;
                if (skillCrit > 0)
                {
                    // PoB2: (skillCrit + sum of CritChance BASE) * (1 + inc/100) * more (CalcOffence.lua:3718).
                    critChance = Math.Min(critChanceCap, (skillCrit + bucket.CritChanceAdd) * (1 + (bucket.CritChanceInc + bucket.SpellCritInc + critChanceFromSupportsInc) / 100) * critChanceMore);
                    critBonus = (BaseCritDamageBonus + bucket.CritBonusAdd + bucket.SpellCritBonusAdd)
                        * (1 + (bucket.CritBonusInc + bucket.SpellCritBonusInc) / 100)
                        * (1 + bucket.CritBonusMorePct / 100) * critBonusMore;
                }
                var values = skill.LevelValues(effectiveLevel);
                if (values is not null)
                {
                    split = SpellSplit(values, bucket, scopedGeneral, scopedType, notes, gem, breakdown);
                    if (dealNoElemental || dealNoChaos)
                    {
                        var removed = new[] { dealNoElemental ? "Elemental" : null, dealNoChaos ? "Chaos" : null }
                            .Where(x => x is not null);
                        breakdown.Add("Support: deals no " + string.Join(" and no ", removed) + " damage (Brutality-class flag)");
                        split = ApplyDealNo(split, dealNoElemental, dealNoChaos);
                    }
                    // Archmage grants "Gain X% of Damage as Extra Lightning Damage per 100 maximum Mana"
                    // to non-channelling spells. It is a gain-as step, so it lands with the skill's own
                    // gain-as, before the more/less multipliers — the PoB2 ordering.
                    if (bucket.ArchmageGainAsLightningPer100Mana != 0 && !gem.Tags.Contains("channelling") && !dealNoElemental)
                    {
                        // PoB2 floors the per-100-Mana quotient (PerStat tag), so the gain is computed
                        // from whole 100-Mana stacks.
                        decimal stacks = Math.Floor(bucket.ManaFinal / 100m + 0.0001m);
                        decimal gain = bucket.ArchmageGainAsLightningPer100Mana * stacks;
                        if (gain > 0)
                        {
                            split = split with { Lightning = split.Lightning + split.Total * gain / 100m };
                            breakdown.Add("Archmage: gain " + Round(gain, 1) + "% of damage as extra Lightning");
                        }
                    }
                    // Arc's own static: "Consumes a Lightning Infusion to gain 6 chains and deal 200%
                    // more damage" (Data/Skills/act_int.lua:79 maps it to Damage MORE with
                    // Condition:ArcLightningInfused, which ConfigOptions.lua:212 sets from the
                    // "Lightning Infused?" config checkbox). It is applied only when the imported build
                    // actually enables that flag, exactly like PoB2's panel.
                    if (bucket.ArcLightningInfused && gem.Skill is { Statics.Count: > 0 } hostGem &&
                        hostGem.Statics.TryGetValue("arc_damage_+%_final_from_infusion_consumption", out decimal infusion))
                    {
                        damageMoreGeneral *= 1 + infusion / 100m;
                        breakdown.Add("Arc: consumes a Lightning Infusion for " + Round(infusion, 0) + "% more damage (config)");
                    }
                    split = ApplyDamageMore(split, damageMore, damageMoreGeneral);
                }
                else notes.Add("NoGemData");
            }
        }
        avgHit = split.Total;
        // Barrage (Modules/CalcOffence.lua:962-966): a supported Barrageable skill fires its projectiles in
        // sequence, and PoB2 folds that into ONE DPS multiplier — dpsMulti = (1 + repeats) x repeatDamage,
        // added as "DPS MORE dpsMulti", whose value it then reads as a percentage. That is why a 5-repeat
        // Barrage with a 42% repeat penalty shows up as x1.0348 in its own panel, and it is the 1.04 this
        // engine was missing on the Twister: our rate already matches, so the multiplier is a DPS factor.
        decimal barrageDpsFactor = 1m;
        if (isAttack && bucket.BarrageRepeats > 0)
        {
            decimal barrageMulti = (1 + bucket.BarrageRepeats) * bucket.BarrageRepeatDamageMore;
            barrageDpsFactor = 1 + barrageMulti / 100m;
            breakdown.Add("Barrage repeats: x" + Round(barrageDpsFactor, 4) + " (" + Dmg(bucket.BarrageRepeats) +
                " repeats, repeat damage x" + Dmg(bucket.BarrageRepeatDamageMore) + ")");
        }
        dps = avgHit * rate * (1 + critChance / 100 * critBonus / 100) * barrageDpsFactor;
        bucket.Extras["Pool:BarrageRepeats"] = bucket.BarrageRepeats;
        // PoB2's "effective" mode (its Calcs panel and its exported TotalDPS): what the enemy actually takes.
        decimal[] effectiveFactors = EffectiveDamageFactors(bucket, supportSink.InvertElementalResistChance, split);
        var effectiveSplit = new DamageSplit(split.Physical * effectiveFactors[0], split.Fire * effectiveFactors[1],
            split.Cold * effectiveFactors[2], split.Lightning * effectiveFactors[3], split.Chaos * effectiveFactors[4]);
        decimal effectiveAvgHit = effectiveSplit.Total;
        decimal effectiveDps = effectiveAvgHit * rate * (1 + critChance / 100 * critBonus / 100) * barrageDpsFactor;
        if (effectiveFactors.Any(f => f != 1m))
            breakdown.Add("Effective DPS mod (enemy): physical x" + Dmg(effectiveFactors[0]) + ", fire x" + Dmg(effectiveFactors[1]) +
                ", cold x" + Dmg(effectiveFactors[2]) + ", lightning x" + Dmg(effectiveFactors[3]) + ", chaos x" + Dmg(effectiveFactors[4]) +
                " -> hit " + Dmg(effectiveAvgHit) + " (raw " + Dmg(avgHit) + ")" +
                (supportSink.InvertElementalResistChance > 0 ? ", elemental resistance inverted (Rakiata's Flow)" : ""));
        if (rateMore != 1m) breakdown.Add("More (rate): x" + Dmg(rateMore));
        if (critChanceMore != 1m) breakdown.Add("More (crit chance): x" + Dmg(critChanceMore));
        if (critBonusMore != 1m) breakdown.Add("More (crit bonus): x" + Dmg(critBonusMore));
        if (damageMoreGeneral != 1m || damageMore.Any(m => m != 1m)) breakdown.Add("More (damage supports): x" + Dmg(damageMoreGeneral * damageMore.Max()));
        // Per-group diagnostics: the individual factors, so a multiplier can be traced to its source.
        bucket.Extras["Group:" + group.Name + ":moreDamage"] = damageMoreGeneral;
        bucket.Extras["Group:" + group.Name + ":moreTypes"] = damageMore.Max();
        if (critChance > 0) breakdown.Add("Crit: " + Round(critChance, 2) + "% chance x +" + Round(critBonus, 0) + "% bonus");
        // Accuracy only affects attacks; spells always hit (PoB hitChance for spells is 100%).
        decimal skillHitChance = isAttack ? (playerHitChance ?? 100m) : 100m;
        if (critChance > 0 && skillHitChance > 0)
        {
            // Effective crit chance of a landed hit (PoB "Crit Chance (Effective)").
            effectiveCrit = critChance * skillHitChance / 100m;
            breakdown.Add("Crit (effective): " + Round(effectiveCrit.Value, 2) + "% (" + Dmg(skillHitChance) + "% hit chance)");
        }

        // Damaging ailments (Ignite/Poison/Bleed) from the hit, ported from PoB2 CalcOffence.lua
        // (calcDamagingAilmentOutputs). Chances come from gem statics/levels and gear; a zero
        // chance keeps the ailment out of the breakdown entirely (honest v1 contract).
        var ailments = new List<AilmentDotResult>();
        decimal? totalDotDps = null;
        var gemStatics = gem.Skill?.Statics;
        var levelValues = skill?.LevelValues(effectiveLevel);
        void AddAilment(string ailment, decimal sourceDamage, decimal chanceBase, decimal chanceMoreBuckets, string finalId)
        {
            if (sourceDamage <= 0 || chanceBase <= 0) return;
            decimal chance = chanceBase * (1 + (chanceMoreBuckets + GemStat(finalId, gemStatics, levelValues)) / 100m);
            if (chance <= 0) return;
            string dotType = AilmentDotCalculator.DotTypeOf(ailment).ToLowerInvariant();
            int dotIndex = Array.IndexOf(TypeWords, dotType);
            decimal ailmentMore = damageMoreGeneral * (dotIndex >= 0 ? damageMore[dotIndex] : 1m);
            decimal ailmentInc = AilmentDotCalculator.IncreasedFor(ailment,
                IncFor(dotType, isAttack, bucket) + scopedGeneral + (dotIndex >= 0 ? scopedType[dotIndex] : 0), bucket);
            var result = AilmentDotCalculator.Evaluate(ailment, sourceDamage, sourceDamage * (1 + critBonus / 100m),
                critChance, chance, chance, ailmentInc, ailmentMore,
                hitsPerSecond: rate, hitChancePercent: skillHitChance);
            if (result is null) return;
            ailments.Add(result);
            breakdown.AddRange(result.Breakdown);
        }
        AddAilment("Ignite", split.Fire, bucket.IgniteChancePct + GemStat("base_chance_to_ignite_%", gemStatics, levelValues),
            bucket.IgniteChanceMorePct, "active_skill_ignite_chance_+%_final");
        AddAilment("Bleed", split.Physical,
            bucket.BleedChancePct + GemStat("base_chance_to_inflict_bleeding_%", gemStatics, levelValues) + GemStat("base_chance_to_bleed_%", gemStatics, levelValues),
            bucket.BleedChanceMorePct, "active_skill_bleeding_chance_+%_final");
        AddAilment("Poison", split.Physical + split.Chaos,
            bucket.PoisonChancePct + GemStat("base_chance_to_poison_on_hit_%", gemStatics, levelValues) + GemStat("base_chance_to_poison_%", gemStatics, levelValues),
            bucket.PoisonChanceMorePct, "active_skill_poison_chance_+%_final");
        if (ailments.Count > 0)
        {
            totalDotDps = ailments.Sum(a => a.SustainedDamagePerSecond);
            breakdown.Add("DoT total: " + Dmg(totalDotDps.Value) + "/s (from " + ailments.Count + " ailment(s))");
        }

        breakdown.Add("Average hit: " + Dmg(avgHit) + " (" + SplitSummary(split) + ")");
        breakdown.Add("Rate: " + Round(rate, 2) + "/s");
        breakdown.Add("DPS: " + Dmg(dps));
        if (effectiveDps != dps) breakdown.Add("Effective DPS (PoB2 mode): " + Dmg(effectiveDps));
        return Record(dps, avgHit, split, rate, critChance, critBonus, effectiveCrit, manaCost, isAttack, notes, breakdown, ailments, totalDotDps, group, gem, setMatches, levelFromItems, effectiveLevel, effectiveQuality, effectiveDps);
    }

    private static decimal Round(decimal value, int digits) => decimal.Round(value, digits, MidpointRounding.AwayFromZero);

    private static string Dmg(decimal value) => Round(value, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string SplitSummary(DamageSplit split)
    {
        var parts = new List<string>();
        if (split.Physical != 0) parts.Add("physical " + Dmg(split.Physical));
        if (split.Fire != 0) parts.Add("fire " + Dmg(split.Fire));
        if (split.Cold != 0) parts.Add("cold " + Dmg(split.Cold));
        if (split.Lightning != 0) parts.Add("lightning " + Dmg(split.Lightning));
        if (split.Chaos != 0) parts.Add("chaos " + Dmg(split.Chaos));
        return string.Join(", ", parts);
    }

    private static void AddIncreasedLine(List<string> breakdown, string type, decimal incPercent)
    {
        if (incPercent == 0) return;
        breakdown.Add("Increased (" + type + "): " + (incPercent > 0 ? "+" : "") + Round(incPercent, 0) + "%");
    }

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
    private static DamageSplit ConvertDamage(DamageSplit split, Gem gem, List<string> notes, List<string> breakdown)
    {
        var statics = gem.Skill?.Statics;
        if (statics is null || statics.Count == 0 || split.Total == 0) return split;
        bool applied = false;
        foreach (var sourceType in TypeWords)
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
            if (si < 0 || di < 0 || si == di || TypeWords[si] != sourceType) continue;
            decimal amount = SplitAt(split, si) * Math.Clamp(value, 0, 100) / 100m;
            split = AddType(SetType(split, si, SplitAt(split, si) - amount), TypeWords[di], amount);
            breakdown.Add("Converted: " + Round(value, 0) + "% " + src + " into " + dst);
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
            breakdown.Add("Gain as extra (gem): " + Round(value, 0) + "% " + src + " added as " + dst);
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

    /// <summary>Numeric ids from PoB2's global skill-stat table (SkillStatMap.lua) that the model
    /// implements. Applying a global numeric entry is only safe when no other path covers it — otherwise it
    /// double counts (Rapid Casting's base_cast_speed_+% is the counter-example, proven by the fixture's own
    /// Speed = 2.3727). Rakiata's Flow is such an id: nothing else reads it.</summary>
    private static bool GlobalNumericAllowed(string id) =>
        id == "treat_enemy_resistances_as_negated_on_elemental_damage_hit_%_chance";

    /// <summary>PoB2's effective damage multipliers per type (CalcOffence.lua:4117-4205): armour cuts the
    /// physical hit, the enemy's resistance cuts elemental ones, and Rakiata's Flow inverts that resistance.
    /// The values are factors (1 leaves the hit untouched). PoB2's own Calcs panel and its exported
    /// TotalDPS are this mode, so the effective numbers are reported next to the raw ones.</summary>
    private static decimal[] EffectiveDamageFactors(StatBucket bucket, decimal invertChance, DamageSplit split)
    {
        // PoB2's defaults: 50% elemental resistance, 0% chaos, and the monster armour table's level-82 value
        // (5,375) at the enemy preset's x1.5 scale — that reproduces both reference panels (the Huntress'
        // x0.285 physical and the Twister's x0.918). ArmourRatio = 10 (Data.lua:255).
        decimal armour = bucket.EnemyArmour ?? 5375m * 1.5m;
        decimal flatPhysicalDr = bucket.EnemyPhysicalDamageReduction ?? 0;
        decimal[] resists =
        [
            0,
            bucket.EnemyFireResist ?? 50m,
            bucket.EnemyColdResist ?? 50m,
            bucket.EnemyLightningResist ?? 50m,
            bucket.EnemyChaosResist ?? 0m
        ];
        decimal[] raw = [split.Physical, split.Fire, split.Cold, split.Lightning, split.Chaos];
        // "Enemies in your Presence Resist Elemental Damage based on their Lowest Resistance" (Sylvan's Effigy)
        // is PoB2's ElementalDamageUsesLowestResistance flag: every elemental type is priced against the LOWEST
        // of the three elemental resistances (CalcOffence.lua:4134-4152). The choice is made on the resist
        // values themselves, before Rakiata's inversion is folded in, exactly like PoB2's calcResistForType.
        if (bucket.EnemyElementalUsesLowestResistance)
        {
            decimal lowest = Math.Min(resists[1], Math.Min(resists[2], resists[3]));
            resists[1] = resists[2] = resists[3] = lowest;
        }
        var factors = new decimal[5];
        for (int i = 0; i < 5; i++)
        {
            decimal resist;
            if (i == 0)
                resist = flatPhysicalDr + (raw[i] <= 0 ? 0 : armour / (armour + raw[i] * 10m) * 100m);
            else
                resist = resists[i] * (1 - invertChance) - resists[i] * invertChance;
            factors[i] = Math.Max(0m, 1 - resist / 100m);
        }
        return factors;
    }

    private static SkillDpsInfo Record(decimal dps, decimal avgHit, DamageSplit split, decimal rate, decimal critChance, decimal critBonus,
        decimal? effectiveCrit, decimal? manaCost, bool isAttack, List<string> notes, List<string> breakdown,
        IReadOnlyList<AilmentDotResult> ailments, decimal? totalDotDps, SkillGroup group, Gem gem, bool setMatches, int levelFromItems,
        int effectiveLevel, int effectiveQuality, decimal effectiveDps = 0) =>
        new(group.Id, group.Name, gem.Id, gem.Name, isAttack, setMatches, notes.All(n => n is not ("NoGemData" or "NoWeapon")),
            Round(dps, 1), Round(effectiveDps, 1), Round(avgHit, 1), new(Round(split.Physical, 1), Round(split.Fire, 1), Round(split.Cold, 1), Round(split.Lightning, 1), Round(split.Chaos, 1)),
            Round(rate, 2), Round(critChance, 2), Round(critBonus, 0), effectiveCrit is decimal ec ? Round(ec, 2) : null, manaCost, notes.ToArray(),
            breakdown, ailments, totalDotDps, levelFromItems, effectiveLevel, effectiveQuality);

    private static decimal GemStat(string id, IReadOnlyDictionary<string, decimal>? statics, Dictionary<string, decimal>? levels)
    {
        decimal sum = 0;
        if (statics is not null && statics.TryGetValue(id, out var s)) sum += s;
        if (levels is not null && levels.TryGetValue(id, out var l)) sum += l;
        return sum;
    }

    /// <summary>Whether the gem is a direct damage source: attacks need a weapon; spells need base
    /// damage at the given level. Non-damage hosts (Spell Totem, auras, reserved skills) return false.</summary>
    /// <summary>Character-wide statics contributed by gems in ANY group. Only the statics whose own
    /// wording makes them global are read — currently Archmage's
    /// "Gain X% of Damage as Extra Lightning Damage per 100 maximum Mana to non-channelling spells".
    /// Everything else stays group-local, which is where PoB2 applies it.</summary>
    private static void CollectGlobalGemStatics(StatBucket bucket, GameCatalog catalog, SkillGroup[] groups)
    {
        foreach (var group in groups)
        {
            if (!group.Enabled) continue;
            foreach (var gemId in new[] { group.Active.GemId }.Concat(group.Supports.Select(s => s.GemId)))
            {
                var gem = catalog.Gems.GetValueOrDefault(gemId);
                if (gem?.Skill is null) continue;
                foreach (var (id, value) in gem.Skill.Statics)
                    if (id.StartsWith("archmage_", StringComparison.Ordinal))
                        StatInterpreter.Apply(bucket, id, value, null);
            }
        }
    }

    /// <summary>Level bonus a group's support gems grant the supported skill. PoB2 stores it as a
    /// static stat id such as "supported_lightning_skill_gem_level_+" (Lightning Mastery) or
    /// "fire_spell_skill_gem_level_+"; the scope words must be present in the skill's own tags.</summary>
    private static int SupportGemLevelBonus(GameCatalog catalog, SkillGroup group, Gem gem)
    {
        const string suffix = "_skill_gem_level_+";
        int bonus = 0;
        foreach (var selection in group.Supports)
        {
            var support = catalog.Gems.GetValueOrDefault(selection.GemId);
            if (support?.Skill is null) continue;
            foreach (var (id, value) in support.Skill.Statics)
            {
                if (value == 0 || !id.EndsWith(suffix, StringComparison.Ordinal)) continue;
                string scope = id.StartsWith("supported_", StringComparison.Ordinal) ? id["supported_".Length..] : id;
                scope = scope[..^suffix.Length];
                if (scope.Length == 0 || GemScopeMatches(scope, gem.Tags)) bonus += (int)value;
            }
        }
        return bonus;
    }

    /// <summary>PoB2's TotemsSummoned (CalcOffence.lua:1786: the Configuration override, else
    /// ActiveTotemLimit), which every "per Summoned Totem" line scales with (the PerStat forms in
    /// Data/ModCache.lua). PoB2's own data hardcodes the host's limit as an OVERRIDE with a "Should fix to
    /// take from active part of gem" note; the active part is what this reads — the totem host gem's level
    /// table carries <c>base_number_of_totems_allowed</c> (Spell Totem: 1 at levels 1-13, 2 at 14-22, 3
    /// from 23), and the exported reference build reports ActiveTotemLimit 2 for a level 14 Spell Totem.
    /// A host whose table lacks the field falls back to 1, since a totem host always summons its own.</summary>
    private static int TotemsSummoned(Gem? hostGem, int level)
    {
        if (hostGem?.Skill?.LevelValues(level) is { } values &&
            values.TryGetValue("base_number_of_totems_allowed", out var limit) && limit >= 1)
            return (int)limit;
        return 1;
    }

    /// <summary>Cast-speed increases the supports grant to the skill they deploy from a totem. Only
    /// <c>totem_skill_cast_speed_+%</c> / <c>totem_skill_attack_speed_+%</c> speed the deployed skill up;
    /// PoB2 maps <c>summon_totem_cast_speed_+%</c> to TotemPlacementSpeed instead (SkillStatMap.lua), so
    /// the wording that reads like "cast speed" must not be counted here — doing so inflated the
    /// reference build's rate by 100%.</summary>
    private static decimal SupportTotemSpeedInc(GameCatalog catalog, SkillGroup group, bool isAttack)
    {
        decimal total = 0;
        foreach (var selection in group.Supports)
        {
            var support = catalog.Gems.GetValueOrDefault(selection.GemId);
            if (support?.Skill is null) continue;
            foreach (var (id, value) in support.Skill.Statics)
                if (id == "totem_skill_cast_speed_+%" || (id == "totem_skill_attack_speed_+%" && isAttack))
                    total += value;
        }
        return total;
    }

    private static bool GemCanDealDamage(Gem gem, int level, ItemBase? mainBase)
    {
        if (gem.Tags.Contains("attack")) return mainBase?.Props.IsWeapon == true;
        var skill = gem.Skill;
        if (skill is null) return false;
        var values = skill.LevelValues(level);
        if (values is not null)
            foreach (var type in Types)
                if (values.TryGetValue("spell_minimum_base_" + type + "_damage", out var min) && min != 0) return true;
        return false;
    }

    private static DamageSplit AttackSplit(ItemBase weapon, ItemContext? local, StatBucket bucket, decimal scopedGeneral, decimal[] scopedType, Gem gem, List<string> notes, List<string> breakdown)
    {
        var wp = weapon.Props;
        // --- The weapon's own damage, exactly as PoB2 builds it (Classes/Item.lua:1909-1949) ---
        //   physical:            (base + local added physical) * (1 + local phys% / 100) * (1 + quality / 100)
        //   fire/cold/lightning: (base + local added)           * (1 + (local <type>% + local elemental%) / 100)
        //   chaos:               (base + local added)            (no local scaling at all)
        // Quality scales PHYSICAL only and as its own factor, so it is never folded into the local
        // percentage — the two multiply, they do not add.
        decimal quality = Math.Clamp(local?.WeaponQuality ?? 0, 0, 100);
        decimal localPhysMin = local?.AddedMin.GetValueOrDefault("physical") ?? 0;
        decimal localPhysMax = local?.AddedMax.GetValueOrDefault("physical") ?? 0;
        decimal phys = wp.PhysMin is decimal pmin && wp.PhysMax is decimal pmax
            ? (pmin + localPhysMin + pmax + localPhysMax) / 2 * (1 + (local?.PhysInc ?? 0) / 100) * (1 + quality / 100)
            : 0;
        breakdown.Add("Base (weapon): " + Dmg(phys) + " physical (" + Dmg(1 + (local?.PhysInc ?? 0) / 100) +
            " local damage x" + Dmg(1 + quality / 100) + " quality)");
        var split = new DamageSplit(phys, 0, 0, 0, 0);
        foreach (var type in Types)
        {
            if (type == "physical") continue;
            decimal added = ((local?.AddedMin.GetValueOrDefault(type) ?? 0) + (local?.AddedMax.GetValueOrDefault(type) ?? 0)) / 2;
            if (added == 0) continue;
            // Chaos is deliberately unscaled locally; the elemental types take their own local increase
            // plus the shared "Local Elemental Damage" one.
            decimal localInc = type == "chaos" ? 0 :
                (local?.LocalTypeInc.GetValueOrDefault(type) ?? 0) + (local?.LocalElemInc ?? 0);
            decimal scaled = added * (1 + localInc / 100);
            breakdown.Add("Weapon (local): " + Dmg(scaled) + " " + type +
                (localInc != 0 ? " (local +" + Dmg(localInc) + "%)" : ""));
            split = AddType(split, type, scaled);
        }
        // Global "Adds X to Y Damage" lines are added AFTER the weapon, and are scaled by the skill's
        // base multiplier together with it (PoB2 CalcOffence.lua:3941-3945).
        foreach (var type in Types)
        {
            decimal added = (bucket.AddedAttackMin.GetValueOrDefault(type) + bucket.AddedAttackMax.GetValueOrDefault(type)) / 2;
            // Flame Wall's ModFlag.Projectile added damage reaches projectile attacks only.
            if (gem.Tags.Contains("projectile"))
                added += (bucket.AddedAttackProjectileMin.GetValueOrDefault(type) + bucket.AddedAttackProjectileMax.GetValueOrDefault(type)) / 2;
            if (added != 0) breakdown.Add("Added (attack): " + Dmg(added) + " " + type);
            split = AddType(split, type, added);
        }
        // Game order: conversion and "gain as extra" expand base-stage damage first; only then do
        // increased/reduced modifiers scale each damage type by its final type.
        split = ConvertDamage(split, gem, notes, breakdown);
        split = ApplyGainAs(split, bucket.GainAs, breakdown);
        // A buff gated on ModFlag.Attack (Blazing Critical) reaches attacks only.
        split = ApplyGainAs(split, bucket.AttackOnlyGainAs, breakdown);
        split = ApplySourceGainAs(split, bucket.SourceGainAs, breakdown);
        // A character-wide "more" gated on ModFlag.Attack (a buff declared for attacks only) multiplies the whole
        // attack hit, exactly like PoB2's "Damage MORE" with that flag.
        if (bucket.AttackDamageMoreFactor != 1m)
        {
            split = new DamageSplit(split.Physical * bucket.AttackDamageMoreFactor, split.Fire * bucket.AttackDamageMoreFactor,
                split.Cold * bucket.AttackDamageMoreFactor, split.Lightning * bucket.AttackDamageMoreFactor,
                split.Chaos * bucket.AttackDamageMoreFactor);
            breakdown.Add("Buff attack damage (more): x" + Dmg(bucket.AttackDamageMoreFactor));
        }
        var result = new DamageSplit(
            split.Physical * (1 + (IncFor("physical", true, bucket) + scopedGeneral + scopedType[0]) / 100),
            split.Fire * (1 + (IncFor("fire", true, bucket) + scopedGeneral + scopedType[1]) / 100),
            split.Cold * (1 + (IncFor("cold", true, bucket) + scopedGeneral + scopedType[2]) / 100),
            split.Lightning * (1 + (IncFor("lightning", true, bucket) + scopedGeneral + scopedType[3]) / 100),
            split.Chaos * (1 + (IncFor("chaos", true, bucket) + scopedGeneral + scopedType[4]) / 100));
        AddIncreasedLine(breakdown, "physical", IncFor("physical", true, bucket) + scopedGeneral + scopedType[0]);
        AddIncreasedLine(breakdown, "fire", IncFor("fire", true, bucket) + scopedGeneral + scopedType[1]);
        AddIncreasedLine(breakdown, "cold", IncFor("cold", true, bucket) + scopedGeneral + scopedType[2]);
        AddIncreasedLine(breakdown, "lightning", IncFor("lightning", true, bucket) + scopedGeneral + scopedType[3]);
        AddIncreasedLine(breakdown, "chaos", IncFor("chaos", true, bucket) + scopedGeneral + scopedType[4]);
        return result;
    }

    private static DamageSplit SpellSplit(Dictionary<string, decimal> values, StatBucket bucket, decimal scopedGeneral, decimal[] scopedType, List<string> notes, Gem gem, List<string> breakdown)
    {
        var split = new DamageSplit(0, 0, 0, 0, 0);
        bool hasDamage = false;
        foreach (var t in Types)
        {
            decimal min = values.TryGetValue("spell_minimum_base_" + t + "_damage", out var v1) ? v1 : 0;
            decimal max = values.TryGetValue("spell_maximum_base_" + t + "_damage", out var v2) ? v2 : 0;
            if (min != 0 || max != 0) { split = AddType(split, t, (min + max) / 2); breakdown.Add("Base (gem): " + Dmg((min + max) / 2) + " " + t); hasDamage = true; }
        }
        if (!hasDamage) notes.Add("NoDamageStats");
        
        // Apply damage effectiveness - scales added spell damage. The pinned 0.5.5 export ships no
        // effectiveness statics, so the 100% fallback is the current contract.
        decimal dmgEffectPct = 100m;
        if (gem.Skill?.Statics is { Count: > 0 } statics)
        {
            if (statics.TryGetValue("damage_effectiveness", out var eff) ||
                statics.TryGetValue("spell_damage_effectiveness", out eff))
                dmgEffectPct = eff;
        }
        decimal dmgEffectMultiplier = dmgEffectPct / 100m;
        
        if (dmgEffectPct != 100m) breakdown.Add("Damage effectiveness: " + Round(dmgEffectPct, 0) + "%");

        foreach (var type in Types)
        {
            decimal added = (bucket.AddedSpellMin.GetValueOrDefault(type) + bucket.AddedSpellMax.GetValueOrDefault(type)) / 2 * dmgEffectMultiplier;
            // Flame Wall's ModFlag.Projectile added damage reaches projectile spells only (Flameblast, a
            // non-projectile spell, must not gain it).
            if (gem.Tags.Contains("projectile"))
                added += (bucket.AddedSpellProjectileMin.GetValueOrDefault(type) + bucket.AddedSpellProjectileMax.GetValueOrDefault(type)) / 2 * dmgEffectMultiplier;
            if (added != 0) breakdown.Add("Added (spell): " + Dmg(added) + " " + type);
            split = AddType(split, type, added);
        }
        split = ConvertDamage(split, gem, notes, breakdown);
        split = ApplyGainAs(split, bucket.GainAs, breakdown);
        split = ApplySourceGainAs(split, bucket.SourceGainAs, breakdown);
        var result = new DamageSplit(
            split.Physical * (1 + (IncFor("physical", false, bucket) + scopedGeneral + scopedType[0]) / 100),
            split.Fire * (1 + (IncFor("fire", false, bucket) + scopedGeneral + scopedType[1]) / 100),
            split.Cold * (1 + (IncFor("cold", false, bucket) + scopedGeneral + scopedType[2]) / 100),
            split.Lightning * (1 + (IncFor("lightning", false, bucket) + scopedGeneral + scopedType[3]) / 100),
            split.Chaos * (1 + (IncFor("chaos", false, bucket) + scopedGeneral + scopedType[4]) / 100));
        AddIncreasedLine(breakdown, "physical", IncFor("physical", false, bucket) + scopedGeneral + scopedType[0]);
        AddIncreasedLine(breakdown, "fire", IncFor("fire", false, bucket) + scopedGeneral + scopedType[1]);
        AddIncreasedLine(breakdown, "cold", IncFor("cold", false, bucket) + scopedGeneral + scopedType[2]);
        AddIncreasedLine(breakdown, "lightning", IncFor("lightning", false, bucket) + scopedGeneral + scopedType[3]);
        AddIncreasedLine(breakdown, "chaos", IncFor("chaos", false, bucket) + scopedGeneral + scopedType[4]);
        return result;
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

    /// <summary>Whether the group's blue support gems are the most numerous of its socketed supports. PoB2 checks
    /// RED first and then green, so a tie goes to the earlier colour (CalcSetup.lua:2155-2162).</summary>
    private static bool MostNumerousColourIsBlue(SkillGroup group, GameCatalog catalog)
    {
        int red = 0, green = 0, blue = 0;
        foreach (var selection in group.Supports)
            switch (catalog.Gems.GetValueOrDefault(selection.GemId)?.Color)
            {
                case "r": red++; break;
                case "g": green++; break;
                case "b": blue++; break;
            }
        if (red >= green && red >= blue) return false;
        if (green >= red && green >= blue) return false;
        return blue >= red && blue >= green;
    }

    private static DamageSplit ApplyGainAs(DamageSplit split, IReadOnlyDictionary<string, decimal> gainAs, List<string> breakdown)
    {
        if (gainAs.Count == 0 || split.Total == 0) return split;
        decimal baseTotal = split.Total;
        foreach (var (type, percent) in gainAs)
        {
            split = AddType(split, type, baseTotal * percent / 100m);
            breakdown.Add("Gain as extra (" + type + "): +" + Round(percent, 0) + "% of base damage");
        }
        return split;
    }

    /// <summary>"Gain X% of Y damage as Extra Z" applies to the named source damage type only
    /// (tree/jewel lines), not to the whole base damage pool like all-damage gain.</summary>
    private static DamageSplit ApplySourceGainAs(DamageSplit split,
        IReadOnlyDictionary<(string Source, string Destination), decimal> sourceGainAs, List<string> breakdown)
    {
        if (sourceGainAs.Count == 0 || split.Total == 0) return split;
        foreach (var entry in sourceGainAs)
        {
            int sourceIndex = Array.IndexOf(TypeWords, entry.Key.Source);
            if (sourceIndex < 0 || entry.Value == 0) continue;
            decimal amount = SplitAt(split, sourceIndex) * entry.Value / 100m;
            split = AddType(split, entry.Key.Destination, amount);
            breakdown.Add("Gain as extra (" + entry.Key.Source + " into " + entry.Key.Destination + "): +" +
                Round(entry.Value, 0) + "% of " + entry.Key.Source + " damage");
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

    /// <summary>Support flags "Deals no Elemental Damage" / "Deals no Chaos Damage" (Brutality) drop those
    /// damage types after the gain-as steps, matching PoB2's DealNo* flags on the final damage pool.</summary>
    private static DamageSplit ApplyDealNo(DamageSplit s, bool noElemental, bool noChaos) => s with
    {
        Fire = noElemental ? 0 : s.Fire,
        Cold = noElemental ? 0 : s.Cold,
        Lightning = noElemental ? 0 : s.Lightning,
        Chaos = noChaos ? 0 : s.Chaos
    };
}

