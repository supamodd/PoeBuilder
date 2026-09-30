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

/// <summary>One GlobalEffect modifier of a persistent buff whose condition was not resolvable in the first
/// pass (the life states are unknown until the pools exist). The second pass re-reads the buff's own statMap
/// and applies the modifier only if the condition holds then — PoB2's CalcSetup adds the player's buffs,
/// computes the pools, and re-evaluates the conditions against them.</summary>
public sealed record DeferredBuff(string Name, string EffectId, int Level, decimal Quality, string StatId,
    decimal Value, string NoteKey);

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
            foreach (var statId in StatIds(effect))
                if (IsGlobal(catalog, effect, statId)) return true;
        return false;
    }

    /// <summary>Every stat id an effect can produce: its ordered <c>stats</c> list (which positions the level
    /// values), its <c>statMap</c> keys and its <c>constantStats</c> keys. PoB2 writes an id in only one of those
    /// places depending on how the value is defined — Blazing Critical's extra-fire id, for instance, has no
    /// level row at all (only a constant plus a statMap entry) — so all three have to be read.</summary>
    public static IEnumerable<string> StatIds(SkillEffect effect)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in effect.Stats) if (seen.Add(id)) yield return id;
        foreach (var id in effect.StatMap.Keys) if (seen.Add(id)) yield return id;
        foreach (var id in effect.Constants.Keys) if (seen.Add(id)) yield return id;
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

    /// <summary>PoB2's <c>{ type = "StatThreshold", stat = "FrenzyCharges", threshold = 1 }</c> tag: the mod only
    /// applies when that stat is at least the threshold. Charge counts come from the imported config, so a
    /// threshold on them is answerable; anything else is unknown and the mod is catalogued instead of assumed.</summary>
    private static readonly Regex StatThresholdTag = new(
        @"type = ""StatThreshold"",\s*stat = ""(\w+)"",\s*threshold = (-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>PoB2's <c>{ type = "Multiplier", var = "TotalCharges" }</c> tag: the mod's value is multiplied by
    /// that stat (CalcTools/CalcSetup evaluate the multiplier for every mod).</summary>
    private static readonly Regex MultiplierTag = new(
        @"type = ""Multiplier"",\s*var = ""(\w+)""(?:,\s*div = (-?\d+(?:\.\d+)?))?(?:,\s*invert = true)?", RegexOptions.Compiled);

    /// <summary><c>{ type = "MultiplierThreshold", var = "ResonanceCount", threshold = 250 }</c> — the mod only
    /// applies once that multiplier reaches the threshold.</summary>
    private static readonly Regex MultiplierThresholdTag = new(
        @"type = ""MultiplierThreshold"",\s*var = ""(\w+)"",\s*threshold = (-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>The condition a spec is gated behind: true (met), false (switched off) or null (a condition
    /// this model cannot resolve, including enemy-actor criteria — the offence pipeline owns those).</summary>
    private static bool? ConditionOf(string raw, BuildConditions? conditions, StatBucket bucket)
    {
        if (raw.Contains("actor = \"enemy\"", StringComparison.Ordinal)) return null;
        // Charge gates are answerable because the imported config carries the counts.
        if (StatThresholdTag.Match(raw) is { Success: true } threshold &&
            ChargeCount(threshold.Groups[1].Value, conditions, bucket) is decimal have)
        {
            double need = double.Parse(threshold.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            return have >= (decimal)need;
        }
        // "{ type = MultiplierThreshold, var = ResonanceCount, threshold = 250 }": Trinity's speed bonus only
        // exists once the resonance reaches the threshold (ConfigOptions.lua:673 sets the count).
        if (MultiplierThresholdTag.Match(raw) is { Success: true } thresholdTag &&
            MultiplierValue(thresholdTag.Groups[1].Value, conditions, bucket) is decimal multiplier)
        {
            double need = double.Parse(thresholdTag.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            return multiplier >= (decimal)need;
        }
        var match = ConditionTag.Match(raw);
        if (!match.Success) return true;
        if (conditions is null) return null;
        return match.Groups[1].Value switch
        {
            "FlameWallAddedDamage" => conditions.FlameWallAddedDamage,
            // "Lightning Infused?" is only offered once "Projectile Travelled through?" is on
            // (ConfigOptions.lua:382, ifOption = flameWallAddedDamage), so both must hold.
            "FlameWallInfused" => conditions.FlameWallAddedDamage && conditions.FlameWallInfused,
            "Moving" => conditions.Moving,
            "CritRecently" => conditions.CritRecently,
            "BeenHitRecently" => conditions.BeenHitRecently,
            _ => null
        };
    }

    /// <summary>The calc-side condition record for the flags the imported config states. The life states are left
    /// unknown here on purpose: the aura pass runs before the pools, so a life-conditioned buff mod is catalogued
    /// rather than assumed to be on.</summary>
    private static ModConditions ToModConditions(BuildConditions? conditions) => conditions is null ? ModConditions.None : new ModConditions
    {
        Moving = conditions.Moving,
        BeenHitRecently = conditions.BeenHitRecently,
        CritRecently = conditions.CritRecently,
        EnemyIgnited = conditions.EnemyIgnited,
        EnemyChilled = conditions.EnemyChilled,
        EnemyShocked = conditions.EnemyShocked
    };

    /// <summary>The count a <c>StatThreshold</c> can compare against: the multiplier vocabulary (charges, rage,
    /// resonance) covers every threshold PoB2's buffs use here.</summary>
    private static decimal? ChargeCount(string stat, BuildConditions? conditions, StatBucket bucket) =>
        MultiplierValue(stat, conditions, bucket);

    /// <summary>PoB2's multiplier value of a stat name for the given build state, or null when this model has no
    /// value for it. Charge counts and Rage come from the imported config (Modules/CalcPerform.lua:777-791
    /// resolves Rage into a stack count and its effect), resonance from the Trinity config input, and the
    /// Elemental Conflux multipliers from the "Elemental Conflux Element" list (ConfigOptions.lua:389-407:
    /// Average = 3 for all three elements, a chosen element = 1 for it and 0 for the others).</summary>
    private static decimal? MultiplierValue(string stat, BuildConditions? conditions, StatBucket bucket) => stat switch
    {
        "FrenzyCharges" or "FrenzyCharge" => conditions?.FrenzyCharges ?? 0,
        "PowerCharges" or "PowerCharge" => conditions?.PowerCharges ?? 0,
        "EnduranceCharges" or "EnduranceCharge" => conditions?.EnduranceCharges ?? 0,
        "TotalCharges" => conditions?.TotalCharges ?? 0,
        "Rage" => conditions?.RageStacks ?? 0,
        "RageEffect" => bucket.RageEffectPct,
        // Barrage's per-frenzy-charge repeats read the removable frenzy charges (the config's own count) —
        // the same number the charge gates use.
        "RemovableFrenzyCharge" => conditions?.FrenzyCharges ?? 0,
        "ResonanceCount" => conditions?.ResonanceCount ?? 0,
        "ElementalConfluxLightningEffect" => ConfluxMultiplier(conditions, "lightning"),
        "ElementalConfluxColdEffect" => ConfluxMultiplier(conditions, "cold"),
        "ElementalConfluxFireEffect" => ConfluxMultiplier(conditions, "fire"),
        _ => null
    };

    /// <summary>The Elemental Conflux multiplier for one element: 3 while the config's element is "Average"
    /// (index 1 — the default), 1 for the chosen element, 0 for the other two.</summary>
    private static decimal ConfluxMultiplier(BuildConditions? conditions, string element)
    {
        int chosen = conditions?.ConfluxElement ?? 1;
        if (chosen == 1) return 3;
        return (chosen, element) switch
        {
            (2, "lightning") or (3, "cold") or (4, "fire") => 1,
            _ => 0
        };
    }

    /// <summary>The factor a <c>Multiplier</c> tag applies to a spec's value, exactly as PoB2 computes it
    /// (Classes/ModStore.lua:385-400): <c>mult = floor(base / div + 0.0001)</c>, and with <c>invert</c> the
    /// value is DIVIDED by that count (<c>mult = 1 / mult</c>), which is how Elemental Conflux turns its "75%
    /// more damage" into the average 25% per element. Returns null when the multiplier names something this
    /// model cannot size, so the caller reports the line instead of assuming 1.</summary>
    private static decimal? MultiplierOf(string raw, BuildConditions? conditions, StatBucket bucket)
    {
        var match = MultiplierTag.Match(raw);
        if (!match.Success) return 1m;
        if (MultiplierValue(match.Groups[1].Value, conditions, bucket) is not decimal baseValue) return null;
        decimal div = match.Groups[2].Success
            ? decimal.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 1m;
        decimal mult = Math.Floor(baseValue / div + 0.0001m);
        bool invert = match.Value.Contains("invert = true", StringComparison.Ordinal);
        if (invert && mult != 0) mult = 1m / mult;
        return mult;
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
                foreach (var statId in StatIds(effect))
                {
                    var specs = GlobalSpecs(catalog, effect, statId);
                    if (specs.Count == 0) continue;
                    bool unresolved = false;
                    decimal applicableFactor = 0m, unresolvedFactor = 0m;
                    foreach (var spec in specs)
                    {
                        // applyNotPlayer means the effect never reaches the character (PoB2 keeps it for
                        // minions/allies), so it is not a gap in this model.
                        if (spec.Spec.Contains("applyNotPlayer = true", StringComparison.Ordinal)) continue;
                        bool? holds = ConditionOf(spec.Spec, conditions, bucket);
                        // A Multiplier tag scales the mod's own value (PoB2's ModStore multiplies every mod by
                        // floor(value / div), inverted when the tag says so — Elemental Conflux's average case).
                        decimal? factor = MultiplierOf(spec.Spec, conditions, bucket);
                        if (factor is null) unresolved = true;
                        else if (holds is null)
                        {
                            // The condition is not answerable YET (a life state): keep the value so the second
                            // pass can apply it once the pools exist, instead of answering "false" and losing it.
                            unresolved = true;
                            unresolvedFactor = Math.Max(unresolvedFactor, factor.Value);
                        }
                        else if (holds == true) applicableFactor = Math.Max(applicableFactor, factor.Value);
                    }
                    if (applicableFactor <= 0m && unresolvedFactor <= 0m)
                    {
                        if (unresolved) bucket.Note("aura: " + instance.Name + " " + statId + " (condition not resolved)");
                        continue;
                    }
                    decimal? levelValue = SkillData.Positional(effect, statId, instance.Level);
                    decimal constantValue = effect.Constant(statId);
                    decimal qualityValue = 0;
                    foreach (var (stat, perQuality) in skill.QualityStats)
                        if (string.Equals(stat, statId, StringComparison.Ordinal) && instance.Quality > 0)
                            // PoB2 keeps the integral part only (math.modf of qualityStat × quality).
                            qualityValue += Math.Truncate(perQuality * instance.Quality);
                    if (levelValue is null && qualityValue == 0 && constantValue == 0) continue;
                    decimal value = (levelValue ?? 0) + constantValue + qualityValue;
                    if (applicableFactor <= 0m)
                    {
                        // Deferred: the whole value belongs to the second pass, and the note the first pass
                        // leaves behind is exactly what that pass removes once it applies the modifier.
                        string noteKey = "aura: " + instance.Name + " " + statId + " (condition not resolved)";
                        bucket.Note(noteKey);
                        bucket.DeferredBuffs.Add(new(instance.Name, instance.EffectId, instance.Level, instance.Quality,
                            statId, value * unresolvedFactor, noteKey));
                        continue;
                    }
                    value *= applicableFactor;
                    string key = instance.Name + "\u0000" + statId;
                    if (!merged.TryGetValue(key, out var best) || value > best) merged[key] = value;
                }
        }
        int applied = 0;
        var sink = new SupportModSink();
        foreach (var (key, value) in merged)
        {
            if (value == 0) continue;
            int split = key.IndexOf('\u0000');
            string name = key[..split], statId = key[(split + 1)..];
            // An "archmage_*" stat is collected once from the gems' own static stats (CollectGlobalGemStatics,
            // which is where PoB2 reads a support's constantStats from), so applying it here as well would
            // double Archmage's gain-as-extra-Lightning.
            if (statId.StartsWith("archmage_", StringComparison.Ordinal)) continue;
            // PoB2 resolves a skill's OWN stat id through that skill's statMap, so a skill-specific id (Charge
            // Regulation's "charge_mastery_*_with_*_charges", Blazing Critical's extra-Fire) is translated here.
            // The id-based interpreter keeps the shared vocabulary, but only for the ids that reach a real
            // bucket: its early filter CATALOGUES some ids as extras (anything mentioning a "charge"), and a
            // stat both paths model (Archmage's gain-as-extra) must be owned by exactly one of them.
            bool mapped = StatInterpreter.Maps(statId);
            string why = "";
            bool unknownCondition = false;
            if (!mapped && TranslateBuffStat(bucket, catalog, instances, name, statId, value, conditions, sink, out why,
                out unknownCondition))
            {
                bucket.Extras["Aura:" + name + " " + statId] = value;
                applied++;
                continue;
            }
            if (!mapped)
            {
                // A mod blocked only by a condition this pass cannot answer yet is DEFERRED, not lost: the
                // second pass runs once the pools exist and re-evaluates it (PoB2's two-stage CalcSetup).
                if (unknownCondition)
                {
                    string noteKey = "aura: " + name + " " + statId + " (condition not resolved)";
                    bucket.Note(noteKey);
                    bucket.DeferredBuffs.Add(new(name, instances.FirstOrDefault(i => i.Name == name)?.EffectId ?? "",
                        instances.FirstOrDefault(i => i.Name == name)?.Level ?? 1, 0m, statId, value, noteKey));
                    continue;
                }
                if (!bucket.NoteKnownOr(statId)) bucket.Note("aura: " + name + " " + statId + " (" + why + ")");
                continue;
            }
            StatInterpreter.Apply(bucket, statId, value, null);
            bucket.Extras["Aura:" + name + " " + statId] = value;
            applied++;
        }
        if (instances.Count > 0) bucket.Extras["AuraInstances"] = instances.Count;
        Fold(sink, bucket);
        return applied;
    }

    /// <summary>The SECOND pass of PoB2's CalcSetup order: the pools (and with them the life states) exist now,
    /// so every buff modifier the first pass had to defer is re-evaluated against <c>bucket.Conditions</c> and
    /// applied when its condition holds. A modifier that is still blocked keeps the honesty note the first pass
    /// wrote, so nothing is silently assumed — the note is only dropped for the ones that really were applied.
    /// </summary>
    public static int ApplyDeferred(StatBucket bucket, GameCatalog catalog, BuildConditions? conditions)
    {
        if (bucket.DeferredBuffs.Count == 0) return 0;
        var sink = new SupportModSink();
        int applied = 0;
        foreach (var deferred in bucket.DeferredBuffs)
        {
            if (catalog.SkillData.ForEffect(deferred.EffectId) is not { } skill) continue;
            bool any = false;
            foreach (var effect in skill.Effects)
                foreach (var spec in catalog.SkillData.SpecsFor(deferred.StatId, effect))
                    foreach (var parsed in PoB2ModTranslator.ParseAll(spec.Spec))
                    {
                        if (parsed.Raw.Contains("applyNotPlayer = true", StringComparison.Ordinal)) continue;
                        // The conditions are the whole point of this pass: they are evaluated against the state
                        // the pools produced, and the sink does that evaluation itself.
                        var resolved = parsed with
                        {
                            ConditionVars = [.. parsed.ConditionVars.Where(v => !IsMultiplierCondition(v))]
                        };
                        if (sink.Apply(resolved, deferred.Value * (spec.Mult ?? 1m), isAttack: true, gemTags: [],
                            conditions: bucket.Conditions, out _)) any = true;
                    }
            if (!any) continue;
            bucket.Forget(deferred.NoteKey);
            bucket.Extras["Aura:" + deferred.Name + " " + deferred.StatId] = deferred.Value;
            applied++;
        }
        if (applied > 0) bucket.Extras["Aura:deferredApplied"] = applied;
        Fold(sink, bucket);
        return applied;
    }

    /// <summary>Translates one skill-specific buff stat through the skill's own statMap (the same vocabulary the
    /// support pass uses) and records the resulting mod in <paramref name="sink"/>. Returns false when no spec of
    /// the stat could be parsed — the caller then reports the line instead of inventing an effect.</summary>
    private static bool TranslateBuffStat(StatBucket bucket, GameCatalog catalog, IReadOnlyList<AuraInstance> instances,
        string name, string statId, decimal value, BuildConditions? conditions, SupportModSink sink, out string reason,
        out bool unknownCondition)
    {
        reason = "no buff instance grants it";
        unknownCondition = false;
        foreach (var instance in instances)
        {
            if (!string.Equals(instance.Name, name, StringComparison.Ordinal)) continue;
            var skill = catalog.SkillData.ForEffect(instance.EffectId);
            if (skill is null) { reason = "no pinned skill data for " + instance.EffectId; continue; }
            bool any = false;
            var why = new List<string>();
            foreach (var effect in skill.Effects)
            {
                var specs = catalog.SkillData.SpecsFor(statId, effect);
                if (specs.Count == 0) continue;
                foreach (var spec in specs)
                {
                    // Every mod call of the entry is evaluated on its own tags — one stat can map to several
                    // mods (Elemental Conflux's three per-element damage calls), and a call's own conditions
                    // must not be read from its neighbour's text.
                    foreach (var parsed in PoB2ModTranslator.ParseAll(spec.Spec))
                    {
                        if (parsed.Raw.Contains("applyNotPlayer = true", StringComparison.Ordinal)) continue;
                        if (ConditionOf(parsed.Raw, conditions, bucket) != true) { why.Add("condition holds false"); continue; }
                        // The multiplier is a GATE here: the collection loop already scaled `value` by it, and this checks that
                        // the call's own tag resolves for this build. A zero multiplier means the mod contributes
                        // nothing at all (PoB2 multiplies its value by 0), which is how Elemental Conflux's
                        // unchosen elements drop out when the config names one instead of "Average".
                        if (MultiplierOf(parsed.Raw, conditions, bucket) is not decimal factor)
                        {
                            why.Add("multiplier unknown");
                            continue;
                        }
                        if (factor == 0m) continue;
                        // The multiplier and MultiplierThreshold tags are already evaluated here (and folded into
                        // the value by the collection loop), so they must not reach the sink as conditions — it
                        // would call them unknown and drop the mod. Charge/rage/resonance tags are consumed here too.
                        var resolved = parsed with
                        {
                            ConditionVars = [.. parsed.ConditionVars.Where(v => !IsMultiplierCondition(v))]
                        };
                        // `value` already carries the multiplier factor and `spec.Mult` is PoB2's own sign
                        // multiplier from the data, which applies on top.
                        decimal scaled = value * (spec.Mult ?? 1m);
                        // isAttack stays true so a ModFlag.Attack buff (Blazing Critical) is honoured; the sink
                        // keeps such gains in its attack-only pool, which a spell never reads.
                        if (sink.Apply(resolved, scaled, isAttack: true, gemTags: [], conditions: ToModConditions(conditions),
                            out bool blockedByCondition))
                        {
                            any = true;
                        }
                        else
                        {
                            if (blockedByCondition) unknownCondition = true;
                            why.Add(blockedByCondition
                                ? "condition not resolved"
                                : "mod " + parsed.Name + " " + parsed.Type + " not modelled");
                        }
                    }
                }
            }
            if (any) return true;
            if (why.Count > 0) reason = string.Join("; ", why.Distinct());
        }
        return false;
    }

    /// <summary>Whether a parsed condition symbol is one of the multiplier tags the aura pass resolves itself
    /// (its own count arithmetic), rather than a build condition the sink must evaluate.</summary>
    private static bool IsMultiplierCondition(string symbol) =>
        symbol.StartsWith("Multiplier:", StringComparison.Ordinal) ||
        symbol.StartsWith("MultiplierThreshold:", StringComparison.Ordinal) ||
        symbol.StartsWith("StatThreshold:", StringComparison.Ordinal);

    /// <summary>Folds the translated buff mods into the character. They come from GlobalEffects, so they apply to
    /// every skill (like PoB2's own character-wide mod list) — the game-wide MOREs, the increases and the
    /// defence multipliers each go to the bucket the rest of the calculator already reads.</summary>
    private static void Fold(SupportModSink sink, StatBucket bucket)
    {
        if (sink.DamageInc != 0) bucket.DamageInc += sink.DamageInc;
        if (sink.AttackDamageInc != 0) bucket.AttackDamageInc += sink.AttackDamageInc;
        if (sink.AttackDamageMore != 1m) bucket.AttackDamageMoreFactor *= sink.AttackDamageMore;
        if (sink.DamageMore != 1m) bucket.DamageMorePct += (sink.DamageMore - 1m) * 100m;
        // PoB2's `Speed` mod feeds the attack rate, the cast rate and skill speed alike, which is exactly how this
        // engine's SkillSpeedInc is consumed (both rate formulas add it).
        if (sink.RateInc != 0) bucket.SkillSpeedInc += sink.RateInc;
        if (sink.RateMore != 1m)
        {
            bucket.AttackSpeedMorePct += (sink.RateMore - 1m) * 100m;
            bucket.CastSpeedMorePct += (sink.RateMore - 1m) * 100m;
        }
        if (sink.CritChanceInc != 0) bucket.CritChanceInc += sink.CritChanceInc;
        if (sink.CritChanceMore != 1m) bucket.CritChanceMorePct += (sink.CritChanceMore - 1m) * 100m;
        if (sink.CritMultiplierInc != 0) bucket.CritBonusInc += sink.CritMultiplierInc;
        if (sink.CritMultiplierMore != 1m) bucket.CritBonusMorePct += (sink.CritMultiplierMore - 1m) * 100m;
        if (sink.ArmourInc != 0) bucket.ArmourInc += sink.ArmourInc;
        if (sink.ArmourMore != 1m) bucket.ArmourMorePct += (sink.ArmourMore - 1m) * 100m;
        if (sink.EvasionInc != 0) bucket.EvInc += sink.EvasionInc;
        if (sink.EvasionMore != 1m) bucket.EvMorePct += (sink.EvasionMore - 1m) * 100m;
        if (sink.EnergyShieldInc != 0) bucket.EsInc += sink.EnergyShieldInc;
        if (sink.EnergyShieldMore != 1m) bucket.EsMorePct += (sink.EnergyShieldMore - 1m) * 100m;
        foreach (var (type, more) in sink.TypeMore)
            if (more != 1m)
                bucket.TypeMorePct[type] = bucket.TypeMorePct.TryGetValue(type, out var oldMore)
                    ? oldMore + (more - 1m) * 100m : (more - 1m) * 100m;
        foreach (var (type, inc) in sink.TypeInc)
            if (inc != 0) bucket.TypeIncPct[type] = bucket.TypeIncPct.TryGetValue(type, out var oldInc) ? oldInc + inc : inc;
        if (sink.RageEffectInc != 0) bucket.RageEffectInc += sink.RageEffectInc;
        if (sink.MaximumRageFlat != 0) bucket.MaximumRageFlat += sink.MaximumRageFlat;
        if (sink.BarrageRepeats != 0) bucket.BarrageRepeats += sink.BarrageRepeats;
        if (sink.BarrageRepeatDamageMore != 1m) bucket.BarrageRepeatDamageMore *= sink.BarrageRepeatDamageMore;
        // Both pools: a gain gated on ModFlag.Attack lives in the sink's attack-only dictionary (Blazing Critical's
        // +15% Fire for attacks), so iterating only the general one would silently drop it.
        foreach (var (type, gain) in sink.GainAs)
            if (gain != 0) bucket.GainAs[type] = bucket.GainAs.TryGetValue(type, out var oldGain) ? oldGain + gain : gain;
        foreach (var (type, gain) in sink.AttackOnlyGainAs)
            if (gain != 0)
                bucket.AttackOnlyGainAs[type] = bucket.AttackOnlyGainAs.TryGetValue(type, out var oldAttackGain)
                    ? oldAttackGain + gain : gain;
    }
}
