using System.Security.Cryptography;
using System.Text.Json;

namespace PoeBuilder.Core.Equipment;

/// <summary>One level row of a PoB2 skill: positional values in the effect's <c>stats</c> order plus
/// the named fields PoB2 stores on the same row (baseMultiplier, attackSpeedMultiplier, critChance,
/// cooldown, levelRequirement, Mana, cost…).</summary>
public sealed record SkillLevelValues(IReadOnlyList<decimal> Positions, IReadOnlyDictionary<string, decimal> Named)
{
    public decimal Number(string key, decimal fallback) => Named.TryGetValue(key, out var value) ? value : fallback;
    public decimal? Number(string key) => Named.TryGetValue(key, out var value) ? value : null;
}

/// <summary>One entry of a PoB2 skill statMap: the raw Lua mod spec plus its optional value multiplier
/// (<c>mult = -1</c> means the stat's sign is inverted, as PoB2 itself applies it).</summary>
public sealed record SkillStatSpec(string Spec, decimal? Mult);

/// <summary>A skill effect variant (PoB2's numbered <c>[n]</c> table): its label, the ordered stat-id
/// list that positions the level values, the per-level rows, PoB2's own stat→mod mapping and its constant
/// stats. A constant is a value that does NOT scale with level (e.g. Blazing Critical's "15% of damage
/// gained as extra Fire damage with attacks on a critical hit"), and PoB2 adds it to the mod exactly like a
/// level value.</summary>
public sealed record SkillEffect(string Label, string[] Stats, IReadOnlyDictionary<string, SkillLevelValues> Levels,
    IReadOnlyDictionary<string, SkillStatSpec[]> StatMap, IReadOnlyDictionary<string, decimal> Constants)
{
    /// <summary>The constant value of one stat, or 0 when the effect has none.</summary>
    public decimal Constant(string statId) => Constants.TryGetValue(statId, out var value) ? value : 0m;
}

/// <summary>PoB2's own per-skill data (PathOfBuilding-PoE2-master <c>src/Data/Skills/*.lua</c>), which
/// the pinned RePoE catalog does not carry: the attack damage multiplier ("X% of base weapon damage"),
/// the attack-speed multiplier, per-level crit chance/cooldown, gem quality stats and every effect's
/// per-level values (e.g. Flame Wall's projectile added damage).</summary>
public sealed record SkillData(string Id, string Name, decimal? CastTime, decimal? Cooldown,
    IReadOnlyList<(string Stat, decimal Value)> QualityStats,
    IReadOnlyDictionary<string, SkillLevelValues> Levels,
    IReadOnlyList<SkillEffect> Effects)
{
    public SkillLevelValues? Level(int level)
    {
        for (int candidate = Math.Max(1, level); candidate >= 1; candidate--)
            if (Levels.TryGetValue(candidate.ToString(), out var values)) return values;
        return Levels.Count == 0 ? null : Levels.Values.First();
    }

    /// <summary>Named field of a level row (e.g. "baseMultiplier"), searched downwards like PoB2.</summary>
    public decimal? Number(string key, int level)
    {
        for (int candidate = Math.Max(1, level); candidate >= 1; candidate--)
            if (Levels.TryGetValue(candidate.ToString(), out var values) && values.Number(key) is decimal value) return value;
        return null;
    }

    /// <summary>The effect variant whose stat list contains the requested id (e.g. Flame Wall's
    /// "Projectile Damage" effect carries flame_wall_minimum_added_fire_damage).</summary>
    public SkillEffect? EffectWithStat(string statId) =>
        Effects.FirstOrDefault(e => e.Stats.Contains(statId, StringComparer.Ordinal));

    /// <summary>Positional stat of an effect at a level (the value order follows the effect's stat list).</summary>
    public static decimal? Positional(SkillEffect? effect, string statId, int level)
    {
        if (effect is null) return null;
        int index = Array.IndexOf(effect.Stats, statId);
        if (index < 0) return null;
        if (!effect.Levels.TryGetValue(Math.Max(1, level).ToString(), out var values))
        {
            var best = effect.Levels.OrderBy(kv => Math.Abs(int.Parse(kv.Key) - level)).FirstOrDefault();
            if (best.Value is null) return null;
            values = best.Value;
        }
        return index < values.Positions.Count ? values.Positions[index] : null;
    }
}

/// <summary>Loaded <c>skilldata.json</c>: PoB2's skills by granted-effect id plus the gem-item id →
/// granted-effect join from PoB2's <c>Data/Gems.lua</c>.</summary>
public sealed class SkillDataIndex
{
    public const string Sha256 = "861d68094390226bcdfb7d25435d8f1c299780993b41aac3b0a7431cef2f8fb0";
    public static readonly SkillDataIndex Empty = new(new Dictionary<string, SkillData>(), new Dictionary<string, string>(), new Dictionary<string, SkillStatSpec[]>());

    private readonly IReadOnlyDictionary<string, SkillData> _byEffectId;
    private readonly IReadOnlyDictionary<string, string> _gemToEffect;
    private readonly IReadOnlyDictionary<string, SkillStatSpec[]> _skillStats;

    private SkillDataIndex(IReadOnlyDictionary<string, SkillData> byEffectId, IReadOnlyDictionary<string, string> gemToEffect,
        IReadOnlyDictionary<string, SkillStatSpec[]> skillStats)
    {
        _byEffectId = byEffectId; _gemToEffect = gemToEffect; _skillStats = skillStats;
    }

    /// <summary>PoB2's global skill-stat map (Data/SkillStatMap.lua): the mods shared by every skill, e.g.
    /// <c>attacks_roll_crits_twice</c> → flag("BifurcateCrit"). PoB2 looks a stat id up in the skill's own
    /// statMap first and falls back to this table, so the calculator does the same.</summary>
    public IReadOnlyDictionary<string, SkillStatSpec[]> SkillStats => _skillStats;

    public IReadOnlyList<SkillStatSpec> SpecsFor(string statId, SkillEffect? effect) =>
        effect is not null && effect.StatMap.TryGetValue(statId, out var own) ? own
        : _skillStats.TryGetValue(statId, out var global) ? global : [];

    public IReadOnlyDictionary<string, SkillData> ByEffectId => _byEffectId;
    public int Count => _byEffectId.Count;

    /// <summary>Skill data for a gem item id (the join PoB2 itself uses: Gems.lua gameId → grantedEffectId).</summary>
    public SkillData? ForGem(string? gemId) =>
        gemId is not null && _gemToEffect.TryGetValue(gemId, out var effect) && _byEffectId.TryGetValue(effect, out var skill) ? skill : null;

    public SkillData? ForEffect(string effectId) => _byEffectId.GetValueOrDefault(effectId);

    /// <summary>Loads and verifies the pinned file. A missing file is an empty index (a build can run
    /// without it); a checksum mismatch always fails closed.</summary>
    public static SkillDataIndex Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        var bytes = File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Skill data checksum mismatch.");
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        var byEffect = new Dictionary<string, SkillData>(StringComparer.Ordinal);
        if (root.TryGetProperty("skills", out var skills))
        {
            foreach (var property in skills.EnumerateObject())
            {
                var value = property.Value;
                var effects = new List<SkillEffect>();
                if (value.TryGetProperty("variants", out var variants))
                {
                    foreach (var variant in variants.EnumerateObject())
                    {
                        var stats = variant.Value.TryGetProperty("stats", out var statsEl) && statsEl.ValueKind == JsonValueKind.Array
                            ? statsEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : [];
                        effects.Add(new(variant.Value.TryGetProperty("label", out var label) ? label.GetString() ?? "" : "",
                            stats, ReadLevels(variant.Value), ReadStatMap(variant.Value), ReadConstants(variant.Value)));
                    }
                }
                byEffect[property.Name] = new(property.Name,
                    value.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    value.TryGetProperty("castTime", out var cast) && cast.ValueKind == JsonValueKind.Number ? cast.GetDecimal() : null,
                    value.TryGetProperty("cooldown", out var cd) && cd.ValueKind == JsonValueKind.Number ? cd.GetDecimal() : null,
                    ReadQualityStats(value), ReadLevels(value), effects);
            }
        }
        var gemToEffect = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("gems", out var gems))
            foreach (var gem in gems.EnumerateObject())
                if (gem.Value.TryGetProperty("grantedEffectId", out var effect) && effect.GetString() is string effectId)
                    gemToEffect[gem.Name] = effectId;
        var skillStats = new Dictionary<string, SkillStatSpec[]>(StringComparer.Ordinal);
        if (root.TryGetProperty("skillStats", out var skillStatsElement))
            foreach (var entry in skillStatsElement.EnumerateObject())
            {
                var specs = ReadSpecs(entry.Value);
                if (specs.Count > 0) skillStats[entry.Name] = [.. specs];
            }
        return new(byEffect, gemToEffect, skillStats);
    }

    /// <summary>PoB2's <c>constantStats</c> of one effect: values that do not scale with level. They are added to
    /// the mod's value exactly like a per-level value (e.g. Blazing Critical's 15% extra Fire damage).</summary>
    private static IReadOnlyDictionary<string, decimal> ReadConstants(JsonElement container)
    {
        var map = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (!container.TryGetProperty("constants", out var constants) || constants.ValueKind != JsonValueKind.Object) return map;
        foreach (var entry in constants.EnumerateObject())
            if (entry.Value.ValueKind == JsonValueKind.Number) map[entry.Name] = entry.Value.GetDecimal();
        return map;
    }

    private static IReadOnlyDictionary<string, SkillStatSpec[]> ReadStatMap(JsonElement container)
    {
        var map = new Dictionary<string, SkillStatSpec[]>(StringComparer.Ordinal);
        if (!container.TryGetProperty("statMap", out var statMap) || statMap.ValueKind != JsonValueKind.Object) return map;
        foreach (var entry in statMap.EnumerateObject())
        {
            var specs = ReadSpecs(entry.Value);
            if (specs.Count > 0) map[entry.Name] = [.. specs];
        }
        return map;
    }

    private static List<SkillStatSpec> ReadSpecs(JsonElement value)
    {
        var specs = new List<SkillStatSpec>();
        if (value.ValueKind != JsonValueKind.Array) return specs;
        foreach (var spec in value.EnumerateArray())
        {
            string text = spec.TryGetProperty("spec", out var s) ? s.GetString() ?? "" : "";
            if (text.Length == 0) continue;
            decimal? mult = spec.TryGetProperty("mult", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDecimal() : null;
            specs.Add(new(text, mult));
        }
        return specs;
    }

    private static IReadOnlyList<(string Stat, decimal Value)> ReadQualityStats(JsonElement skill)
    {
        var list = new List<(string, decimal)>();
        if (!skill.TryGetProperty("qualityStats", out var quality) || quality.ValueKind != JsonValueKind.Array) return list;
        foreach (var entry in quality.EnumerateArray())
            if (entry.TryGetProperty("stat", out var stat) && entry.TryGetProperty("value", out var value) &&
                stat.GetString() is string id && value.ValueKind == JsonValueKind.Number)
                list.Add((id, value.GetDecimal()));
        return list;
    }

    private static IReadOnlyDictionary<string, SkillLevelValues> ReadLevels(JsonElement container)
    {
        var levels = new Dictionary<string, SkillLevelValues>(StringComparer.Ordinal);
        if (!container.TryGetProperty("levels", out var levelsEl) || levelsEl.ValueKind != JsonValueKind.Object) return levels;
        foreach (var row in levelsEl.EnumerateObject())
        {
            var positions = new List<decimal>();
            var named = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var field in row.Value.EnumerateObject())
            {
                if (field.NameEquals("values") && field.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in field.Value.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Number) positions.Add(item.GetDecimal());
                }
                else if (field.Value.ValueKind == JsonValueKind.Number) named[field.Name] = field.Value.GetDecimal();
                else if (field.NameEquals("cost") && field.Value.ValueKind == JsonValueKind.Object)
                    foreach (var costField in field.Value.EnumerateObject())
                        if (costField.Value.ValueKind == JsonValueKind.Number) named["cost:" + costField.Name] = costField.Value.GetDecimal();
            }
            levels[row.Name] = new(positions, named);
        }
        return levels;
    }
}
