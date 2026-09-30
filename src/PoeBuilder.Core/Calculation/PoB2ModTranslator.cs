using System.Text.RegularExpressions;

namespace PoeBuilder.Core.Calculation;

/// <summary>The PoB2 conditions this model can evaluate when a mod spec is conditional (a build's config
/// supplies the flags, the pools supply the life state). A condition outside this set is not assumed:
/// the spec is catalogued instead of applied.</summary>
public sealed record ModConditions
{
    public static readonly ModConditions None = new();
    /// <summary>The two life states. NULL means "not known yet" — which is exactly the situation of PoB2's
    /// first CalcSetup stage, where the player's mods are collected before the pools exist. A condition that
    /// reads an unknown state is not answered with <c>false</c> (that would silently drop the mod) but left
    /// unresolved, so the buff pass can defer it to the second stage: pools first, then the condition is
    /// re-evaluated with the real value (Modules/CalcSetup.lua keeps the mod list and re-evaluates it).</summary>
    public bool? LowLife { get; init; }
    public bool? FullLife { get; init; }
    public bool Moving { get; init; }
    public bool BeenHitRecently { get; init; }
    public bool CritRecently { get; init; }
    public bool EnemyIgnited { get; init; }
    public bool EnemyChilled { get; init; }
    public bool EnemyShocked { get; init; }
    /// <summary>PoB2's <c>conditionEnemyLowLife</c> ("Is the enemy on Low Life?"). Unknown (null) unless the
    /// build's config states it, because a spec conditioned on the ENEMY's life must never be answered with
    /// the player's own life state.</summary>
    public bool? EnemyLowLife { get; init; }
    /// <summary>PoB2's <c>CannotConsumeCharges</c> flag. Nothing in this engine sets it yet (the pinned stat
    /// table has no wording for it and the fixtures carry no such mod), so it stays false — which is what the
    /// "not prevented from consuming charges" gate on Barrage's per-charge repeats needs. A build that does
    /// carry the mod would have to wire it here.</summary>
    public bool CannotConsumeCharges { get; init; }

    /// <summary>True when every named condition is known and currently met; null when any is unknown. A symbol
    /// prefixed with '!' is inverted, which is how PoB2's <c>neg = true</c> tags are carried.</summary>
    public bool? Evaluate(IEnumerable<string> conditionVars)
    {
        foreach (string raw in conditionVars)
        {
            bool negated = raw.StartsWith('!');
            string symbol = negated ? raw[1..] : raw;
            bool? value = symbol switch
            {
                "LowLife" => LowLife,
                "FullLife" => FullLife,
                "Moving" => Moving,
                "BeenHitRecently" => BeenHitRecently,
                "CritRecently" => CritRecently,
                "Ignited" => EnemyIgnited,
                "Chilled" => EnemyChilled,
                "Shocked" => EnemyShocked,
                "CannotConsumeCharges" => CannotConsumeCharges,
                "enemy.LowLife" => EnemyLowLife,
                _ => null
            };
            if (negated && value is not null) value = !value;
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
    /// <summary>Damage mods gated on ModFlag.Attack: attacks read them, spells never do.</summary>
    public decimal AttackDamageMore = 1m, AttackDamageInc;
    public decimal RateMore = 1m, RateInc;
    public decimal ArmourMore = 1m, ArmourInc;
    public decimal EvasionMore = 1m, EvasionInc;
    public decimal EnergyShieldMore = 1m, EnergyShieldInc;
    /// <summary>"N% increased Rage effect" (Berserk) and "+N to Maximum Rage", both from buffs.</summary>
    public decimal RageEffectInc, MaximumRageFlat;
    /// <summary>Barrage: how many times the supported skill repeats, and the damage penalty for the repeats.</summary>
    public decimal BarrageRepeats;
    public decimal BarrageRepeatDamageMore = 1m;
    public decimal CritChanceMore = 1m, CritChanceInc;
    public decimal CritMultiplierMore = 1m, CritMultiplierInc;
    public readonly Dictionary<string, decimal> TypeMore = new(StringComparer.Ordinal);
    public readonly Dictionary<string, decimal> TypeInc = new(StringComparer.Ordinal);
    public readonly Dictionary<string, decimal> GainAs = new(StringComparer.Ordinal);
    /// <summary>"Gain X% of Damage as Extra <type>" gated on ModFlag.Attack: attacks read it, spells do not.</summary>
    public readonly Dictionary<string, decimal> AttackOnlyGainAs = new(StringComparer.Ordinal);
    /// <summary>Whether a gain-as type came from an attack-only mod.</summary>
    public bool AttackOnly(string type) => AttackOnlyGainAs.ContainsKey(type);
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
        ModConditions? conditions = null) =>
        Apply(spec, value, isAttack, gemTags, conditions, out _);

    /// <summary>The same, reporting WHY a spec was not applied: <paramref name="unknownCondition"/> is set when
    /// the only obstacle is a condition this model cannot answer YET (an unknown life state). That is what the
    /// two-stage pass defers to the moment the pools exist, instead of losing the modifier.
    /// A pure <c>false</c> return with this flag clear means the mod itself is not modelled.</summary>
    public bool Apply(PoB2ModTranslator.Spec spec, decimal value, bool isAttack, IReadOnlyCollection<string> gemTags,
        ModConditions? conditions, out bool unknownCondition)
    {
        unknownCondition = false;
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
                if (known.Evaluate([symbol]) != true)
                {
                    // Distinguish "the condition is false" from "the condition cannot be answered yet":
                    // only the second one is worth deferring to the pass that runs after the pools.
                    if (known.Evaluate([symbol]) is null) unknownCondition = true;
                    goto unhandled;
                }
            }
        }
        switch (name)
        {
            case "Damage":
            {
                // A damage mod gated on ModFlag.Attack reaches ATTACKS only (Direstrike II's "+70% increased
                // Attack Damage while on Low Life"), so it must not land in the general pool a spell reads.
                bool attackOnly = spec.ConditionVars.Contains("ModFlag.Attack", StringComparer.Ordinal);
                if (spec.Type == "MORE")
                {
                    if (attackOnly) AttackDamageMore *= factor; else DamageMore *= factor;
                    return true;
                }
                if (spec.Type == "INC")
                {
                    if (attackOnly) AttackDamageInc += value; else DamageInc += value;
                    return true;
                }
                break;
            }
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
            // Berserk's "N% increased Rage effect" scales the rage count into PoB2's RageEffect
            // (Modules/CalcPerform.lua:782: floor(stacks × (1 + RageEffect INC / 100))).
            case "RageEffect":
                if (spec.Type == "INC") { RageEffectInc += value; return true; }
                break;
            // Barrage's own mods (Data/Skills/act_dex.lua:216-230): the repeat COUNT and the damage penalty applied
            // to repeated projectiles. PoB2 turns them into one DPS multiplier (CalcOffence.lua:962-966):
            // dpsMulti = (1 + BarrageRepeats) x BarrageRepeatDamage, then NewMod("DPS", "MORE", dpsMulti).
            case "BarrageRepeats":
                if (spec.Type == "BASE") { BarrageRepeats += value; return true; }
                break;
            case "BarrageRepeatDamage":
                if (spec.Type == "MORE") { BarrageRepeatDamageMore *= factor; return true; }
                break;
            case "MaximumRage":
                if (spec.Type == "BASE") { MaximumRageFlat += value; return true; }
                break;
            case "DamageGainAsFire": case "DamageGainAsCold": case "DamageGainAsLightning": case "DamageGainAsChaos":
                if (spec.Type != "BASE") break;
                string gainType = name.Replace("DamageGainAs", "").ToLowerInvariant();
                // A gain gated on ModFlag.Attack reaches attacks only (Blazing Critical's "imbue all of your
                // Attacks with Fire damage"), so it is kept apart from the pool a spell also reads.
                var gainStore = spec.ConditionVars.Contains("ModFlag.Attack", StringComparer.Ordinal) ? AttackOnlyGainAs : GainAs;
                gainStore[gainType] = gainStore.TryGetValue(gainType, out var gainOld) ? gainOld + value : value;
                return true;
            // Defence mods a buff can carry (Charge Infusion's "…+% final with Endurance Charges").
            case "Armour":
                if (spec.Type == "MORE") { ArmourMore *= factor; return true; }
                if (spec.Type == "INC") { ArmourInc += value; return true; }
                break;
            case "Evasion":
                if (spec.Type == "MORE") { EvasionMore *= factor; return true; }
                if (spec.Type == "INC") { EvasionInc += value; return true; }
                break;
            case "EnergyShield":
                if (spec.Type == "MORE") { EnergyShieldMore *= factor; return true; }
                if (spec.Type == "INC") { EnergyShieldInc += value; return true; }
                break;
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

    /// <summary>Parses EVERY mod call of one raw statMap entry. PoB2 routinely puts several calls in a single
    /// string when a stat feeds more than one mod (Elemental Conflux's damage becomes three calls — one per
    /// element; Charge Regulation's speed becomes Speed + WarcrySpeed + TotemPlacementSpeed), and each call
    /// carries its OWN tags, so they have to be evaluated separately. Each returned spec's <c>Raw</c> is the
    /// single call, which is what the condition/multiplier readers work on.</summary>
    public static IReadOnlyList<Spec> ParseAll(string raw)
    {
        var specs = new List<Spec>();
        int index = 0;
        while (index < raw.Length)
        {
            int nextMod = raw.IndexOf("mod(", index, StringComparison.Ordinal);
            int nextFlag = raw.IndexOf("flag(", index, StringComparison.Ordinal);
            int at = nextMod < 0 ? nextFlag : nextFlag < 0 ? nextMod : Math.Min(nextMod, nextFlag);
            if (at < 0) break;
            int end = EndOfCall(raw, at);
            string call = end < 0 ? raw[at..] : raw[at..(end + 1)];
            if (Parse(call) is { } spec) specs.Add(spec);
            if (end < 0) break;
            index = end + 1;
        }
        return specs;
    }

    /// <summary>Index of the closing parenthesis of the call that starts at <paramref name="start"/> (the
    /// opening one), counting nested brackets so a call whose tags contain a table — or even another call, as
    /// PoB2's ExtraAura specs do — is not cut in half.</summary>
    private static int EndOfCall(string raw, int start)
    {
        int depth = 0;
        for (int i = start; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>Parses one raw spec. Returns null when the shape is not a mod call (e.g. a flag).</summary>
    public static Spec? Parse(string raw)
    {
        // The flag is only this spec's shape when it is the FIRST call: PoB2 writes "mod(...), flag(...)" in
        // one string when a stat both grants a modifier and sets a flag (Barrage's repeats do exactly that),
        // and returning the flag for those dropped the modifier — BarrageRepeats never reached the bucket.
        int firstMod = raw.IndexOf("mod(", StringComparison.Ordinal);
        var flag = FlagCall.Match(raw);
        if (flag.Success && (firstMod < 0 || flag.Index < firstMod))
            return new Spec(flag.Groups[1].Value, "FLAG", null, [], raw);
        var match = ModCall.Match(raw);
        if (!match.Success) return null;
        decimal? literal = match.Groups[3].Success && match.Groups[3].Value != "nil"
            ? decimal.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
        // One statMap entry can carry SEVERAL mod calls in one string (PoB2 does that when a stat feeds more
        // than one mod, e.g. Charge Regulation's speed -> Speed + WarcrySpeed + TotemPlacementSpeed). Only the
        // first call is parsed here, so the condition scan must stop at the next call: otherwise the
        // "stat = ..." of a LATER call would be read as a condition of this one and the mod would be dropped
        // as unresolvable.
        int nextCall = raw.IndexOf("), mod(", match.Index, StringComparison.Ordinal);
        string call = nextCall < 0 ? raw : raw[match.Index..(nextCall + 1)];
        var conditions = new List<string>();
        foreach (var condition in ConditionKind.Matches(call).Cast<Match>())
        {
            // The name must come from the SAME tag as the kind: PoB2 writes "{ type = GlobalEffect, effectType =
            // "Buff", effectName = "X" }, { type = Multiplier, var = "Y" }" in one call, and searching the rest
            // of the call would read Y as the GlobalEffect's own variable — which then looks like an unknown
            // condition and silently drops the mod (Elemental Conflux's per-element damage did exactly this).
            int tagEnd = call.IndexOf('}', condition.Index);
            int window = tagEnd < 0 ? call.Length : tagEnd;
            var name = ConditionName.Match(call, condition.Index + condition.Length, window - condition.Index - condition.Length);
            if (!name.Success) continue;
            // An ActorCondition can name the ENEMY as its actor ("{ type = ActorCondition, actor = enemy,
            // var = LowLife }") — that is the enemy's own life state, not the player's, so the actor is
            // carried into the condition symbol and evaluated separately.
            int scope = Math.Min(window, name.Index + 80);
            string tagWindow = call[condition.Index..scope];
            string actor = tagWindow.Contains("actor = \"enemy\"", StringComparison.Ordinal) ? "enemy." : "";
            // "neg = true" inverts the condition (Barrage's per-charge repeats require that the build is NOT
            // prevented from consuming charges), so the negation travels in the symbol and Evaluate() applies it.
            string negated = tagWindow.Contains("neg = true", StringComparison.Ordinal) ? "!" : "";
            conditions.Add(condition.Groups[1].Value + ":" + actor + negated + name.Groups[1].Value);
        }
        foreach (var kv in LiteralMods(call)) conditions.Add(kv);
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
