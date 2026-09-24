namespace PoeBuilder.Core.Calculation;

/// <summary>
/// PoE2 damaging-ailment DoT evaluated from a hit. Port of Path of Building 2
/// (CalcOffence.lua — calcDamagingAilmentOutputs / calcAilmentDamage). The per-second base
/// percentages and base durations come from the pinned PoB2 game data (Data/Misc.lua):
/// Bleed 900%/min = 15%/s for 5s, Ignite 1200%/min = 20%/s for 4s, Poison 1200%/min = 20%/s for 2s.
/// The hit is the raw (unmitigated) split; enemy resistance and configurable stacks stay outside
/// this v1 contract and are reported, never invented.
/// </summary>
public sealed record AilmentDotResult(
    string Ailment,
    string DotType,
    decimal SourceChancePercent,
    decimal DamagePerSecond,
    decimal SustainedDamagePerSecond,
    decimal DurationSeconds,
    decimal TotalDamagePerApplication,
    string[] Breakdown);

public static class AilmentDotCalculator
{
    public const decimal BleedPercentPerSecond = 15m;
    public const decimal IgnitePercentPerSecond = 20m;
    public const decimal PoisonPercentPerSecond = 20m;
    public const decimal BleedDurationBaseSeconds = 5m;
    public const decimal IgniteDurationBaseSeconds = 4m;
    public const decimal PoisonDurationBaseSeconds = 2m;

    private static readonly Dictionary<string, (string DotType, decimal PercentPerSecond, decimal DurationBase)> Ailments = new()
    {
        ["Bleed"] = ("Physical", BleedPercentPerSecond, BleedDurationBaseSeconds),
        ["Ignite"] = ("Fire", IgnitePercentPerSecond, IgniteDurationBaseSeconds),
        ["Poison"] = ("Chaos", PoisonPercentPerSecond, PoisonDurationBaseSeconds),
    };

    /// <summary>Dot damage type of an ailment ("Poison → Chaos", "Ignite → Fire", "Bleed → Physical").</summary>
    public static string DotTypeOf(string ailment) => Ailments[ailment].DotType;

    /// <summary>
    /// Evaluates one damaging ailment from a hit. <paramref name="sourceHit"/> is the average
    /// unmitigated hit of all damage types the ailment scales from (Ignite: Fire; Poison:
    /// Physical+Chaos; Bleed: Physical); <paramref name="sourceCritHit"/> is the same value
    /// multiplied by the crit damage bonus. The result follows PoB's weighted calc: each ailment
    /// instance is a mix of crit and non-crit sources weighted by crit chance and the ailment's
    /// chance on hit versus chance on crit. <paramref name="hitsPerSecond"/> and
    /// <paramref name="hitChancePercent"/> fold the average concurrent ailment count into
    /// <see cref="AilmentDotResult.SustainedDamagePerSecond"/> (capped at one stack, the PoE2
    /// default; stacking ailments must scale this externally).
    /// </summary>
    public static AilmentDotResult? Evaluate(
        string ailment,
        decimal sourceHit,
        decimal sourceCritHit,
        decimal critChancePercent,
        decimal chanceOnHitPercent,
        decimal chanceOnCritPercent,
        decimal increasedPercent = 0,
        decimal moreMultiplier = 1,
        decimal dotMultiplierPercent = 0,
        decimal durationIncreasedPercent = 0,
        decimal ailmentMagnitudeEffect = 1,
        decimal hitsPerSecond = 0,
        decimal hitChancePercent = 100)
    {
        if (!Ailments.TryGetValue(ailment, out var spec) || sourceHit <= 0) return null;
        decimal chanceOnHit = Math.Clamp(chanceOnHitPercent, 0, 100) / 100m;
        decimal chanceOnCrit = Math.Clamp(chanceOnCritPercent, 0, 100) / 100m;
        decimal crit = Math.Clamp(critChancePercent, 0, 100) / 100m;
        // PoB calcAilmentDamage: chance a present ailment instance came from a crit.
        decimal applyChance = chanceOnHit * (1 - crit) + chanceOnCrit * crit;
        if (applyChance <= 0 || chanceOnHit + chanceOnCrit <= 0) return null;
        if (applyChance > 1) applyChance = 1;
        decimal avgFromHit = sourceHit * (chanceOnHit * (1 - crit) / applyChance);
        decimal avgFromCrit = sourceCritHit * (chanceOnCrit * crit / applyChance);
        decimal weightedSource = avgFromHit + avgFromCrit;
        decimal dps = weightedSource * spec.PercentPerSecond / 100m
            * (1 + increasedPercent / 100m) * moreMultiplier
            * (1 + dotMultiplierPercent / 100m) * Math.Max(0, ailmentMagnitudeEffect);
        decimal duration = spec.DurationBase * (1 + Math.Max(-100, durationIncreasedPercent) / 100m);
        decimal total = dps * duration;
        // Average concurrent instances, capped by the PoE2 default of one stack (PoB ailmentStacks).
        decimal hitChance = Math.Clamp(hitChancePercent, 0, 100) / 100m;
        decimal stacks = hitsPerSecond > 0
            ? hitChance * applyChance * duration * hitsPerSecond
            : hitChance * applyChance;
        decimal sustained = dps * Math.Min(1, Math.Max(0, stacks));
        var lines = new List<string>
        {
            ailment + " source: " + Dmg(sourceHit) + " " + spec.DotType.ToLowerInvariant(),
            "Crit source: " + Dmg(sourceCritHit),
            "Chance: " + Round0(chanceOnHitPercent) + "% on hit / " + Round0(chanceOnCritPercent) + "% on crit",
            "Deals " + Round0(spec.PercentPerSecond) + "% per second = " + Dmg(dps) + "/s",
            "Duration: " + Round1(duration) + "s",
            "Total " + ailment + " per application: " + Dmg(total),
            "Sustained DoT: " + Dmg(sustained) + "/s"
        };
        return new(ailment, spec.DotType, Math.Min(100, (chanceOnHit * (1 - crit) + chanceOnCrit * crit) * 100), dps, sustained, duration, total, lines.ToArray());
    }

    /// <summary>Combined increased/reduced modifiers that scale an ailment's DoT in the current model:
    /// the dot-type increased damage (which already folds in generic damage/attack/spell/ele increases)
    /// plus the ailment-specific buckets.</summary>
    public static decimal IncreasedFor(string ailment, decimal dotTypeIncreased, StatBucket bucket)
    {
        decimal extra = bucket.DotInc;
        extra += ailment switch
        {
            "Ignite" => bucket.BurningInc,
            "Poison" => bucket.PoisonInc,
            "Bleed" => bucket.BleedInc,
            _ => 0
        };
        return dotTypeIncreased + extra;
    }

    private static string Dmg(decimal value) => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    private static string Round1(decimal value) => decimal.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    private static string Round0(decimal value) => decimal.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
}
