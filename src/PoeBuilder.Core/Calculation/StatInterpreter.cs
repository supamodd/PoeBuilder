using PoeBuilder.Core.Equipment;

namespace PoeBuilder.Core.Calculation;

/// <summary>Condition under which a stat line applies. The flags are PoB2 config conditions
/// (conditionMoving, conditionCritRecently, conditionBeenHitRecently) plus the two life states
/// PoB2 derives from the unreserved Life fraction (Low Life below 35% of maximum).</summary>
[Flags]
public enum StatCondition
{
    None = 0,
    Moving = 1,
    Stationary = 2,
    FullLife = 4,
    LowLife = 8,
    CritRecently = 16,
    BeenHitRecently = 32,
    EnemyIgnited = 64,
    EnemyChilled = 128,
    EnemyShocked = 256
}

/// <summary>Aggregated character stats. Only buckets with a verified game formula are consumed by
/// the calculator; everything else is shown as "catalogued but not in formulas v1" or unaccounted.</summary>
public sealed class StatBucket
{
    public decimal Life, LifeInc, Mana, ManaInc, EsFlat, EsInc, WardFlat, WardInc, Spirit, SpiritInc;
    public decimal EnergyShieldToManaPercent, DamageTakenFromManaPercent;
    /// <summary>"N% reduced Mana Cost" (<c>base_mana_cost_-%</c>): PoB2 maps it to
    /// mod("ManaCost","INC") with mult = -1 and divides the cost by (1 + value/100), so it is stored
    /// as the signed increase and consumed as a divisor.</summary>
    public decimal ManaCostInc;
    /// <summary>Condition-scoped stat lines (moving, on Low/Full Life, crit recently...). They are
    /// resolved after the pools are known, because Low Life depends on the unreserved Life fraction,
    /// and the flags come from the imported PoB2 config (&lt;Config&gt; inputs).</summary>
    public readonly List<(string Id, decimal Value, StatCondition When)> Conditionals = new();
    public bool ChaosInoculation;
    public bool ArmourAppliesToElemental;
    public decimal ArmourFlat, ArmourInc, EvFlat, EvInc, AccFlat, AccInc;
    public decimal FireRes, ColdRes, LightRes, ChaosRes, FireMax, ColdMax, LightMax, ChaosMax;
    public decimal Str, Dex, Int;
    public decimal MoveInc, AttackSpeedInc, CastSpeedInc, SkillSpeedInc;
    public decimal CritChanceInc, AttackCritInc, SpellCritInc, CritChanceAdd;
    public decimal CritBonusAdd, AttackCritBonusAdd, SpellCritBonusAdd;
    /// <summary>Increased Critical Damage Bonus (PoB2 CritMultiplier INC). The flat bonus is
    /// multiplied by (1 + inc/100) before the more/less multiplier, exactly like PoB2.</summary>
    public decimal CritBonusInc, SpellCritBonusInc;
    /// <summary>Conditional "more/less Critical Damage Bonus" (percent, signed): PoB2's
    /// CritMultiplier MORE mods, e.g. Pain Attunement's "30% more Critical Damage Bonus when on Low
    /// Life". Applied as (1 + value/100) after the increased step.</summary>
    public decimal CritBonusMorePct;
    /// <summary>Conditional crit-bonus lines found while reading the tree. They are resolved after the
    /// pools are known, because the Low/Full Life condition depends on the unreserved Life fraction.</summary>
    public readonly List<(decimal Percent, bool RequiresLowLife, bool RequiresFullLife)> ConditionalCritBonus = new();
    // Damaging ailment (Ignite/Poison/Bleed) buckets. Chances are percentages; the *_MorePct
    // buckets hold "final" (more) multipliers from skills/keystones. Inc buckets are increased
    // damage for damage-over-time / burning / poison / bleeding respectively.
    public decimal IgniteChancePct, PoisonChancePct, BleedChancePct;
    public decimal IgniteChanceMorePct, PoisonChanceMorePct, BleedChanceMorePct;
    public decimal DotInc, BurningInc, PoisonInc, BleedInc, AilmentDurationInc;
    // Gem quality granted by tree/jewels ("all_skill_gem_quality_+") and mana-scaled spell damage.
    public decimal AllGemQuality, SpellDamagePer100Mana;
    /// <summary>Spell Critical Hit Chance per 100 maximum Mana (Rathpith Globe and friends): the
    /// final maximum Mana is divided by 100 and multiplied by this value, like PoB2's PerStat tag.</summary>
    public decimal SpellCritChancePer100Mana;
    /// <summary>"Non-Channelling Spells cost an additional X% of your maximum Life": reported as the
    /// life component of a skill's cost (PoB2 LifeCostBase with a PercentStat tag).</summary>
    public decimal LifeCostPercentOfMaxLife;
    /// <summary>Final maximum Mana, published after the pools are computed. Per-100-Mana scalers
    /// (Rathpith Globe, Archmage) read the FINAL pool, exactly like PoB2's PerStat tag.</summary>
    public decimal ManaFinal;
    /// <summary>PoB2's <c>CanUseBondedModifiers</c> condition. Every "Bonded: …" rune/idol line is
    /// tagged with it (Modules/ModParser.lua), so item text cannot be read before the flag is known:
    /// only "Gain the benefits of Bonded modifiers on Runes and Idols" sets it.</summary>
    public bool CanUseBondedModifiers;
    /// <summary>The PoB2 conditions this build can evaluate (config flags + the life state), used when a
    /// skill statMap entry is conditional.</summary>
    public ModConditions Conditions = ModConditions.None;
    /// <summary>Enemy values for PoB2's "effective" mode (its Calcs panel and exported TotalDPS price the
    /// damage the enemy actually takes). Filled in from the build config; null means PoB2's own default.</summary>
    public decimal? EnemyFireResist, EnemyColdResist, EnemyLightningResist, EnemyChaosResist;
    public decimal? EnemyArmour, EnemyLevel, EnemyPhysicalDamageReduction;
    /// <summary>PoB2's <c>ArcLightningInfused</c> condition ("Lightning Infused?" checkbox,
    /// ConfigOptions.lua:212). Only when the imported config sets it does Arc's
    /// <c>arc_damage_+%_final_from_infusion_consumption</c> (+200% more) apply, exactly like PoB2.</summary>
    public bool ArcLightningInfused;
    /// <summary>Archmage: "Gain X% of Damage as Extra Lightning Damage ... per 100 maximum Mana,
    /// granted to non-channelling spells" (PoB2 static id
    /// archmage_all_damage_%_to_gain_as_lightning_to_grant_to_non_channelling_spells_per_100_max_mana).
    /// Stored as the per-100-Mana rate; the skill's damage gain is rate × ManaFinal / 100.</summary>
    public decimal ArchmageGainAsLightningPer100Mana;
    /// <summary>Cast-speed increases that only apply when the skill is deployed by a totem
    /// (PoB2 <c>totem_skill_cast_speed_+%</c> / <c>totem_skill_attack_speed_+%</c>, both INC Speed with the
    /// Totem keyword).</summary>
    public decimal TotemCastSpeedInc;
    /// <summary>Totem PLACEMENT speed (<c>summon_totem_cast_speed_+%</c>, PoB2 maps it to
    /// TotemPlacementSpeed). It speeds up deploying the totem, never the skill the totem casts, so it is
    /// recorded for honesty but not consumed by any rate formula.</summary>
    public decimal TotemPlacementSpeedInc;
    // Tree keystones resolved by the resident fallback mapper (see CharacterCalculator.TreeStatFallbacks).
    // LifeRegenPercentPerSecond scales maximum Life; ManaCostFinalPct is a final (more/less) mana-cost
    // adjustment in percent; SpiritReservedFlat is flat Spirit reserved by tree mechanics (e.g. totems).
    public decimal LifeRegenPercentPerSecond, ManaCostFinalPct, SpiritReservedFlat;
    // Source-specific "gain X as extra Y": gain is a percentage of the named source damage type only
    // (e.g. tree line "Gain 6% of Lightning damage as Extra Cold damage"), unlike the all-damage GainAs.
    public readonly Dictionary<(string Source, string Destination), decimal> SourceGainAs = new();
    public decimal DamageInc, PhysInc, FireInc, ColdInc, LightInc, ChaosInc, ElemInc, ElemAttackInc, AttackDamageInc, SpellDamageInc;
    public decimal LifeRegenPerMin, LifeRegenInc, ManaRegenInc, EsRechargeInc, EsRechargeFasterInc;
    public decimal DeflectPctOfEvasion, DeflectPctOfArmour, DeflectInc, DeflectEffectAdd, LifePerDexRate;
    public decimal BlockInc, BlockAdditional, BlockMaxAdd;
    public decimal SpellBlockBase, SpellBlockAdditional, SpellBlockMaxAdd;
    public decimal AttackDodgeChance, SpellDodgeChance;
    public decimal SpellSuppressionChance, SpellSuppressionEffectAdd;
    public decimal? BlockMaxOverride, SpellBlockMaxOverride;
    // Gem levels granted by tree/items: (scope, value); scope words joined with '+' (e.g. "fire+spell").
    public readonly List<(string Scope, decimal Value)> GemLevels = new();
    // Skill-scoped damage increases: (scope words, value); words must all appear in the gem's tags.
    public readonly List<(string[] Words, decimal Value)> ScopedDamage = new();
    /// <summary>Weapon-class-scoped crit and attack-speed mods — PoB2's <c>&lt;class&gt;_critical_strike_multiplier_+</c>,
    /// <c>&lt;class&gt;_critical_strike_chance_+%</c> and <c>&lt;class&gt;_attack_speed_+%</c> family (e.g. Javelin's
    /// "40% increased Critical Damage Bonus with Spears"). They only count while that class is in the main
    /// hand, so the calculator resolves them per group exactly like <see cref="ScopedDamage"/>.</summary>
    public readonly List<(string[] Words, decimal Value)> ScopedCritChanceInc = new();
    public readonly List<(string[] Words, decimal Value)> ScopedCritBonusAdd = new();
    public readonly List<(string[] Words, decimal Value)> ScopedAttackSpeedInc = new();
    public readonly Dictionary<string, decimal> AddedAttackMin = new(), AddedAttackMax = new();
    public readonly Dictionary<string, decimal> AddedSpellMin = new(), AddedSpellMax = new();
    public readonly Dictionary<string, decimal> GainAs = new();
    public readonly Dictionary<(string Source, string Destination), decimal> DamageTakenAs = new();
    public readonly SortedDictionary<string, decimal> Extras = new();
    public readonly SortedDictionary<string, int> Unaccounted = new();
    public int UnaccountedTotal;

    private static readonly string[] DamageTypes = ["physical", "fire", "cold", "lightning", "chaos"];

    public void AddExtra(string id, decimal value)
    {
        Extras[id] = Extras.TryGetValue(id, out var old) ? old + value : value;
    }
    public void Note(string id)
    {
        UnaccountedTotal++;
        Unaccounted[id] = Unaccounted.TryGetValue(id, out var n) ? n + 1 : 1;
    }
    private void AddPair(Dictionary<string, decimal> store, string id, string type, decimal value)
    {
        // Stat ids embed the type as "_added_<type>_damage" (attack/spell/global variants); a bare
        // suffix check never matches them, so look for the marker anywhere in the id.
        foreach (var t in DamageTypes)
            if (id.Contains("_added_" + t + "_damage", StringComparison.Ordinal))
            {
                store[t] = store.TryGetValue(t, out var old) ? old + value : value;
                return;
            }
        AddExtra(id, value);
    }
    internal void AddAttack(string id, decimal v, bool max)
    {
        if (max) AddPair(AddedAttackMax, id, "", v); else AddPair(AddedAttackMin, id, "", v);
    }
    public void AddGemLevel(string scope, decimal v) => GemLevels.Add((scope, v));
    public void AddScopedDamage(string[] words, decimal v) => ScopedDamage.Add((words, v));

    public void AddScopedCritChance(string[] words, decimal v) => ScopedCritChanceInc.Add((words, v));

    public void AddScopedCritBonus(string[] words, decimal v) => ScopedCritBonusAdd.Add((words, v));

    public void AddScopedAttackSpeed(string[] words, decimal v) => ScopedAttackSpeedInc.Add((words, v));
    internal void AddSpell(string id, decimal v, bool max)
    {
        if (max) AddPair(AddedSpellMax, id, "", v); else AddPair(AddedSpellMin, id, "", v);
    }
}

/// <summary>Per-item accumulation of LOCAL modifiers (they scale that item's base values only).</summary>
public sealed class ItemContext
{
    public decimal ArmourInc, EvInc, EsInc, WardInc, SpiritInc, AttackSpeedInc, PhysInc, CritChanceAdd, CritBonusAdd, BlockInc, AccuracyFlat;
    /// <summary>Weapon quality carried alongside local mods so AttackSplit can fold it into the
    /// weapon's base physical damage (PoE2/PoB: weapon quality = +quality% local physical).</summary>
    public decimal WeaponQuality;
    public readonly Dictionary<string, decimal> AddedMin = new(), AddedMax = new();
    public decimal LocalHybridArmourInc, LocalHybridEvInc, LocalHybridEsInc;
    /// <summary>Flat local defences of this item ("+71 to maximum Energy Shield" on armour is the
    /// LOCAL affix `local_energy_shield`, not the global `base_maximum_energy_shield`). They add to
    /// the item's own base — and are ignored whenever the item prints its final value, because the
    /// printed number already contains them (see CharacterCalculator.ApplyTextBases).</summary>
    public decimal ArmourFlat, EvFlat, EsFlat, WardFlat;
    public readonly List<(string Scope, decimal Value)> GemLevels = new();
    public void AddGemLevel(string scope, decimal v) => GemLevels.Add((scope, v));
}

/// <summary>Curated mapping stat id → bucket. Every entry is a direct reading of the stat id itself;
/// unknown ids never silently vanish — they are reported as unaccounted by the calculator.</summary>
public static class StatInterpreter
{
    public static void Apply(StatBucket g, string id, decimal v, ItemContext? item)
    {
        if (v == 0) return;
        // Condition-scoped stats (allies/presence, minions, flask/charm behaviour, ailments, charges,
        // gem levels, areas, durations, projectiles...) are catalogued as extras, never silently dropped.
        if (id.StartsWith("allies_in_presence") || id.StartsWith("minion") || id.Contains("flask") || id.Contains("charm") ||
            id.Contains("ailment") || id.Contains("totem") || id.Contains("grenade") || id.Contains("banner") ||
            id.EndsWith("_skill_gem_level") || id.Contains("projectile_speed") ||
            id.Contains("leech") || (id.Contains("charge") && id is not ("energy_shield_recharge_rate_+%" or "energy_shield_delay_-%")) ||
            id.Contains("presence_area") || id.Contains("light_radius") ||
            id.Contains("stun_threshold") || id.Contains("shock_chance") || id.Contains("ignite_chance") ||
            id.Contains("freeze") || id.Contains("poison") || id.Contains("bleeding") || id.Contains("thorns"))
        { g.AddExtra(id, v); return; }

        if (TryGetDamageTakenAs(id, out var source, out var destination))
        {
            if (source == "elemental")
            {
                foreach (var elemental in new[] { "fire", "cold", "lightning" })
                    AddTakenAs(g, elemental, destination, v);
            }
            else AddTakenAs(g, source, destination, v);
            return;
        }

        switch (id)
        {
            // Flat pools. Tree lines reuse item-local ids; outside an item they are global flats.
            case "base_maximum_life": g.Life += v; return;
            case "base_maximum_mana": g.Mana += v; return;
            case "base_maximum_energy_shield": g.EsFlat += v; return;
            case "base_maximum_ward": g.WardFlat += v; return;
            // The LOCAL flat ward/ES affixes of an item scale that item's own base, exactly like the
            // local percent ids below: routing them into the global pool doubled the item's defence
            // (an armour's printed "Energy Shield: 425" already contains its "+71 to maximum Energy
            // Shield"). PoB2 feeds the same "EnergyShield BASE" mod into the item's own armourData
            // (Classes/Item.lua: calcLocal(modList, "EnergyShield", "BASE", 0)).
            case "local_ward": if (item is null) { g.WardFlat += v; return; } item.WardFlat += v; return;
            case "maximum_ward_+%": g.WardInc += v; return;
            case "local_ward_+%": ApplyDefensive(g, item, v, ward: true); return;
            case "energy_shield_to_mana": case "energy_shield_%_to_mana":
                g.EnergyShieldToManaPercent += v; return;
            case "life_regeneration_percent_per_second":
                g.LifeRegenPercentPerSecond += v; return;
            case "mana_recovery_rate_+%_final":
                g.AddExtra(id, v); return;
            case "skill_mana_cost_+100%_final": case "skill_mana_cost_+%_final":
                g.ManaCostFinalPct += v; return;
            case "spirit_reserved_flat":
                g.SpiritReservedFlat += v; return;
            // Source-scoped extra gain, e.g. "non_skill_base_lightning_damage_%_to_gain_as_cold".
            case "non_skill_base_physical_damage_%_to_gain_as_fire":
            case "non_skill_base_physical_damage_%_to_gain_as_cold":
            case "non_skill_base_physical_damage_%_to_gain_as_lightning":
            case "non_skill_base_physical_damage_%_to_gain_as_chaos":
            case "non_skill_base_fire_damage_%_to_gain_as_cold":
            case "non_skill_base_fire_damage_%_to_gain_as_lightning":
            case "non_skill_base_fire_damage_%_to_gain_as_chaos":
            case "non_skill_base_cold_damage_%_to_gain_as_fire":
            case "non_skill_base_cold_damage_%_to_gain_as_lightning":
            case "non_skill_base_cold_damage_%_to_gain_as_chaos":
            case "non_skill_base_lightning_damage_%_to_gain_as_fire":
            case "non_skill_base_lightning_damage_%_to_gain_as_cold":
            case "non_skill_base_lightning_damage_%_to_gain_as_chaos":
            case "non_skill_base_chaos_damage_%_to_gain_as_fire":
            case "non_skill_base_chaos_damage_%_to_gain_as_cold":
            case "non_skill_base_chaos_damage_%_to_gain_as_lightning":
            {
                const string marker = "_damage_%_to_gain_as_";
                int at = id.IndexOf(marker, StringComparison.Ordinal);
                if (at > 0)
                {
                    var left = id[..at];
                    string src = left[(left.LastIndexOf('_') + 1)..];
                    string dst = id[(at + marker.Length)..];
                    var key = (src, dst);
                    g.SourceGainAs[key] = g.SourceGainAs.TryGetValue(key, out var old) ? old + v : v;
                    return;
                }
                g.Note(id); return;
            }
            case "energy_shield_protects_mana":
                g.EnergyShieldToManaPercent = Math.Max(g.EnergyShieldToManaPercent, 100); return;
            case "damage_removed_from_mana_before_life_%": case "damage_taken_from_mana_%":
            case "damage_%_taken_from_mana": case "damage_taken_goes_to_mana":
                g.DamageTakenFromManaPercent += v == 1 ? 100 : v; return;
            case "keystone_chaos_inoculation": g.ChaosInoculation = true; return;
            case "armour_%_applies_to_fire_cold_lightning_damage": g.ArmourAppliesToElemental = true; return;
            case "base_spirit_from_equipment": case "base_maximum_spirit": case "base_spirit": case "maximum_spirit": g.Spirit += v; return;
            case "base_physical_damage_reduction_rating": g.ArmourFlat += v; return;
            case "base_evasion_rating": g.EvFlat += v; return;
            case "local_energy_shield": if (item is null) { g.EsFlat += v; return; } item.EsFlat += v; return;
            case "local_base_evasion_rating": if (item is null) { g.EvFlat += v; return; } item.EvFlat += v; return;
            case "local_base_physical_damage_reduction_rating": g.ArmourFlat += v; return;
            case "accuracy_rating": case "base_accuracy_rating": g.AccFlat += v; return;
            case "local_accuracy_rating": if (item is null) { g.AccFlat += v; return; } item.AccuracyFlat += v; return;

            // Percent pools.
            case "maximum_life_+%": g.LifeInc += v; return;
            case "maximum_mana_+%": g.ManaInc += v; return;
            case "maximum_energy_shield_+%": g.EsInc += v; return;
            case "base_movement_velocity_+%": case "movement_speed_+%": g.MoveInc += v; return;
            // "N% reduced Mana Cost" (PoB2 maps base_mana_cost_-% to mod("ManaCost","INC") with
            // mult = -1 and divides the cost by (1 + value/100)).
            case "base_mana_cost_-%": g.ManaCostInc += -v; return;
            // Condition-scoped lines: recorded with their condition and resolved once the pools and the
            // imported config flags are known (see CharacterCalculator's life-state block).
            // PoB2 maps these three wordings explicitly: "if you've dealt a Critical Hit Recently" is a
            // ModParser tag (ModParser.lua:1926 -> Condition:CritRecently) and "when on Low Life" is in
            // ModCache ("20% increased Cast Speed when on Low Life" -> Speed INC, Condition:LowLife).
            case "cast_speed_+%_if_have_crit_recently": g.Conditionals.Add((id, v, StatCondition.CritRecently)); return;
            case "cast_speed_+%_when_on_full_life": g.Conditionals.Add((id, v, StatCondition.FullLife)); return;
            case "cast_speed_+%_when_on_low_life": g.Conditionals.Add((id, v, StatCondition.LowLife)); return;
            case "mana_regeneration_rate_+%_while_moving": g.Conditionals.Add((id, v, StatCondition.Moving)); return;
            case "mana_regeneration_rate_+%_while_stationary": g.Conditionals.Add((id, v, StatCondition.Stationary)); return;
            // "N% increased Damage for each type of Elemental Ailment on Enemy" (The Taming). PoB2 maps
            // the wording to one conditional Damage INC per enemy ailment (Modules/ModParser.lua:3837:
            // Electrocuted, Frozen, Chilled, Ignited, Shocked), so the line is expanded into one entry per
            // ailment type the config can report; the calculator counts only the active ones.
            case "conditional_damage_+%_enemy_ignited": g.Conditionals.Add((id, v, StatCondition.EnemyIgnited)); return;
            case "conditional_damage_+%_enemy_chilled": g.Conditionals.Add((id, v, StatCondition.EnemyChilled)); return;
            case "conditional_damage_+%_enemy_shocked": g.Conditionals.Add((id, v, StatCondition.EnemyShocked)); return;
            // Catalogued without a formula: rarity, attribute requirements, sprint speed, meta-skill
            // reservation, shock magnitude on self/enemies (only matters when the enemy is shocked, and
            // PoB2 leaves "witch_passive_maximum_lightning_damage_+%_final" unmapped too), the movement
            // penalty while performing an action (PoB2 keeps it out of the movement total), and
            // "N% reduced Critical Damage Bonus against you" — PoB2 maps
            // base_self_critical_strike_multiplier_-% to an *enemy* SelfCritMultiplier mod
            // (CalcOffence.lua:3847, tagged "Enemy modifiers" in CalcSections.lua), so it never touches
            // your own critical damage total.
            case "base_self_critical_strike_multiplier_-%":
            case "base_item_found_rarity_+%":
            case "global_item_attribute_requirements_+%":
            case "sprint_movement_speed_+%":
            case "reservation_efficiency_+%_of_meta_skills":
            case "shock_effect_+%":
            case "shocked_effect_on_self_+%":
            case "movement_speed_penalty_+%_while_performing_action":
            case "witch_passive_maximum_lightning_damage_+%_final":
                g.AddExtra(id, v); return;

            // Local defensive increases: on an item they scale that item; from the tree they are global.
            case "local_physical_damage_reduction_rating_+%": ApplyDefensive(g, item, v, armour: true); return;
            case "local_evasion_rating_+%": ApplyDefensive(g, item, v, evasion: true); return;
            case "local_energy_shield_+%": ApplyDefensive(g, item, v, energy: true); return;
            case "local_armour_and_energy_shield_+%": ApplyDefensive(g, item, v, armour: true, energy: true); return;
            case "local_armour_and_evasion_+%": ApplyDefensive(g, item, v, armour: true, evasion: true); return;
            case "local_armour_and_evasion_and_energy_shield_+%": ApplyDefensive(g, item, v, armour: true, evasion: true, energy: true); return;
            // "N% increased Global Armour, Evasion and Energy Shield" (PoB2 maps "armour, evasion
            // and energy shield" to the Defences bucket; the Global wording makes it a global mod).
            case "defences_+%": g.ArmourInc += v; g.EvInc += v; g.EsInc += v; return;
            case "local_evasion_and_energy_shield_+%": ApplyDefensive(g, item, v, evasion: true, energy: true); return;
            case "local_spirit_+%": if (item is null) { g.SpiritInc += v; return; } item.SpiritInc += v; return;

            // Resistances.
            case "base_resist_all_elements_%": g.FireRes += v; g.ColdRes += v; g.LightRes += v; return;
            case "base_fire_damage_resistance_%": case "fire_damage_resistance_%": g.FireRes += v; return;
            case "base_cold_damage_resistance_%": case "cold_damage_resistance_%": g.ColdRes += v; return;
            case "base_lightning_damage_resistance_%": case "lightning_damage_resistance_%": g.LightRes += v; return;
            case "base_chaos_damage_resistance_%": case "chaos_damage_resistance_%": g.ChaosRes += v; return;
            // Aura/persistent-skill resistances, e.g. Purity of Fire's
            // base_skill_buff_fire_damage_resistance_%_to_apply ("+40% to Fire Resistance" at gem level 19).
            case "base_skill_buff_fire_damage_resistance_%_to_apply": g.FireRes += v; return;
            case "base_skill_buff_cold_damage_resistance_%_to_apply": g.ColdRes += v; return;
            case "base_skill_buff_lightning_damage_resistance_%_to_apply": g.LightRes += v; return;
            case "base_skill_buff_chaos_damage_resistance_%_to_apply": g.ChaosRes += v; return;
            case "maximum_fire_damage_resistance_%": g.FireMax += v; return;
            case "maximum_cold_damage_resistance_%": g.ColdMax += v; return;
            case "maximum_lightning_damage_resistance_%": g.LightMax += v; return;
            case "maximum_chaos_damage_resistance_%": g.ChaosMax += v; return;

            // Attributes.
            case "additional_strength": case "base_strength": g.Str += v; return;
            case "additional_dexterity": case "base_dexterity": g.Dex += v; return;
            case "additional_intelligence": case "base_intelligence": g.Int += v; return;
            case "additional_all_attributes": g.Str += v; g.Dex += v; g.Int += v; return;
            case "base_strength_and_intelligence": case "additional_strength_and_intelligence": g.Str += v; g.Int += v; return;
            case "base_strength_and_dexterity": case "additional_strength_and_dexterity": g.Str += v; g.Dex += v; return;
            case "base_dexterity_and_intelligence": case "additional_dexterity_and_intelligence": g.Dex += v; g.Int += v; return;
            case "X_life_per_4_dexterity": g.LifePerDexRate += v; return;

            // Damage increases.
            case "damage_+%": g.DamageInc += v; return;
            case "physical_damage_+%": g.PhysInc += v; return;
            case "fire_damage_+%": g.FireInc += v; return;
            case "cold_damage_+%": g.ColdInc += v; return;
            case "lightning_damage_+%": g.LightInc += v; return;
            case "chaos_damage_+%": g.ChaosInc += v; return;
            case "elemental_damage_+%": g.ElemInc += v; return;
            case "elemental_damage_with_attack_skills_+%": g.ElemAttackInc += v; return;
            case "attack_damage_+%": g.AttackDamageInc += v; return;
            case "spell_damage_+%": g.SpellDamageInc += v; return;

            // Gem levels granted by items and runes ("+N to Level of all X Skills"). Scope words are
            // joined by '+', and every one of them is a GLOBAL bonus in PoE2 — a weapon's "+N to Level
            // of Socketed Gems" would be its own id — so the bonus always lands in the character bucket.
            // Routing it into an item context silently dropped it: the context built while reading the
            // item's text is discarded, and the weapon context is built from the pinned rolls only.
            case "all_skill_gem_level_+": g.AddGemLevel("all", v); return;
            case "projectile_skill_gem_level_+": g.AddGemLevel("projectile", v); return;
            case "melee_skill_gem_level_+": g.AddGemLevel("melee", v); return;
            case "spell_skill_gem_level_+": g.AddGemLevel("spell", v); return;
            case "attack_skill_gem_level_+": g.AddGemLevel("attack", v); return;
            case "minion_skill_gem_level_+": g.AddGemLevel("minion", v); return;
            case "fire_skill_gem_level_+": g.AddGemLevel("fire", v); return;
            case "cold_skill_gem_level_+": g.AddGemLevel("cold", v); return;
            case "lightning_skill_gem_level_+": g.AddGemLevel("lightning", v); return;
            case "chaos_skill_gem_level_+": g.AddGemLevel("chaos", v); return;
            case "physical_skill_gem_level_+": g.AddGemLevel("physical", v); return;
            case "elemental_skill_gem_level_+": g.AddGemLevel("elemental", v); return;
            case "fire_spell_skill_gem_level_+": g.AddGemLevel("fire+spell", v); return;
            case "cold_spell_skill_gem_level_+": g.AddGemLevel("cold+spell", v); return;
            case "lightning_spell_skill_gem_level_+": g.AddGemLevel("lightning+spell", v); return;
            case "chaos_spell_skill_gem_level_+": g.AddGemLevel("chaos+spell", v); return;
            case "physical_spell_skill_gem_level_+": g.AddGemLevel("physical+spell", v); return;

            // Skill-scoped damage increases (bow/crossbow/projectile/melee/area, per weapon class,
            // type+scope pairs like "physical with bows"). Exact per-type ids are handled above.
            case "bow_damage_+%": g.AddScopedDamage(["bow"], v); return;
            case "crossbow_damage_+%": g.AddScopedDamage(["crossbow"], v); return;
            case "projectile_damage_+%": g.AddScopedDamage(["projectile"], v); return;
            case "melee_damage_+%": g.AddScopedDamage(["melee"], v); return;
            case "area_damage_+%": g.AddScopedDamage(["area"], v); return;
            case "sword_damage_+%": g.AddScopedDamage(["sword"], v); return;
            case "mace_damage_+%": g.AddScopedDamage(["mace"], v); return;
            case "axe_damage_+%": g.AddScopedDamage(["axe"], v); return;
            case "dagger_damage_+%": g.AddScopedDamage(["dagger"], v); return;
            case "spear_damage_+%": g.AddScopedDamage(["spear"], v); return;
            case "flail_damage_+%": g.AddScopedDamage(["flail"], v); return;
            // Weapon-class-scoped critical mods and attack speed of the same family (the tree's
            // "40% increased Critical Damage Bonus with Spears" / "10% increased Critical Hit Chance with
            // Spears" / "8% increased Attack Speed with Spears"). Resolved per group like the damage lines
            // above, so they only count while that class is in the main hand.
            case "bow_critical_strike_multiplier_+": case "crossbow_critical_strike_multiplier_+":
            case "dagger_critical_strike_multiplier_+": case "flail_critical_strike_multiplier_+":
            case "quarterstaff_critical_strike_multiplier_+": case "spear_critical_strike_multiplier_+":
                g.AddScopedCritBonus([id[..id.IndexOf('_')]], v); return;
            case "crossbow_critical_strike_chance_+%": case "dagger_critical_strike_chance_+%":
            case "flail_critical_strike_chance_+%": case "quarterstaff_critical_strike_chance_+%":
            case "spear_critical_strike_chance_+%":
                g.AddScopedCritChance([id[..id.IndexOf('_')]], v); return;
            case "axe_attack_speed_+%": case "bow_attack_speed_+%": case "crossbow_attack_speed_+%":
            case "dagger_attack_speed_+%": case "flail_attack_speed_+%": case "quarterstaff_attack_speed_+%":
            case "spear_attack_speed_+%": case "sword_attack_speed_+%":
                g.AddScopedAttackSpeed([id[..id.IndexOf('_')]], v); return;
            case "staff_damage_+%": g.AddScopedDamage(["staff"], v); return;
            case "quarterstaff_damage_+%": g.AddScopedDamage(["quarterstaff"], v); return;
            case "wand_damage_+%": g.AddScopedDamage(["wand"], v); return;
            case "channelled_skill_damage_+%": g.AddScopedDamage(["channelled"], v); return;
            case "attack_area_damage_+%": g.AddScopedDamage(["attack", "area"], v); return;
            case "spell_area_damage_+%": g.AddScopedDamage(["spell", "area"], v); return;
            case "physical_bow_damage_+%": g.AddScopedDamage(["physical", "bow"], v); return;
            case "physical_attack_damage_+%": g.AddScopedDamage(["physical", "attack"], v); return;
            case "cold_attack_damage_+%": g.AddScopedDamage(["cold", "attack"], v); return;
            case "fire_attack_damage_+%": g.AddScopedDamage(["fire", "attack"], v); return;
            case "lightning_attack_damage_+%": g.AddScopedDamage(["lightning", "attack"], v); return;

            // Speeds.
            case "base_cast_speed_+%": case "cast_speed_+%": g.CastSpeedInc += v; return;
            case "attack_speed_+%": g.AttackSpeedInc += v; return;
            case "skill_speed_+%": g.SkillSpeedInc += v; return;
            case "local_attack_speed_+%": if (item is null) { g.AttackSpeedInc += v; return; } item.AttackSpeedInc += v; return;

            // Critical strikes. PoE2: base Critical Damage Bonus is 100 (crits deal 2x by default).
            case "critical_strike_chance_+%": g.CritChanceInc += v; return;
            case "attack_critical_strike_chance_+%": g.AttackCritInc += v; return;
            case "spell_critical_strike_chance_+%": g.SpellCritInc += v; return;
            case "local_critical_strike_chance": if (item is null) { g.AddExtra(id, v); return; } item.CritChanceAdd += v; return;
            case "base_critical_strike_multiplier_+": g.CritBonusAdd += v; return;
            // "+X% to Critical Hit Chance": a flat addition to the critical hit chance (PoB2 maps it to
            // CritChance BASE and its panel adds it before the increases: CalcOffence.lua:3718).
            case "critical_strike_chance_+": g.CritChanceAdd += v; return;
            case "attack_critical_strike_multiplier_+": g.AttackCritBonusAdd += v; return;
            case "base_spell_critical_strike_multiplier_+": g.SpellCritBonusAdd += v; return;
            case "spell_critical_strike_multiplier_+%": g.SpellCritBonusInc += v; return;
            case "critical_strike_multiplier_+%": g.CritBonusInc += v; return;
            case "local_critical_strike_multiplier_+": if (item is null) { g.AddExtra(id, v); return; } item.CritBonusAdd += v; return;

            // Recovery and panel stats.
            case "base_life_regeneration_rate_per_minute": g.LifeRegenPerMin += v; return;
            case "life_regeneration_rate_+%": g.LifeRegenInc += v; return;
            case "mana_regeneration_rate_+%": g.ManaRegenInc += v; return;
            case "energy_shield_recharge_rate_+%": g.EsRechargeInc += v; return;
            case "energy_shield_delay_-%": g.EsRechargeFasterInc += v; return;
            case "base_deflection_rating_%_of_evasion_rating": g.DeflectPctOfEvasion += v; return;
            case "base_deflection_rating_%_of_armour": g.DeflectPctOfArmour += v; return;
            case "deflection_rating_+%": g.DeflectInc += v; return;
            case "base_damage_%_deflected": g.DeflectEffectAdd += v; return;
            case "local_block_chance_+%": if (item is null) { g.BlockInc += v; return; } item.BlockInc += v; return;
            case "local_additional_block_chance_%": g.BlockAdditional += v; return;
            case "additional_block_%": g.BlockAdditional += v; return;
            case "additional_maximum_block_%": g.BlockMaxAdd += v; return;
            case "maximum_block_chance_override": g.BlockMaxOverride = v; return;
            case "base_spell_block_%": case "base_spell_block_chance_%": case "spell_block_chance_%": g.SpellBlockBase += v; return;
            case "additional_spell_block_%": g.SpellBlockAdditional += v; return;
            case "additional_maximum_spell_block_%": g.SpellBlockMaxAdd += v; return;
            case "maximum_spell_block_chance_override": g.SpellBlockMaxOverride = v; return;
            // PoB2 SkillStatMap: base_chance_to_dodge_% → AttackDodgeChance and
            // base_chance_to_dodge_spells_% → SpellDodgeChance.
            case "base_chance_to_dodge_%": g.AttackDodgeChance += v; return;
            case "base_chance_to_dodge_spells_%": g.SpellDodgeChance += v; return;
            case "spell_suppression_chance_%": g.SpellSuppressionChance += v; return;
            case "spell_suppression_effect": case "spell_suppression_effect_%": g.SpellSuppressionEffectAdd += v; return;

            // Added damage.
            case "attack_minimum_added_physical_damage": case "attack_minimum_added_fire_damage":
            case "attack_minimum_added_cold_damage": case "attack_minimum_added_lightning_damage":
            case "attack_minimum_added_chaos_damage":
                g.AddAttack(id, v, max: false); return;
            case "attack_maximum_added_physical_damage": case "attack_maximum_added_fire_damage":
            case "attack_maximum_added_cold_damage": case "attack_maximum_added_lightning_damage":
            case "attack_maximum_added_chaos_damage":
                g.AddAttack(id, v, max: true); return;
            // Reverse-translation yields the global variants of added damage for imported uniques.
            case "global_minimum_added_physical_damage": case "global_minimum_added_fire_damage":
            case "global_minimum_added_cold_damage": case "global_minimum_added_lightning_damage":
            case "global_minimum_added_chaos_damage":
                g.AddAttack(id, v, max: false); return;
            case "global_maximum_added_physical_damage": case "global_maximum_added_fire_damage":
            case "global_maximum_added_cold_damage": case "global_maximum_added_lightning_damage":
            case "global_maximum_added_chaos_damage":
                g.AddAttack(id, v, max: true); return;
            case "spell_minimum_added_physical_damage": case "spell_minimum_added_fire_damage":
            case "spell_minimum_added_cold_damage": case "spell_minimum_added_lightning_damage":
            case "spell_minimum_added_chaos_damage":
                g.AddSpell(id, v, max: false); return;
            case "spell_maximum_added_physical_damage": case "spell_maximum_added_fire_damage":
            case "spell_maximum_added_cold_damage": case "spell_maximum_added_lightning_damage":
            case "spell_maximum_added_chaos_damage":
                g.AddSpell(id, v, max: true); return;
            case "local_minimum_added_physical_damage": case "local_minimum_added_fire_damage":
            case "local_minimum_added_cold_damage": case "local_minimum_added_lightning_damage":
            case "local_minimum_added_chaos_damage":
                if (item is null) { g.AddAttack(id, v, false); return; } AddLocal(item, id, v, false); return;
            case "local_maximum_added_physical_damage": case "local_maximum_added_fire_damage":
            case "local_maximum_added_cold_damage": case "local_maximum_added_lightning_damage":
            case "local_maximum_added_chaos_damage":
                if (item is null) { g.AddAttack(id, v, true); return; } AddLocal(item, id, v, true); return;

            // "Gain as" non-skill damage.
            case "non_skill_base_all_damage_%_to_gain_as_physical": g.GainAs["physical"] = g.GainAs.TryGetValue("physical", out var gp) ? gp + v : v; return;
            case "non_skill_base_all_damage_%_to_gain_as_fire": g.GainAs["fire"] = g.GainAs.TryGetValue("fire", out var gf) ? gf + v : v; return;
            case "non_skill_base_all_damage_%_to_gain_as_cold": g.GainAs["cold"] = g.GainAs.TryGetValue("cold", out var gc) ? gc + v : v; return;
            case "non_skill_base_all_damage_%_to_gain_as_lightning": g.GainAs["lightning"] = g.GainAs.TryGetValue("lightning", out var gl) ? gl + v : v; return;
            case "non_skill_base_all_damage_%_to_gain_as_chaos": g.GainAs["chaos"] = g.GainAs.TryGetValue("chaos", out var gx) ? gx + v : v; return;

            // Weapon-local physical damage increase.
            case "local_physical_damage_+%": if (item is null) { g.PhysInc += v; return; } item.PhysInc += v; return;

            // Damaging ailment sources (PoB2 modifier bucket names: ChanceToIgnite/_Poison/_Bleed,
            // IgniteChance/FireDamage... for DoT; "final" ids are the "+% more" multipliers).
            case "base_chance_to_ignite_%": g.IgniteChancePct += v; return;
            case "base_chance_to_poison_%": case "base_chance_to_poison_on_hit_%": g.PoisonChancePct += v; return;
            case "base_chance_to_bleed_%": case "base_chance_to_inflict_bleeding_%": g.BleedChancePct += v; return;
            case "active_skill_ignite_chance_+%_final": g.IgniteChanceMorePct += v; return;
            case "active_skill_poison_chance_+%_final": g.PoisonChanceMorePct += v; return;
            case "active_skill_bleeding_chance_+%_final": g.BleedChanceMorePct += v; return;
            case "damage_over_time_+%": g.DotInc += v; return;
            case "burning_damage_+%": g.BurningInc += v; return;
            case "poison_damage_+%": g.PoisonInc += v; return;
            case "bleeding_damage_+%": g.BleedInc += v; return;
            case "ailment_duration_+%": case "base_ailment_duration_+%": g.AilmentDurationInc += v; return;
            // Gem quality and triggered-spell scalers (PoE2 ids seen in imported builds).
            case "all_skill_gem_quality_+": g.AllGemQuality += v; return;
            case "triggered_spell_spell_damage_+%": g.SpellDamageInc += v; return;
            case "spell_damage_+%_per_100_maximum_mana": g.SpellDamagePer100Mana += v; return;
            case "spell_critical_strike_chance_+%_per_100_maximum_mana": g.SpellCritChancePer100Mana += v; return;
            case "non_channelling_spells_life_cost_+%_of_maximum_life": g.LifeCostPercentOfMaxLife += v; return;
            case "archmage_all_damage_%_to_gain_as_lightning_to_grant_to_non_channelling_spells_per_100_max_mana":
                g.ArchmageGainAsLightningPer100Mana += v; return;
            // PoB2 distinguishes the two totem speed wordings (SkillStatMap.lua):
            //   totem_skill_cast_speed_+% / totem_skill_attack_speed_+% -> Speed INC, Totem keyword
            //   summon_totem_cast_speed_+%                             -> TotemPlacementSpeed INC
            // Only the first pair speeds up the skill the totem casts. Counting the second one as cast
            // speed made the reference build's totem rate 100% too high.
            case "totem_skill_cast_speed_+%": case "totem_skill_attack_speed_+%":
                g.TotemCastSpeedInc += v; return;
            case "summon_totem_cast_speed_+%":
                g.TotemPlacementSpeedInc += v; return;
            case "intelligence_skill_gem_level_+": g.AddGemLevel("intelligence", v); return;
            case "strength_skill_gem_level_+": g.AddGemLevel("strength", v); return;
            case "dexterity_skill_gem_level_+": g.AddGemLevel("dexterity", v); return;

            // Catalogued, but not part of v1 formulas.
            case "base_skill_area_of_effect_+%": case "skill_effect_duration_+%":
            case "base_projectile_speed_+%": case "accuracy_rating_+%": case "damage_+%_final":
            case "local_additional_charm_slots": case "base_chance_to_pierce_%": case "base_slow_potency_+%":
            case "damage_taken_goes_to_life_over_4_seconds_%":
            case "base_deflection_rating": case "hit_damage_freeze_multiplier_+%": case "base_life_leech_amount_+%":
            case "charm_recover_X_life_when_used": case "charm_recover_X_mana_when_used":
                g.AddExtra(id, v); return;
            default:
                g.Note(id); return;
        }

        static void ApplyDefensive(StatBucket g, ItemContext? item, decimal v, bool armour = false, bool evasion = false, bool energy = false, bool ward = false)
        {
            if (item is null)
            {
                if (armour) g.ArmourInc += v;
                if (evasion) g.EvInc += v;
                if (energy) g.EsInc += v;
                if (ward) g.WardInc += v;
                return;
            }
            if (armour) item.ArmourInc += v;
            if (evasion) item.EvInc += v;
            if (energy) item.EsInc += v;
            if (ward) item.WardInc += v;
        }
        static void AddLocal(ItemContext item, string id, decimal v, bool max)
        {
            var store = max ? item.AddedMax : item.AddedMin;
            foreach (var type in new[] { "physical", "fire", "cold", "lightning", "chaos" })
                if (id.Contains("_added_" + type + "_damage")) { store[type] = store.TryGetValue(type, out var old) ? old + v : v; return; }
        }
        static void AddTakenAs(StatBucket bucket, string source, string destination, decimal value)
        {
            var key = (source, destination);
            bucket.DamageTakenAs[key] = bucket.DamageTakenAs.TryGetValue(key, out var old) ? old + value : value;
        }
    }

    /// <summary>True when the interpreter has a bucket for this stat id. Used by the aura pass so a stat it
    /// cannot place yet is reported under its own source instead of as a bare id.</summary>
    public static bool Handles(string id)
    {
        var probe = new StatBucket();
        Apply(probe, id, 1, null);
        return probe.UnaccountedTotal == 0;
    }

    private static bool TryGetDamageTakenAs(string id, out string source, out string destination)
    {
        source = "";
        destination = "";
        if (!id.Contains("damage_taken", StringComparison.Ordinal) || !id.Contains("_as_", StringComparison.Ordinal))
            return false;
        destination = id[(id.LastIndexOf("_as_", StringComparison.Ordinal) + 4)..];
        destination = destination.Split('_')[0];
        if (destination is not ("physical" or "fire" or "cold" or "lightning" or "chaos")) return false;
        source = id.StartsWith("base_", StringComparison.Ordinal) ? id[5..] : id;
        foreach (var type in new[] { "physical", "fire", "cold", "lightning", "chaos", "elemental" })
            if (source.StartsWith(type + "_", StringComparison.Ordinal)) { source = type; return true; }
        return false;
    }

    /// <summary>Applies a stat dictionary (tree line or implicit/explicit collection) to a bucket.</summary>
    public static void ApplyAll(StatBucket g, IReadOnlyDictionary<string, decimal> stats, ItemContext? item = null)
    {
        foreach (var (id, value) in stats) Apply(g, id, value, item);
    }
}
