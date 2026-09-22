using PoeBuilder.Core.Equipment;

namespace PoeBuilder.Core.Calculation;

/// <summary>Aggregated character stats. Only buckets with a verified game formula are consumed by
/// the calculator; everything else is shown as "catalogued but not in formulas v1" or unaccounted.</summary>
public sealed class StatBucket
{
    public decimal Life, LifeInc, Mana, ManaInc, EsFlat, EsInc, Spirit, SpiritInc;
    public decimal ArmourFlat, ArmourInc, EvFlat, EvInc, AccFlat, AccInc;
    public decimal FireRes, ColdRes, LightRes, ChaosRes, FireMax, ColdMax, LightMax, ChaosMax;
    public decimal Str, Dex, Int;
    public decimal MoveInc, AttackSpeedInc, CastSpeedInc, SkillSpeedInc;
    public decimal CritChanceInc, AttackCritInc, SpellCritInc, CritChanceAdd;
    public decimal CritBonusAdd, AttackCritBonusAdd, SpellCritBonusAdd;
    public decimal DamageInc, PhysInc, FireInc, ColdInc, LightInc, ChaosInc, ElemInc, ElemAttackInc, AttackDamageInc, SpellDamageInc;
    public decimal LifeRegenPerMin, ManaRegenInc, EsRechargeInc, EsRechargeFasterInc;
    public decimal DeflectPctOfEvasion, DeflectPctOfArmour, DeflectInc, DeflectEffectAdd, LifePerDexRate;
    public decimal BlockInc, BlockAdditional, BlockMaxAdd;
    public decimal SpellBlockBase, SpellBlockAdditional, SpellBlockMaxAdd;
    public decimal SpellSuppressionChance, SpellSuppressionEffectAdd;
    public decimal? BlockMaxOverride, SpellBlockMaxOverride;
    // Gem levels granted by tree/items: (scope, value); scope words joined with '+' (e.g. "fire+spell").
    public readonly List<(string Scope, decimal Value)> GemLevels = new();
    // Skill-scoped damage increases: (scope words, value); words must all appear in the gem's tags.
    public readonly List<(string[] Words, decimal Value)> ScopedDamage = new();
    public readonly Dictionary<string, decimal> AddedAttackMin = new(), AddedAttackMax = new();
    public readonly Dictionary<string, decimal> AddedSpellMin = new(), AddedSpellMax = new();
    public readonly Dictionary<string, decimal> GainAs = new();
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
        foreach (var t in DamageTypes) if (id.EndsWith(t)) { store[t] = store.TryGetValue(t, out var old) ? old + value : value; return; }
        AddExtra(id, value);
    }
    internal void AddAttack(string id, decimal v, bool max)
    {
        if (max) AddPair(AddedAttackMax, id, "", v); else AddPair(AddedAttackMin, id, "", v);
    }
    public void AddGemLevel(string scope, decimal v) => GemLevels.Add((scope, v));
    public void AddScopedDamage(string[] words, decimal v) => ScopedDamage.Add((words, v));
    internal void AddSpell(string id, decimal v, bool max)
    {
        if (max) AddPair(AddedSpellMax, id, "", v); else AddPair(AddedSpellMin, id, "", v);
    }
}

/// <summary>Per-item accumulation of LOCAL modifiers (they scale that item's base values only).</summary>
public sealed class ItemContext
{
    public decimal ArmourInc, EvInc, EsInc, SpiritInc, AttackSpeedInc, PhysInc, CritChanceAdd, CritBonusAdd, BlockInc, AccuracyFlat;
    public readonly Dictionary<string, decimal> AddedMin = new(), AddedMax = new();
    public decimal LocalHybridArmourInc, LocalHybridEvInc, LocalHybridEsInc;
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
            id.Contains("leech") || id.Contains("charge") || id.Contains("presence_area") || id.Contains("light_radius") ||
            id.Contains("stun_threshold") || id.Contains("shock_chance") || id.Contains("ignite_chance") ||
            id.Contains("freeze") || id.Contains("poison") || id.Contains("bleeding") || id.Contains("thorns"))
        { g.AddExtra(id, v); return; }

        switch (id)
        {
            // Flat pools. Tree lines reuse item-local ids; outside an item they are global flats.
            case "base_maximum_life": g.Life += v; return;
            case "base_maximum_mana": g.Mana += v; return;
            case "base_maximum_energy_shield": g.EsFlat += v; return;
            case "base_spirit_from_equipment": case "base_maximum_spirit": g.Spirit += v; return;
            case "base_physical_damage_reduction_rating": g.ArmourFlat += v; return;
            case "base_evasion_rating": g.EvFlat += v; return;
            case "local_energy_shield": g.EsFlat += v; return;
            case "local_base_evasion_rating": g.EvFlat += v; return;
            case "local_base_physical_damage_reduction_rating": g.ArmourFlat += v; return;
            case "accuracy_rating": case "base_accuracy_rating": g.AccFlat += v; return;
            case "local_accuracy_rating": if (item is null) { g.AccFlat += v; return; } item.AccuracyFlat += v; return;

            // Percent pools.
            case "maximum_life_+%": g.LifeInc += v; return;
            case "maximum_mana_+%": g.ManaInc += v; return;
            case "maximum_energy_shield_+%": g.EsInc += v; return;
            case "base_movement_velocity_+%": case "movement_speed_+%": g.MoveInc += v; return;

            // Local defensive increases: on an item they scale that item; from the tree they are global.
            case "local_physical_damage_reduction_rating_+%": ApplyDefensive(g, item, v, armour: true); return;
            case "local_evasion_rating_+%": ApplyDefensive(g, item, v, evasion: true); return;
            case "local_energy_shield_+%": ApplyDefensive(g, item, v, energy: true); return;
            case "local_armour_and_energy_shield_+%": ApplyDefensive(g, item, v, armour: true, energy: true); return;
            case "local_armour_and_evasion_+%": ApplyDefensive(g, item, v, armour: true, evasion: true); return;
            case "local_evasion_and_energy_shield_+%": ApplyDefensive(g, item, v, evasion: true, energy: true); return;
            case "local_spirit_+%": if (item is null) { g.SpiritInc += v; return; } item.SpiritInc += v; return;

            // Resistances.
            case "base_resist_all_elements_%": g.FireRes += v; g.ColdRes += v; g.LightRes += v; return;
            case "base_fire_damage_resistance_%": case "fire_damage_resistance_%": g.FireRes += v; return;
            case "base_cold_damage_resistance_%": case "cold_damage_resistance_%": g.ColdRes += v; return;
            case "base_lightning_damage_resistance_%": case "lightning_damage_resistance_%": g.LightRes += v; return;
            case "base_chaos_damage_resistance_%": case "chaos_damage_resistance_%": g.ChaosRes += v; return;
            case "maximum_fire_damage_resistance_%": g.FireMax += v; return;
            case "maximum_cold_damage_resistance_%": g.ColdMax += v; return;
            case "maximum_lightning_damage_resistance_%": g.LightMax += v; return;
            case "maximum_chaos_damage_resistance_%": g.ChaosMax += v; return;

            // Attributes.
            case "additional_strength": case "base_strength": g.Str += v; return;
            case "additional_dexterity": case "base_dexterity": g.Dex += v; return;
            case "additional_intelligence": case "base_intelligence": g.Int += v; return;
            case "additional_all_attributes": g.Str += v; g.Dex += v; g.Int += v; return;
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

            // Gem levels granted by items ("+N to Level of all X Skills"). Scope words joined by '+'.
            case "all_skill_gem_level_+": if (item is null) g.AddGemLevel("all", v); else item.AddGemLevel("all", v); return;
            case "projectile_skill_gem_level_+": if (item is null) g.AddGemLevel("projectile", v); else item.AddGemLevel("projectile", v); return;
            case "melee_skill_gem_level_+": if (item is null) g.AddGemLevel("melee", v); else item.AddGemLevel("melee", v); return;
            case "spell_skill_gem_level_+": if (item is null) g.AddGemLevel("spell", v); else item.AddGemLevel("spell", v); return;
            case "attack_skill_gem_level_+": if (item is null) g.AddGemLevel("attack", v); else item.AddGemLevel("attack", v); return;
            case "minion_skill_gem_level_+": if (item is null) g.AddGemLevel("minion", v); else item.AddGemLevel("minion", v); return;
            case "fire_skill_gem_level_+": if (item is null) g.AddGemLevel("fire", v); else item.AddGemLevel("fire", v); return;
            case "cold_skill_gem_level_+": if (item is null) g.AddGemLevel("cold", v); else item.AddGemLevel("cold", v); return;
            case "lightning_skill_gem_level_+": if (item is null) g.AddGemLevel("lightning", v); else item.AddGemLevel("lightning", v); return;
            case "chaos_skill_gem_level_+": if (item is null) g.AddGemLevel("chaos", v); else item.AddGemLevel("chaos", v); return;
            case "physical_skill_gem_level_+": if (item is null) g.AddGemLevel("physical", v); else item.AddGemLevel("physical", v); return;
            case "elemental_skill_gem_level_+": if (item is null) g.AddGemLevel("elemental", v); else item.AddGemLevel("elemental", v); return;
            case "fire_spell_skill_gem_level_+": if (item is null) g.AddGemLevel("fire+spell", v); else item.AddGemLevel("fire+spell", v); return;
            case "cold_spell_skill_gem_level_+": if (item is null) g.AddGemLevel("cold+spell", v); else item.AddGemLevel("cold+spell", v); return;
            case "lightning_spell_skill_gem_level_+": if (item is null) g.AddGemLevel("lightning+spell", v); else item.AddGemLevel("lightning+spell", v); return;
            case "chaos_spell_skill_gem_level_+": if (item is null) g.AddGemLevel("chaos+spell", v); else item.AddGemLevel("chaos+spell", v); return;
            case "physical_spell_skill_gem_level_+": if (item is null) g.AddGemLevel("physical+spell", v); else item.AddGemLevel("physical+spell", v); return;

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
            case "attack_critical_strike_multiplier_+": g.AttackCritBonusAdd += v; return;
            case "base_spell_critical_strike_multiplier_+": g.SpellCritBonusAdd += v; return;
            case "local_critical_strike_multiplier_+": if (item is null) { g.AddExtra(id, v); return; } item.CritBonusAdd += v; return;

            // Recovery and panel stats.
            case "base_life_regeneration_rate_per_minute": g.LifeRegenPerMin += v; return;
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

            // Catalogued, but not part of v1 formulas.
            case "energy_shield_delay_-%": case "base_skill_area_of_effect_+%": case "skill_effect_duration_+%":
            case "base_projectile_speed_+%": case "accuracy_rating_+%": case "damage_+%_final":
            case "local_additional_charm_slots": case "base_chance_to_pierce_%": case "base_slow_potency_+%":
            case "damage_taken_goes_to_life_over_4_seconds_%": case "armour_%_applies_to_fire_cold_lightning_damage":
            case "base_deflection_rating": case "hit_damage_freeze_multiplier_+%": case "base_life_leech_amount_+%":
            case "charm_recover_X_life_when_used": case "charm_recover_X_mana_when_used":
                g.AddExtra(id, v); return;
            default:
                g.Note(id); return;
        }

        static void ApplyDefensive(StatBucket g, ItemContext? item, decimal v, bool armour = false, bool evasion = false, bool energy = false)
        {
            if (item is null)
            {
                if (armour) g.ArmourInc += v;
                if (evasion) g.EvInc += v;
                if (energy) g.EsInc += v;
                return;
            }
            if (armour) item.ArmourInc += v;
            if (evasion) item.EvInc += v;
            if (energy) item.EsInc += v;
        }
        static void AddLocal(ItemContext item, string id, decimal v, bool max)
        {
            var store = max ? item.AddedMax : item.AddedMin;
            foreach (var type in new[] { "physical", "fire", "cold", "lightning", "chaos" })
                if (id.Contains("_added_" + type + "_damage")) { store[type] = store.TryGetValue(type, out var old) ? old + v : v; return; }
        }
    }

    /// <summary>Applies a stat dictionary (tree line or implicit/explicit collection) to a bucket.</summary>
    public static void ApplyAll(StatBucket g, IReadOnlyDictionary<string, decimal> stats, ItemContext? item = null)
    {
        foreach (var (id, value) in stats) Apply(g, id, value, item);
    }
}
