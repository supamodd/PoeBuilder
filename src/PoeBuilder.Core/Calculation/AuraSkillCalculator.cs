using System.Text.RegularExpressions;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Models;

namespace PoeBuilder.Core.Calculation;

/// <summary>One live instance of a persistent/aura skill: a gem in an enabled socket group or a skill an
/// equipped item grants ("Grants Skill: Level 19 Purity of Fire"). Its stats act on the whole character
/// while the skill is on, so the instance only needs its effect, its level and its quality.</summary>
public sealed record AuraInstance(string Name, string EffectId, int Level, decimal Quality);

/// <summary>A "Grants Skill: …" line of an equipped item, resolved to its level. The raw line travels with
/// it so the honesty report can stop listing a line whose modifiers are now in the numbers.</summary>
public sealed record ItemSkillGrant(string Line, string Name, int Level);

/// <summary>
/// Persistent skills (auras and buffs) whose stats PoB2 applies globally.
/// <para>
/// PoB2 turns every stat of a persistent skill that carries a <c>GlobalEffect</c> tag (effectType Aura,
/// Buff or Global) into a buff of that skill and adds its modifiers to the player while the skill is
/// enabled (<c>Modules/CalcSetup.lua:1855</c>, <c>Modules/CalcActiveSkill.lua:1029-1103</c>). The value of
/// each modifier is the effect's per-level value plus its quality share,
/// <c>value = level value + modf(qualityStat × quality)</c> (<c>Modules/CalcTools.lua:138-205</c>).
/// </para>
/// <para>
/// Several instances of the same buff are merged by name and the <b>highest</b> value of each modifier
/// wins (<c>Modules/CalcPerform.lua:40-57</c>, "Merge an instance of a buff, taking the highest value of
/// each modifier"). That is why a socketed Purity of Fire and the same aura granted by the equipped
/// sceptre contribute +45 once instead of twice — the reference build's own panel shows a single row.
/// </para>
/// </summary>
public static class AuraSkillCalculator
{
    private static readonly Regex GrantLine = new(
        @"^Grants Skill:\s*(?:Level\s+(\d+)\s+)?(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"Grants Skill: Level 19 Purity of Fire" / "Grants Skill: Spear Throw" lines of an item text.
    /// PoB2 parses the same wording into an ExtraSkill mod with that level (<c>Modules/ModParser.lua:3560-3561</c>).</summary>
    public static IReadOnlyList<ItemSkillGrant> ParseGrants(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var grants = new List<ItemSkillGrant>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Grants Skill:", StringComparison.OrdinalIgnoreCase)) continue;
            if (GrantLine.Match(line) is not { Success: true } match) continue;
            string name = match.Groups[2].Value.Trim();
            if (name.Length == 0) continue;
            int level = int.TryParse(match.Groups[1].Value, out int parsed) ? parsed : 1;
            grants.Add(new(line, name, Math.Clamp(level, 1, 40)));
        }
        return grants;
    }

    /// <summary>True when the skill has at least one stat that PoB2 applies globally (an aura/buff effect).
    /// A damage skill, a support, a curse (its stats belong to the enemy) or a trigger has none of those
    /// tags and is left to the offence pipeline.</summary>
    public static bool IsAura(GameCatalog catalog, SkillData skill)
    {
        foreach (var effect in skill.Effects)
            foreach (var statId in effect.Stats)
                if (IsGlobal(catalog, effect, statId)) return true;
        return false;
    }

    /// <summary>Effect types PoB2 adds to the player's own modifier list. A curse's GlobalEffect
    /// (effectType = "Curse") lands in the enemy's list instead, which the offence pipeline owns.</summary>
    private static readonly string[] PlayerEffectTypes = ["Aura", "Buff", "Global"];

    private static IReadOnlyList<SkillStatSpec> GlobalSpecs(GameCatalog catalog, SkillEffect effect, string statId) =>
        [.. catalog.SkillData.SpecsFor(statId, effect).Where(spec => PlayerEffectTypes.Any(type =>
            spec.Spec.Contains("effectType = \"" + type + "\"", StringComparison.Ordinal)))];

    private static bool IsGlobal(GameCatalog catalog, SkillEffect effect, string statId) =>
        GlobalSpecs(catalog, effect, statId).Count > 0;

    /// <summary>PoB2's <c>effectCond</c>/<c>modCond</c> tags on a GlobalEffect modifier.</summary>
    private static readonly Regex ConditionTag = new(
        @"(?:effectCond|modCond) = ""(\w+)""", RegexOptions.Compiled);

    /// <summary>The condition a spec is gated behind: true (met), false (switched off) or null (a condition
    /// this model cannot resolve, including enemy-actor criteria — the offence pipeline owns those).</summary>
    private static bool? ConditionOf(SkillStatSpec spec, BuildConditions? conditions)
    {
        if (spec.Spec.Contains("actor = \"enemy\"", StringComparison.Ordinal)) return null;
        var match = ConditionTag.Match(spec.Spec);
        if (!match.Success) return true;
        if (conditions is null) return null;
        return match.Groups[1].Value switch
        {
            "FlameWallAddedDamage" => conditions.FlameWallAddedDamage,
            "Moving" => conditions.Moving,
            "CritRecently" => conditions.CritRecently,
            "BeenHitRecently" => conditions.BeenHitRecently,
            _ => null
        };
    }

    /// <summary>Applies every global-effect stat of the given instances, once per (buff, stat) pair with the
    /// highest value any instance produced. A stat this model cannot place yet — because the interpreter has
    /// no bucket for it, or because PoB2 gates it behind a condition the import does not resolve — is
    /// reported under the skill's name instead of being dropped silently.</summary>
    public static int Apply(StatBucket bucket, GameCatalog catalog, IReadOnlyList<AuraInstance> instances,
        BuildConditions? conditions = null)
    {
        // Key = buff name + the stat id, so two different buffs never merge and two instances of one buff do.
        var merged = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var instance in instances)
        {
            var skill = catalog.SkillData.ForEffect(instance.EffectId);
            if (skill is null)
            {
                bucket.Note("aura: " + instance.Name + " (no pinned skill data)");
                continue;
            }
            foreach (var effect in skill.Effects)
                foreach (var statId in effect.Stats)
                {
                    var specs = GlobalSpecs(catalog, effect, statId);
                    if (specs.Count == 0) continue;
                    bool unresolved = false, applicable = false;
                    foreach (var spec in specs)
                    {
                        // applyNotPlayer means the effect never reaches the character (PoB2 keeps it for
                        // minions/allies), so it is not a gap in this model.
                        if (spec.Spec.Contains("applyNotPlayer = true", StringComparison.Ordinal)) continue;
                        bool? holds = ConditionOf(spec, conditions);
                        if (holds is null) unresolved = true;
                        else if (holds == true) applicable = true;
                    }
                    if (!applicable)
                    {
                        if (unresolved) bucket.Note("aura: " + instance.Name + " " + statId + " (condition not resolved)");
                        continue;
                    }
                    decimal? levelValue = SkillData.Positional(effect, statId, instance.Level);
                    decimal qualityValue = 0;
                    foreach (var (stat, perQuality) in skill.QualityStats)
                        if (string.Equals(stat, statId, StringComparison.Ordinal) && instance.Quality > 0)
                            // PoB2 keeps the integral part only (math.modf of qualityStat × quality).
                            qualityValue += Math.Truncate(perQuality * instance.Quality);
                    if (levelValue is null && qualityValue == 0) continue;
                    decimal value = (levelValue ?? 0) + qualityValue;
                    string key = instance.Name + "\u0000" + statId;
                    if (!merged.TryGetValue(key, out var best) || value > best) merged[key] = value;
                }
        }
        int applied = 0;
        foreach (var (key, value) in merged)
        {
            if (value == 0) continue;
            int split = key.IndexOf('\u0000');
            string name = key[..split], statId = key[(split + 1)..];
            if (!StatInterpreter.Handles(statId))
            {
                bucket.Note("aura: " + name + " " + statId);
                continue;
            }
            StatInterpreter.Apply(bucket, statId, value, null);
            bucket.Extras["Aura:" + name + " " + statId] = value;
            applied++;
        }
        if (instances.Count > 0) bucket.Extras["AuraInstances"] = instances.Count;
        return applied;
    }
}
