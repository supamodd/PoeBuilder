using System.Text.RegularExpressions;

namespace PoeBuilder.Core.Calculation;

/// <summary>The PoB2 conditions this model can evaluate when a mod spec is conditional (a build's config
/// supplies the flags, the pools supply the life state). A condition outside this set is not assumed:
/// the spec is catalogued instead of applied.</summary>
public sealed record ModConditions
{
    public static readonly ModConditions None = new();
    public bool LowLife { get; init; }
    public bool FullLife { get; init; }
    public bool Moving { get; init; }
    public bool BeenHitRecently { get; init; }
    public bool CritRecently { get; init; }
    public bool EnemyIgnited { get; init; }
    public bool EnemyChilled { get; init; }
    public bool EnemyShocked { get; init; }

    /// <summary>True when every named condition is known and currently met; null when any is unknown.</summary>
    public bool? Evaluate(IEnumerable<string> conditionVars)
    {
        foreach (string raw in conditionVars)
        {
            bool? value = raw switch
            {
                "LowLife" => LowLife,
                "FullLife" => FullLife,
                "Moving" => Moving,
                "BeenHitRecently" => BeenHitRecently,
                "CritRecently" => CritRecently,
                "Ignited" => EnemyIgnited,
                "Chilled" => EnemyChilled,
                "Shocked" => EnemyShocked,
                _ => null
            };
            if (value is null) return null;
            if (!value.Value) return false;
        }
        return true;
    }
}

/// <summary>A support-scoped accumulation of the modifiers PoB2's own statMap entries produce. The
/// calculator folds it into the supported skill exactly where PoB2 would: MORE/INC on the damage, on the
/// attack/cast rate and on the critical strike chance and multiplier.</summary>
public sealed class SupportModSink
{
    public decimal DamageMore = 1m, DamageInc;
    public decimal RateMore = 1m, RateInc;
    public decimal CritChanceMore = 1m, CritChanceInc;
    public decimal CritMultiplierMore = 1m, CritMultiplierInc;
    public readonly Dictionary<string, decimal> TypeMore = new(StringComparer.Ordinal);
    public readonly Dictionary<string, decimal> TypeInc = new(StringComparer.Ordinal);
    public readonly Dictionary<string, decimal> GainAs = new(StringComparer.Ordinal);
    public readonly SortedDictionary<string, int> Unhandled = new(StringComparer.Ordinal);
    public decimal LifeRegenPercent, ManaRegenInc, AccuracyInc;
    public decimal? CritChanceCap;
    /// <summary>Rakiata's Flow: "treats enemy monster elemental resistance values as inverted" — PoB2's
    /// HitsInvertEleResChance, a chance in 0..1 (CalcOffence.lua:4179-4201 folds it into the effective
    /// resistance: resist x (1 - chance) + (-resist) x chance).</summary>
    public decimal InvertElementalResistChance;
    /// <summary>"…will Bifurcate Critical Hits" (Garukhan's Resolve) as PoB2's BifurcateCrit flag: the
    /// critical hit chance is rolled twice and the multiplier picks up 2 × pre/post (CalcOffence.lua:3736).</summary>
    public bool CritBifurcates;

    public decimal TypeMoreFor(string type) => TypeMore.TryGetValue(type, out var v) ? v : 1m;
    public decimal TypeIncFor(string type) => TypeInc.TryGetValue(type, out var v) ? v : 0m;

    /// <summary>Applies one parsed PoB2 mod spec with the stat's own value (already scaled by mult).
    /// Returns true when this model implements the mod; false means the caller keeps its own fallback
    /// (so nothing that used to be applied is silently dropped).</summary>
    public bool Apply(PoB2ModTranslator.Spec spec, decimal value, bool isAttack, IReadOnlyCollection<string> gemTags,
        ModConditions? conditions = null)
    {
        decimal factor = 1 + value / 100m;
        string name = spec.Name;
        // A conditional spec is applied only when every condition it names is known to hold for this build
        // (the config and the pools supply them); otherwise it is catalogued, never assumed.
        bool conditional = spec.ConditionVars.Length > 0;
        if (conditional)
        {
            var known = conditions ?? ModConditions.None;
            foreach (string rawCondition in spec.ConditionVars)
            {
                int colon = rawCondition.IndexOf(':');
                bool isFlag = rawCondition.StartsWith("ModFlag.", StringComparison.Ordinal);
                string kind = colon > 0 ? rawCondition[..colon] : isFlag ? "ModFlag" : "";
                string symbol = colon > 0 ? rawCondition[(colon + 1)..] : isFlag ? rawCondition["ModFlag.".Length..] : rawCondition;
                if (kind.StartsWith("ModFlag", StringComparison.Ordinal))
                {
                    // ModFlag describes the skill's shape (a hit, an attack, a spell); our model computes
                    // hits, so Hit/Melee/Projectile hold and Attack/Spell are gated here.
                    if (symbol == "Attack" && !isAttack) goto unhandled;
                    if (symbol == "Spell" && isAttack) goto unhandled;
                    continue;
                }
                if (known.Evaluate([symbol]) != true) goto unhandled;
            }
        }
        switch (name)
        {
            case "Damage":
                if (spec.Type == "MORE") { DamageMore *= factor; return true; }
                if (spec.Type == "INC") { DamageInc += value; return true; }
                break;
            case "PhysicalDamage": case "FireDamage": case "ColdDamage": case "LightningDamage": case "ChaosDamage": case "ElementalDamage":
            {
                var types = name == "ElementalDamage"
                    ? new[] { "fire", "cold", "lightning" }
                    : [name.Replace("Damage", "").ToLowerInvariant()];
                foreach (string type in types)
                {
                    if (spec.Type == "MORE") TypeMore[type] = (TypeMore.TryGetValue(type, out var old) ? old : 1m) * factor;
                    else if (spec.Type == "INC") TypeInc[type] = (TypeInc.TryGetValue(type, out var inc) ? inc : 0m) + value;
                    else goto unhandled;
                }
                return true;
            }
            case "Speed":
                if (spec.Type == "MORE") { RateMore *= factor; return true; }
                if (spec.Type == "INC") { RateInc += value; return true; }
                break;
            case "CritChance":
                if (spec.Type == "MORE") { CritChanceMore *= factor; return true; }
                if (spec.Type == "INC") { CritChanceInc += value; return true; }
                break;
            case "CritMultiplier":
                if (spec.Type == "MORE") { CritMultiplierMore *= factor; return true; }
                if (spec.Type == "INC") { CritMultiplierInc += value; return true; }
                break;
            case "DamageGainAsFire": case "DamageGainAsCold": case "DamageGainAsLightning": case "DamageGainAsChaos":
                if (spec.Type != "BASE") break;
                GainAs[name.Replace("DamageGainAs", "").ToLowerInvariant()] = value;
                return true;
            case "LifeRegenPercent":
                if (spec.Type == "BASE") { LifeRegenPercent += value; return true; }
                break;
            case "ManaRegen":
                if (spec.Type == "INC") { ManaRegenInc += value; return true; }
                break;
            case "Accuracy":
                if (spec.Type == "INC") { AccuracyInc += value; return true; }
                break;
            case "CritChanceCap":
                if (spec.Type == "OVERRIDE") { CritChanceCap = value; return true; }
                break;
            case "BifurcateCrit":
                CritBifurcates = true;
                return true;
            case "HitsInvertEleResChance":
                InvertElementalResistChance = Math.Clamp(value / 100m, 0, 1);
                return true;
            default:
                break;
        }
    unhandled:
        string key = name + "/" + spec.Type + (conditional ? " (" + string.Join("+", spec.ConditionVars) + ")" : "");
        Unhandled[key] = Unhandled.TryGetValue(key, out var n) ? n + 1 : 1;
        return false;
    }
}

/// <summary>Parses the mod specs PoB2 ships in its per-skill <c>statMap</c> tables (Data/Skills/*.lua) into
/// name/type/value/condition parts. The vocabulary is PoB2's own (Modules/ModParser.lua and the mod names
/// its calculator consumes); a spec the calculator cannot parse is reported by the caller, never guessed.</summary>
public static class PoB2ModTranslator
{
    public sealed record Spec(string Name, string Type, decimal? Literal, string[] ConditionVars, string Raw);

    private static readonly Regex ModCall = new(@"mod\(\s*""([^""]+)""\s*,\s*""([A-Z]+)""\s*,\s*(nil|-?[0-9.]+)?", RegexOptions.Compiled);
    private static readonly Regex FlagCall = new(@"flag\(\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex ConditionKind = new(@"type\s*=\s*""(Condition|ActorCondition|Multiplier|MultiplierThreshold|SkillType|StatCondition|GlobalEffect)""", RegexOptions.Compiled);
    private static readonly Regex ConditionName = new(@"(?:var|stat|skillType)\s*=\s*""([^""]+)""", RegexOptions.Compiled);

    /// <summary>Parses one raw spec. Returns null when the shape is not a mod call (e.g. a flag).</summary>
    public static Spec? Parse(string raw)
    {
        var flag = FlagCall.Match(raw);
        if (flag.Success) return new Spec(flag.Groups[1].Value, "FLAG", null, [], raw);
        var match = ModCall.Match(raw);
        if (!match.Success) return null;
        decimal? literal = match.Groups[3].Success && match.Groups[3].Value != "nil"
            ? decimal.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
        var conditions = new List<string>();
        foreach (var condition in ConditionKind.Matches(raw).Cast<Match>())
        {
            var name = ConditionName.Match(raw, condition.Index + condition.Length);
            if (name.Success) conditions.Add(condition.Groups[1].Value + ":" + name.Groups[1].Value);
        }
        foreach (var kv in LiteralMods(raw)) conditions.Add(kv);
        return new Spec(match.Groups[1].Value, match.Groups[2].Value, literal, [.. conditions], raw);
    }

    /// <summary>PoB2 writes a few conditions as bare mod names inside the spec rather than as a table
    /// tag (e.g. ModFlag.Hit), so they are collected here as literal markers.</summary>
    private static IEnumerable<string> LiteralMods(string raw)
    {
        foreach (var marker in new[] { "ModFlag.Hit", "ModFlag.Attack", "ModFlag.Melee", "ModFlag.Projectile", "ModFlag.Spell" })
            if (raw.Contains(marker, StringComparison.Ordinal)) yield return marker;
    }
}
