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
    decimal CritBonusPercent, decimal? EffectiveCritChancePercent, decimal? ManaCost, string[] NoteCodes, IReadOnlyList<string> Breakdown,
    IReadOnlyList<AilmentDotResult> Ailments, decimal? TotalDotDps, int LevelFromItems);

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
    decimal MoveSpeedPercent, decimal LifeRegenPerSecond, decimal EsRechargePerSecond, decimal? EsRechargeDelaySeconds,
    decimal? PhysicalReductionEstimate, IReadOnlyList<DefenceEhpEstimate> EhpEstimates,
    ExpectedAttackEhpEstimate? ExpectedAttackEhp, ExpectedSpellEhpEstimate? ExpectedSpellEhp, int EstimateMonsterLevel,
    IReadOnlyList<SkillDpsInfo> Skills,
    IReadOnlyDictionary<string, decimal> Extras, IReadOnlyDictionary<string, int> Unaccounted, int UnaccountedTotal);

/// <summary>
/// Independent v1 calculator. Sources: pinned RePoE 4.5.5.2 values (item bases, implicits, rolls, gem
/// per-level stats, tree lines via the pinned statmap). Per-level growth +12 life / +4 mana / +6 accuracy /
/// +3 evasion, attributes +2 life (Str) / +5 accuracy (Dex) / +2 mana (Int), armour DR = A/(A+12·hit)
/// capped at 90%, ES recharge base 12.5%/s with a 4s start delay estimate, player resistance = stage baseline + raw sources with a 75%
/// upper cap (raisable by maximum-resistance modifiers). Attack block maximum/cap, dodge caps and deflection chance
/// use the current PoB2 reference constants but remain target-patch verification items until backed by a
/// pinned PoB/data fixture. Armour ratio, growth constants and the exact target-patch resistance rules
/// remain verification items until backed by a pinned PoB/data fixture.
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
            var equipmentPlan = build.Equipment;
            int set = equipmentPlan.WeaponSet == 2 ? 2 : 1;
            foreach (var slot in alwaysSlots.Append("Main" + set).Append("Off" + set))
            {
                if (!equipmentPlan.Slots.TryGetValue(slot, out var itemId)) continue;
                var gear = equipmentPlan.Items.FirstOrDefault(i => i.Id == itemId);
                if (gear is null) continue;
                if (!catalog.Bases.TryGetValue(gear.BaseId, out var b))
                {
                    // Imported uniques have no pinned base identity. Their modifier text is interpreted
                    // through the deterministic UniqueTextParser so pools/resists/attributes contribute;
                    // the base armour/evasion/ES values themselves are not exported by the pinned source
                    // and therefore stay absent (honest: counted lines are exact stat ids only).
                    ApplyUniqueModText(bucket, gear);
                    continue;
                }
                if (slot == "Main" + set) mainHand = gear;
                var item = new ItemContext();
                StatInterpreter.ApplyAll(bucket, ImplicitValues(b), item);
                foreach (var roll in gear.Mods.Concat(gear.CorruptedMods))
                {
                    if (!catalog.Mods.TryGetValue(roll.Id, out var mod)) continue;
                    for (int i = 0; i < mod.Stats.Length && i < roll.Values.Length; i++)
                        StatInterpreter.Apply(bucket, mod.Stats[i].Id, roll.Values[i], item);
                }
                // Base defences scale with the item's quality (PoE2/PoB: +quality% of the base) and the
                // item's own local increases. When the pinned data supplies a base Ward it is added the
                // same way, so Eldritch Battery-style conversions see the quality-inflated pool too.
                bucket.ArmourFlat += (b.Props.Armour ?? 0) * (1 + (item.ArmourInc + gear.Quality) / 100);
                bucket.EvFlat += (b.Props.Evasion ?? 0) * (1 + (item.EvInc + gear.Quality) / 100);
                bucket.EsFlat += (b.Props.EnergyShield ?? 0) * (1 + (item.EsInc + gear.Quality) / 100);
                bucket.WardFlat += (b.Props.Ward ?? 0) * (1 + (item.WardInc + gear.Quality) / 100);
                bucket.MoveInc += b.Props.MovementSpeed ?? 0;
                if ((b.Props.Block ?? 0) > 0 || item.BlockInc != 0)
                    shieldBlock += (b.Props.Block ?? 0) * (1 + item.BlockInc / 100);
                bucket.AccFlat += item.AccuracyFlat;
                // Uniques that DO resolve to a pinned base still carry user text; fold it in too.
                ApplyUniqueModText(bucket, gear);
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
                // Unique jewels carry no jewel-pool affixes; their own text is interpreted instead.
                ApplyUniqueModText(bucket, jewel);
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
        if (bucket.ChaosInoculation) life = 1;
        decimal baseMana = (catalog?.Vitals.BaseMana ?? 30) + ManaPerLevel * (level - 1) + ManaPerIntelligence * inte;
        decimal mana = (baseMana + bucket.Mana) * (1 + bucket.ManaInc / 100);
        decimal accuracy = (AccuracyPerLevel * (level - 1) + AccuracyPerDexterity * dex + bucket.AccFlat) * (1 + bucket.AccInc / 100);
        decimal evasion = (EvasionPerLevel * (level - 1) + bucket.EvFlat) * (1 + bucket.EvInc / 100);
        decimal armour = bucket.ArmourFlat * (1 + bucket.ArmourInc / 100);
        decimal es = bucket.EsFlat * (1 + bucket.EsInc / 100);
        decimal convertedEs = es * Math.Clamp(bucket.EnergyShieldToManaPercent, 0, 100) / 100m;
        es -= convertedEs;
        // Eldritch Battery-style conversion joins the maximum Mana after its increased modifiers have
        // been applied — the PoB resource order — so converted ES lands in the final pool.
        mana += convertedEs;
        // "spell damage per 100 maximum Mana" must read the FINAL maximum Mana, including conversion.
        if (bucket.SpellDamagePer100Mana != 0) bucket.SpellDamageInc += bucket.SpellDamagePer100Mana * mana / 100m;
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
            R(moveSpeed), R(ResourceRecovery.LifeRegenerationPerSecond(bucket.LifeRegenPerMin, bucket.LifeRegenInc)
                + life * Math.Max(0, bucket.LifeRegenPercentPerSecond) / 100m, 2), R(esRechargePerSecond, 2),
            esRechargeDelay is decimal delay ? R(delay, 2) : null,
            reduction, ehpEstimates, expectedAttackEhp, expectedSpellEhp, level, skills, bucket.Extras, bucket.Unaccounted, bucket.UnaccountedTotal);

        static decimal R(decimal v, int digits = 0) => Math.Round(v, digits, MidpointRounding.AwayFromZero);
        // "starter" = campaign (resistances start at 0); "endgame" = each campaign act took -10%,
        // i.e. -40% to Fire/Cold/Lightning after the campaign. Chaos is not penalised by acts.
        static decimal ResBaseline(string stage) => stage == "endgame" ? -40m : 0;
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
    };

    /// <summary>Parametric fallback for recurring tree-line shapes that repeat with different
    /// numbers across the tree. Only the exact shapes stored in the pinned export are matched;
    /// anything else stays unreported rather than guessed.</summary>
    private static Dictionary<string, decimal>? PatternFallbackStats(string line)
    {
        // "Regenerate 0.5% of maximum Life per second"
        const string regenSuffix = "% of maximum Life per second";
        if (line.StartsWith("Regenerate ", StringComparison.Ordinal) && line.EndsWith(regenSuffix, StringComparison.Ordinal))
        {
            var number = line["Regenerate ".Length .. (line.Length - regenSuffix.Length - 1)];
            if (IsDecimalNumber(number)) return new() { ["life_regeneration_percent_per_second"] = decimal.Parse(number) };
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
                return new() { ["non_skill_base_" + source + "_damage_%_to_gain_as_" + destination] = decimal.Parse(parts[0]) };
            }
            return null;
        }
        return null;
    }

    private static bool IsDecimalNumber(string text)
    {
        try { decimal.Parse(text); return true; }
        catch (FormatException) { return false; }
    }

    private static void ApplyTreeStats(StatBucket bucket, GameStatMap statMap, TreeCatalog graph,
        PassiveTreePlan plan, IEnumerable<int> nodeIds)
    {
        foreach (int id in nodeIds.Distinct())
        {
            if (!graph.Nodes.ContainsKey(id)) continue;
            foreach (var line in graph.Describe(id, plan).Stats)
            {
                if (statMap.Lines.TryGetValue(line, out var stats)) { StatInterpreter.ApplyAll(bucket, stats); continue; }
                Dictionary<string, decimal>? fallback = TreeStatFallbacks.TryGetValue(line, out var fb) ? fb : PatternFallbackStats(line);
                if (fallback is not null) StatInterpreter.ApplyAll(bucket, fallback);
                else bucket.Note("tree: " + line);
            }
        }
    }

    /// <summary>Applies the deterministic unique-text parser to an imported unique item or jewel.
    /// Returns the number of recognised stat lines; 0 keeps the item honestly uninterpreted.</summary>
    private static int ApplyUniqueModText(StatBucket bucket, GearItem gear)
    {
        if (!string.Equals(gear.Rarity, "unique", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(gear.Notes)) return 0;
        int applied = 0;
        foreach (var (id, value) in UniqueTextParser.ParseMods(gear.Notes))
        {
            StatInterpreter.Apply(bucket, id, value, null);
            applied++;
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

    /// <summary>Collects only the weapon-LOCAL contributions of a weapon (they scale that weapon only).</summary>
    private static ItemContext WeaponContext(GameCatalog catalog, GearItem weapon)
    {
        // Weapon quality adds to the local physical damage increase (PoE2/PoB local quality convention).
        var item = new ItemContext { WeaponQuality = Math.Clamp(weapon.Quality, 0, 20) };
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
        int levelFromItems = 0;
        foreach (var (scope, value) in bucket.GemLevels) if (GemScopeMatches(scope, gem.Tags)) levelFromItems += (int)value;
        foreach (var (scope, value) in mainLocal?.GemLevels ?? new List<(string, decimal)>()) if (GemScopeMatches(scope, gem.Tags)) levelFromItems += (int)value;
        int effectiveLevel = Math.Clamp(activeSelection.Level + levelFromItems, 1, 40);
        var notes = new List<string>();
        var breakdown = new List<string>();
        if (gem != activeGem) breakdown.Add("Hosted: " + gem.Name + " (deployed by " + activeGem.Name + ")");
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
            return Record(0, 0, new DamageSplit(0, 0, 0, 0, 0), 0, 0, 0, null, null, isAttack, notes, breakdown, [], null, group, gem, setMatches, levelFromItems);
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
        // Gem quality grants +1% increased damage per 1% quality (game convention; the pinned source
        // carries quality only where the exported build lists it).
        decimal qualityIncreased = activeSelection.Quality + bucket.AllGemQuality;
        if (qualityIncreased > 0)
        {
            scopedGeneral += qualityIncreased;
            breakdown.Add("Quality (+" + Round(qualityIncreased, 0) + "%): increased damage");
        }

        if (skill is null)
        {
            notes.Add("NoGemData");
        }
        else
        {
            manaCost = skill.LevelCosts(effectiveLevel)?.TryGetValue("Mana", out var mc) == true ? mc : null;
            if (manaCost is decimal baseManaCost && bucket.ManaCostFinalPct != 0)
            {
                decimal factor = 1 + bucket.ManaCostFinalPct / 100m;
                manaCost = baseManaCost * factor;
                breakdown.Add("More (mana cost): x" + Dmg(factor));
            }
            decimal speedInc = bucket.CastSpeedInc + bucket.SkillSpeedInc;
            if (isAttack)
            {
                if (mainBase?.Props.AttackTime is not int attackTime) { notes.Add("NoWeapon"); return Record(0, 0, split, 0, 0, 0, effectiveCrit, manaCost, isAttack, notes, breakdown, [], null, group, gem, setMatches, levelFromItems); }
                rate = 1000m / attackTime * (1 + (bucket.AttackSpeedInc + speedInc + (mainLocal?.AttackSpeedInc ?? 0)) / 100) * rateMore;
                decimal weaponCrit = (mainBase.Props.CritChance ?? 0) / 100m + (mainLocal?.CritChanceAdd ?? 0);
                critChance = Math.Min(100, weaponCrit * (1 + (bucket.CritChanceInc + bucket.AttackCritInc) / 100) * critChanceMore);
                critBonus = (BaseCritDamageBonus + bucket.CritBonusAdd + bucket.AttackCritBonusAdd + (mainLocal?.CritBonusAdd ?? 0)) * critBonusMore;
                split = AttackSplit(mainBase, mainLocal, bucket, scopedGeneral, scopedType, gem, notes, breakdown);
                split = ApplyDamageMore(split, damageMore, damageMoreGeneral);
            }
            else
            {
                decimal castTime = Math.Max(1, skill.CastTime ?? 1000);
                rate = 1000m / castTime * (1 + speedInc / 100) * rateMore;
                if (skill.Cooldown is int cooldown && cooldown > 0)
                    rate = Math.Min(rate, 1000m / cooldown);
                decimal skillCrit = (skill.Crit ?? 0) / 100m;
                if (skillCrit > 0)
                {
                    critChance = Math.Min(100, skillCrit * (1 + (bucket.CritChanceInc + bucket.SpellCritInc) / 100) * critChanceMore);
                    critBonus = (BaseCritDamageBonus + bucket.CritBonusAdd + bucket.SpellCritBonusAdd) * critBonusMore;
                }
                var values = skill.LevelValues(effectiveLevel);
                if (values is not null)
                {
                    split = SpellSplit(values, bucket, scopedGeneral, scopedType, notes, gem, breakdown);
                    split = ApplyDamageMore(split, damageMore, damageMoreGeneral);
                }
                else notes.Add("NoGemData");
            }
        }
        avgHit = split.Total;
        dps = avgHit * rate * (1 + critChance / 100 * critBonus / 100);
        if (rateMore != 1m) breakdown.Add("More (rate): x" + Dmg(rateMore));
        if (critChanceMore != 1m) breakdown.Add("More (crit chance): x" + Dmg(critChanceMore));
        if (critBonusMore != 1m) breakdown.Add("More (crit bonus): x" + Dmg(critBonusMore));
        if (damageMoreGeneral != 1m || damageMore.Any(m => m != 1m)) breakdown.Add("More (damage supports): x" + Dmg(damageMoreGeneral * damageMore.Max()));
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
        return Record(dps, avgHit, split, rate, critChance, critBonus, effectiveCrit, manaCost, isAttack, notes, breakdown, ailments, totalDotDps, group, gem, setMatches, levelFromItems);
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

    private static SkillDpsInfo Record(decimal dps, decimal avgHit, DamageSplit split, decimal rate, decimal critChance, decimal critBonus,
        decimal? effectiveCrit, decimal? manaCost, bool isAttack, List<string> notes, List<string> breakdown,
        IReadOnlyList<AilmentDotResult> ailments, decimal? totalDotDps, SkillGroup group, Gem gem, bool setMatches, int levelFromItems) =>
        new(group.Id, group.Name, gem.Id, gem.Name, isAttack, setMatches, notes.All(n => n is not ("NoGemData" or "NoWeapon")),
            Round(dps, 1), Round(avgHit, 1), new(Round(split.Physical, 1), Round(split.Fire, 1), Round(split.Cold, 1), Round(split.Lightning, 1), Round(split.Chaos, 1)),
            Round(rate, 2), Round(critChance, 2), Round(critBonus, 0), effectiveCrit is decimal ec ? Round(ec, 2) : null, manaCost, notes.ToArray(),
            breakdown, ailments, totalDotDps, levelFromItems);

    private static decimal GemStat(string id, IReadOnlyDictionary<string, decimal>? statics, Dictionary<string, decimal>? levels)
    {
        decimal sum = 0;
        if (statics is not null && statics.TryGetValue(id, out var s)) sum += s;
        if (levels is not null && levels.TryGetValue(id, out var l)) sum += l;
        return sum;
    }

    /// <summary>Whether the gem is a direct damage source: attacks need a weapon; spells need base
    /// damage at the given level. Non-damage hosts (Spell Totem, auras, reserved skills) return false.</summary>
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
        // Base weapon physical damage scaled by local increases and the weapon's quality.
        decimal phys = wp.PhysMin is decimal pmin && wp.PhysMax is decimal pmax ? (pmin + pmax) / 2 * (1 + ((local?.PhysInc ?? 0) + (local?.WeaponQuality ?? 0)) / 100) : 0;
        breakdown.Add("Base (weapon): " + Dmg(phys) + " physical");
        var split = new DamageSplit(phys, 0, 0, 0, 0);
        split = AddLocalAdded(split, local);
        foreach (var type in Types)
        {
            decimal added = (bucket.AddedAttackMin.GetValueOrDefault(type) + bucket.AddedAttackMax.GetValueOrDefault(type)) / 2;
            if (added != 0) breakdown.Add("Added (attack): " + Dmg(added) + " " + type);
            split = AddType(split, type, added);
        }
        // Game order: conversion and "gain as extra" expand base-stage damage first; only then do
        // increased/reduced modifiers scale each damage type by its final type.
        split = ConvertDamage(split, gem, notes, breakdown);
        split = ApplyGainAs(split, bucket.GainAs, breakdown);
        split = ApplySourceGainAs(split, bucket.SourceGainAs, breakdown);
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
}
